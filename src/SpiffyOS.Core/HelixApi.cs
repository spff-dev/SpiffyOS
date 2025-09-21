using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace SpiffyOS.Core;

public sealed class HelixApi
{
    private readonly HttpClient _http;
    private readonly TwitchAuth _broadcasterAuth; // SPIFFgg
    private readonly TwitchAuth _botAuth;         // SpiffyOS
    private readonly string _clientId;
    private readonly AppTokenProvider _appToken;

    public HelixApi(HttpClient http, TwitchAuth broadcasterAuth, TwitchAuth botAuth, string clientId, AppTokenProvider appToken)
    {
        _http = http;
        _broadcasterAuth = broadcasterAuth;
        _botAuth = botAuth;
        _clientId = clientId;
        _appToken = appToken;
    }

    // ---------- helpers ----------
    private async Task<HttpRequestMessage> NewReqAsync(HttpMethod method, string url, string which, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, url);
        switch (which)
        {
            case "app":
                var t = await _appToken.GetAsync(ct);
                req.Headers.Add("Client-Id", _clientId);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", t);
                break;
            case "bot":
                await _botAuth.EnsureValidAsync(ct);
                _botAuth.ApplyAuth(req);
                break;
            case "broadcaster":
                await _broadcasterAuth.EnsureValidAsync(ct);
                _broadcasterAuth.ApplyAuth(req);
                break;
        }
        return req;
    }

    private static JsonSerializerOptions JsonOpts => new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

    // ---------- chat ----------
    public async Task SendChatMessageWithAppAsync(string broadcasterId, string senderUserId, string text, CancellationToken ct, string? replyParentMessageId = null)
    {
        var req = await NewReqAsync(HttpMethod.Post, "https://api.twitch.tv/helix/chat/messages", "app", ct);
        var body = new Dictionary<string, object?>
        {
            ["broadcaster_id"] = broadcasterId,
            ["sender_id"] = senderUserId,
            ["message"] = text
        };
        if (!string.IsNullOrEmpty(replyParentMessageId))
            body["reply_parent_message_id"] = replyParentMessageId;
        req.Content = JsonContent.Create(body);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    // ---------- stream info ----------
    public sealed class StreamInfo
    {
        public List<Item> data { get; set; } = new();
        public sealed class Item
        {
            public string id { get; set; } = "";
            public string user_id { get; set; } = "";
            public string type { get; set; } = "";
            public DateTime started_at { get; set; } // RFC3339 → DateTime
        }
    }

    public async Task<StreamInfo> GetStreamAsync(string broadcasterId, CancellationToken ct)
    {
        var req = await NewReqAsync(HttpMethod.Get, $"https://api.twitch.tv/helix/streams?user_id={broadcasterId}", "app", ct);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<StreamInfo>(json, JsonOpts)!;
    }

    public async Task<bool> IsLiveAsync(string broadcasterId, CancellationToken ct)
    {
        var s = await GetStreamAsync(broadcasterId, ct);
        return s.data.FirstOrDefault()?.type?.Equals("live", StringComparison.OrdinalIgnoreCase) == true;
    }

    // ---------- title / category (broadcaster token) ----------
    public async Task SetTitleAsync(string broadcasterId, string newTitle, CancellationToken ct)
    {
        var req = await NewReqAsync(HttpMethod.Patch, "https://api.twitch.tv/helix/channels", "broadcaster", ct);
        var payload = new { broadcaster_id = broadcasterId, title = newTitle };
        req.Content = JsonContent.Create(payload);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task SetGameAsync(string broadcasterId, string categoryId, CancellationToken ct)
    {
        var req = await NewReqAsync(HttpMethod.Patch, "https://api.twitch.tv/helix/channels", "broadcaster", ct);
        var payload = new { broadcaster_id = broadcasterId, game_id = categoryId };
        req.Content = JsonContent.Create(payload);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    public sealed class ChannelsResp
    {
        public List<Chan> data { get; set; } = new();
        public sealed class Chan
        {
            public string broadcaster_id { get; set; } = "";
            public string title { get; set; } = "";
            public string game_id { get; set; } = "";
            public string game_name { get; set; } = "";
        }
    }

    public async Task<ChannelsResp> GetChannelsAsync(string broadcasterId, CancellationToken ct)
    {
        var req = await NewReqAsync(HttpMethod.Get, $"https://api.twitch.tv/helix/channels?broadcaster_id={broadcasterId}", "app", ct);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ChannelsResp>(json, JsonOpts)!;
    }

    // ---------- users ----------
    public sealed class UsersResp
    {
        public List<User> data { get; set; } = new();
        public sealed class User
        {
            public string id { get; set; } = "";
            public string login { get; set; } = "";
            public string display_name { get; set; } = "";
        }
    }

    public async Task<UsersResp> GetUsersByLoginAsync(string login, CancellationToken ct)
    {
        var req = await NewReqAsync(HttpMethod.Get, $"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(login)}", "app", ct);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<UsersResp>(json, JsonOpts)!;
    }

    // ---------- shoutouts & announcements (BOT token) ----------
    public async Task ShoutoutAsync(string fromBroadcasterId, string toBroadcasterId, string moderatorUserId, CancellationToken ct)
    {
        // Requires bot token with moderator:manage:shoutouts and bot must be a moderator in the channel
        var url = $"https://api.twitch.tv/helix/chat/shoutouts?from_broadcaster_id={fromBroadcasterId}&to_broadcaster_id={toBroadcasterId}&moderator_id={moderatorUserId}";
        var req = await NewReqAsync(HttpMethod.Post, url, "bot", ct);
        using var res = await _http.SendAsync(req, ct);
        // 204 on success, 429 on cooldown, 401 if wrong token
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Shoutout failed: {(int)res.StatusCode} {res.ReasonPhrase} {body}");
        }
    }

    public async Task SendAnnouncementAsync(string broadcasterId, string moderatorUserId, string message, string color, CancellationToken ct)
    {
        var url = $"https://api.twitch.tv/helix/chat/announcements?broadcaster_id={broadcasterId}&moderator_id={moderatorUserId}";
        var req = await NewReqAsync(HttpMethod.Post, url, "bot", ct);
        req.Content = JsonContent.Create(new { message, color });
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode(); // 204 on success
    }
}
