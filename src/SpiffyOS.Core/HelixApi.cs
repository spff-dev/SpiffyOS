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

    // ============ helpers ============
    private async Task<HttpRequestMessage> NewReqAsync(HttpMethod method, string url, string which, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, url);
        switch (which)
        {
            case "app":
                {
                    var t = await _appToken.GetAsync(ct);
                    req.Headers.Add("Client-Id", _clientId);
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", t);
                    break;
                }
            case "bot":
                {
                    await _botAuth.EnsureValidAsync(ct);
                    _botAuth.ApplyAuth(req);
                    break;
                }
            case "broadcaster":
                {
                    await _broadcasterAuth.EnsureValidAsync(ct);
                    _broadcasterAuth.ApplyAuth(req);
                    break;
                }
        }
        return req;
    }

    private static JsonSerializerOptions JsonOpts => new() { PropertyNameCaseInsensitive = true };

    // ============ models ============
    public sealed class StreamInfo
    {
        public List<Item> data { get; set; } = new();
        public sealed class Item
        {
            public string id { get; set; } = "";
            public string user_id { get; set; } = "";
            public string type { get; set; } = "";
            public string game_name { get; set; } = "";
            public DateTime started_at { get; set; } // RFC3339
        }
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

    public sealed class UsersResp
    {
        public List<User> data { get; set; } = new();
        public sealed class User
        {
            public string id { get; set; } = "";
            public string login { get; set; } = "";
            public string display_name { get; set; } = "";
            public string created_at { get; set; } = "";
        }
    }

    public sealed class SearchCategoriesResp
    {
        public List<Category> data { get; set; } = new();
        public sealed class Category
        {
            public string id { get; set; } = "";
            public string name { get; set; } = "";
        }
    }

    public sealed class GamesResp
    {
        public List<Game> data { get; set; } = new();
        public sealed class Game
        {
            public string id { get; set; } = "";
            public string name { get; set; } = "";
        }
    }

    // Simple game record the handlers can use
    public sealed class Game
    {
        public string id { get; init; } = "";
        public string name { get; init; } = "";
    }

    // Clip handling
    public async Task<string?> CreateClipAsync(string broadcasterId, CancellationToken ct, bool hasDelay = false)
    {
        // Requires broadcaster user token with clips:edit (your broadcaster token already has it)
        await _broadcasterAuth.EnsureValidAsync(ct);

        var url = $"https://api.twitch.tv/helix/clips?broadcaster_id={Uri.EscapeDataString(broadcasterId)}&has_delay={(hasDelay ? "true" : "false")}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        _broadcasterAuth.ApplyAuth(req); // sets Authorization + Client-Id

        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        using var stream = await res.Content.ReadAsStreamAsync(ct);
        var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        try
        {
            var data = root.GetProperty("data");
            if (data.ValueKind == System.Text.Json.JsonValueKind.Array && data.GetArrayLength() > 0)
            {
                var first = data[0];
                var id = first.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                return string.IsNullOrWhiteSpace(id) ? null : id;
            }
        }
        catch { /* fallthrough */ }

        return null;
    }


    // ============ chat send ============
    public async Task SendChatMessageWithAppAsync(string broadcasterId, string senderUserId, string text, CancellationToken ct, string? replyParentMessageId = null)
    {
        var req = await NewReqAsync(HttpMethod.Post, "https://api.twitch.tv/helix/chat/messages", "app", ct);
        var body = new Dictionary<string, object?>
        {
            ["broadcaster_id"] = broadcasterId,
            ["sender_id"] = senderUserId,
            ["message"] = text
        };
        if (!string.IsNullOrWhiteSpace(replyParentMessageId))
            body["reply_parent_message_id"] = replyParentMessageId;

        req.Content = JsonContent.Create(body);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    // ============ reads ============
    public async Task<StreamInfo> GetStreamAsync(string broadcasterId, CancellationToken ct)
    {
        var req = await NewReqAsync(HttpMethod.Get, $"https://api.twitch.tv/helix/streams?user_id={broadcasterId}", "app", ct);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<StreamInfo>(json, JsonOpts)!;
    }

    // New wrapper your handlers can call
    public async Task<DateTime?> GetUptimeAsync(string broadcasterId, CancellationToken ct)
    {
        var s = await GetStreamAsync(broadcasterId, ct);
        var item = s.data.FirstOrDefault();
        if (item is null || !string.Equals(item.type, "live", StringComparison.OrdinalIgnoreCase))
            return null;
        // stream started_at is UTC already per Twitch docs
        return item.started_at;
    }

    public async Task<bool> IsLiveAsync(string broadcasterId, CancellationToken ct)
        => (await GetUptimeAsync(broadcasterId, ct)) is not null;

    public async Task<ChannelsResp> GetChannelsAsync(string broadcasterId, CancellationToken ct)
    {
        var req = await NewReqAsync(HttpMethod.Get, $"https://api.twitch.tv/helix/channels?broadcaster_id={broadcasterId}", "app", ct);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ChannelsResp>(json, JsonOpts)!;
    }

    // Handlers expect a single record
    public async Task<ChannelsResp.Chan?> GetChannelInfoAsync(string broadcasterId, CancellationToken ct)
        => (await GetChannelsAsync(broadcasterId, ct)).data.FirstOrDefault();

    public async Task<UsersResp> GetUsersByLoginAsync(string login, CancellationToken ct)
    {
        var req = await NewReqAsync(HttpMethod.Get, $"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(login)}", "app", ct);
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<UsersResp>(json, JsonOpts)!;
    }

    // Handlers expect a single user
    public async Task<UsersResp.User?> GetUserByLoginAsync(string login, CancellationToken ct)
        => (await GetUsersByLoginAsync(login, ct)).data.FirstOrDefault();

    // ============ title/category (write) ============
    public async Task UpdateTitleAsync(string broadcasterId, string newTitle, CancellationToken ct)
    {
        var req = await NewReqAsync(HttpMethod.Patch, "https://api.twitch.tv/helix/channels", "broadcaster", ct);
        req.Content = JsonContent.Create(new { broadcaster_id = broadcasterId, title = newTitle });
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task UpdateGameAsync(string broadcasterId, string categoryId, CancellationToken ct)
    {
        var req = await NewReqAsync(HttpMethod.Patch, "https://api.twitch.tv/helix/channels", "broadcaster", ct);
        req.Content = JsonContent.Create(new { broadcaster_id = broadcasterId, game_id = categoryId });
        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    // Find a game/category by name (fuzzy first, then exact fallback)
    public async Task<Game?> FindGameAsync(string query, CancellationToken ct)
    {
        // 1) search/categories (fuzzy)
        {
            var req = await NewReqAsync(HttpMethod.Get, $"https://api.twitch.tv/helix/search/categories?query={Uri.EscapeDataString(query)}", "app", ct);
            using var res = await _http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
            {
                var json = await res.Content.ReadAsStringAsync(ct);
                var found = JsonSerializer.Deserialize<SearchCategoriesResp>(json, JsonOpts)!;
                var first = found.data.FirstOrDefault();
                if (first is not null) return new Game { id = first.id, name = first.name };
            }
        }
        // 2) games?name= (exactish)
        {
            var req = await NewReqAsync(HttpMethod.Get, $"https://api.twitch.tv/helix/games?name={Uri.EscapeDataString(query)}", "app", ct);
            using var res = await _http.SendAsync(req, ct);
            if (res.IsSuccessStatusCode)
            {
                var json = await res.Content.ReadAsStringAsync(ct);
                var found = JsonSerializer.Deserialize<GamesResp>(json, JsonOpts)!;
                var first = found.data.FirstOrDefault();
                if (first is not null) return new Game { id = first.id, name = first.name };
            }
        }
        return null;
    }

    // ============ shoutouts & announcements (bot token) ============
    public async Task ShoutoutAsync(string fromBroadcasterId, string toBroadcasterId, string moderatorUserId, CancellationToken ct)
    {
        var url = $"https://api.twitch.tv/helix/chat/shoutouts?from_broadcaster_id={fromBroadcasterId}&to_broadcaster_id={toBroadcasterId}&moderator_id={moderatorUserId}";
        var req = await NewReqAsync(HttpMethod.Post, url, "bot", ct);
        using var res = await _http.SendAsync(req, ct);
        // 204 on success, 429 on cooldown, 401 if wrong token/role
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Shoutout failed: {(int)res.StatusCode} {res.ReasonPhrase} {body}");
        }
    }
    public async Task<DateTime?> GetFollowSinceAsync(string broadcasterId, string userId, string moderatorId, CancellationToken ct)
    {
        // Requires bot to have scope: moderator:read:followers
        await _botAuth.EnsureValidAsync(ct);

        var url = $"https://api.twitch.tv/helix/channels/followers?broadcaster_id={broadcasterId}&user_id={userId}&moderator_id={moderatorId}";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        _botAuth.ApplyAuth(req);

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;

        using var doc = await System.Text.Json.JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != System.Text.Json.JsonValueKind.Array) return null;

        foreach (var item in data.EnumerateArray())
        {
            if (item.TryGetProperty("followed_at", out var fa) && fa.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                if (DateTime.TryParse(fa.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
                    return dt.ToUniversalTime();
            }
        }
        return null;
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
