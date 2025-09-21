using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace SpiffyOS.Core;

/// <summary>
/// Helix helpers for SpiffyOS.
///
/// Tokens used:
/// - App token: /streams, /games, /users
/// - Bot user token: /chat/messages (already used), /chat/announcements, /chat/shoutouts
/// - Broadcaster user token: PATCH/GET /channels (title/category)
///
/// Existing callers:
///   - GetStreamAsync(...) shape: StreamsResponse with .data[]
///   - GetUptimeAsync(...)
///   - SendChatMessageWithAppAsync(...)
/// </summary>
public sealed class HelixApi
{
    private readonly HttpClient _http;
    private readonly TwitchAuth _broadcasterAuth;
    private readonly TwitchAuth _botAuth;
    private readonly AppTokenProvider _app;
    private readonly string _clientId;

    public HelixApi(HttpClient http, TwitchAuth broadcasterAuth, TwitchAuth botAuth, string clientId, AppTokenProvider app)
    {
        _http = http;
        _broadcasterAuth = broadcasterAuth;
        _botAuth = botAuth;
        _clientId = clientId;
        _app = app;
    }

    // ---------------- Chat (App token) ----------------

    public async Task SendChatMessageWithAppAsync(
        string broadcasterId,
        string senderUserId,
        string message,
        CancellationToken ct,
        string? replyParentMessageId = null)
    {
        var appToken = await _app.GetAsync(ct);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.twitch.tv/helix/chat/messages");
        object body = string.IsNullOrWhiteSpace(replyParentMessageId)
            ? new { broadcaster_id = broadcasterId, sender_id = senderUserId, message }
            : new { broadcaster_id = broadcasterId, sender_id = senderUserId, message, reply_parent_message_id = replyParentMessageId };

        req.Content = JsonContent.Create(body);
        _app.Apply(req, appToken);

        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    public Task SendChatMessageAsync(
        string broadcasterId,
        string senderUserId,
        string message,
        CancellationToken ct,
        string? replyParentMessageId = null)
        => SendChatMessageWithAppAsync(broadcasterId, senderUserId, message, ct, replyParentMessageId);

    // ---------------- Streams / status (App token) ----------------

    public async Task<bool> IsLiveAsync(string broadcasterId, CancellationToken ct)
    {
        var appToken = await _app.GetAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/streams?user_id={broadcasterId}");
        _app.Apply(req, appToken);

        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            return data.GetArrayLength() > 0;
        return false;
    }

    public async Task<TimeSpan?> GetUptimeAsync(string broadcasterId, CancellationToken ct)
    {
        var resp = await GetStreamAsync(broadcasterId, ct);
        if (resp is null || resp.data is null || resp.data.Count == 0) return null;

        var startedUtc = resp.data[0].started_at;
        if (startedUtc.Kind != DateTimeKind.Utc)
            startedUtc = DateTime.SpecifyKind(startedUtc, DateTimeKind.Utc);

        return DateTime.UtcNow - startedUtc;
    }

    public async Task<StreamsResponse?> GetStreamAsync(string broadcasterId, CancellationToken ct)
    {
        var appToken = await _app.GetAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/streams?user_id={broadcasterId}");
        _app.Apply(req, appToken);

        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadAsStringAsync(ct);
        var resp = System.Text.Json.JsonSerializer.Deserialize<StreamsResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return resp;
    }

    // ---------------- Channels (title / category) ----------------

    public async Task<ChannelInfo?> GetChannelInfoAsync(string broadcasterId, CancellationToken ct)
    {
        // Try app token first; if Twitch rejects, fall back to broadcaster token.
        async Task<ChannelInfo?> usingToken(Func<HttpRequestMessage> build, Action<HttpRequestMessage> apply)
        {
            using var req = build();
            apply(req);
            using var res = await _http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                var code = (int)res.StatusCode;
                if (code == 401 || code == 403) return null; // try other token
                res.EnsureSuccessStatusCode();
            }
            var json = await res.Content.ReadAsStringAsync(ct);
            var obj = JsonSerializer.Deserialize<ChannelsResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return obj?.data?.FirstOrDefault();
        }

        var appToken = await _app.GetAsync(ct);
        var withApp = await usingToken(
            () => new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/channels?broadcaster_id={broadcasterId}"),
            req => _app.Apply(req, appToken)
        );
        if (withApp is not null) return withApp;

        await _broadcasterAuth.EnsureValidAsync(ct);
        return await usingToken(
            () => new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/channels?broadcaster_id={broadcasterId}"),
            req => { _broadcasterAuth.ApplyAuth(req); req.Headers.Add("Client-Id", _clientId); }
        );
    }

    public async Task<bool> UpdateTitleAsync(string broadcasterId, string newTitle, CancellationToken ct)
    {
        await _broadcasterAuth.EnsureValidAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"https://api.twitch.tv/helix/channels?broadcaster_id={broadcasterId}")
        {
            Content = JsonContent.Create(new { title = newTitle })
        };
        _broadcasterAuth.ApplyAuth(req);
        req.Headers.Add("Client-Id", _clientId);

        using var res = await _http.SendAsync(req, ct);
        return res.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateGameAsync(string broadcasterId, string gameId, CancellationToken ct)
    {
        await _broadcasterAuth.EnsureValidAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"https://api.twitch.tv/helix/channels?broadcaster_id={broadcasterId}")
        {
            Content = JsonContent.Create(new { game_id = gameId })
        };
        _broadcasterAuth.ApplyAuth(req);
        req.Headers.Add("Client-Id", _clientId);

        using var res = await _http.SendAsync(req, ct);
        return res.IsSuccessStatusCode;
    }

    // ---------------- Users / Games lookups ----------------

    public async Task<UserInfo?> GetUserByLoginAsync(string login, CancellationToken ct)
    {
        var appToken = await _app.GetAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/users?login={Uri.EscapeDataString(login)}");
        _app.Apply(req, appToken);

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;

        var json = await res.Content.ReadAsStringAsync(ct);
        var obj = JsonSerializer.Deserialize<UsersResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return obj?.data?.FirstOrDefault();
    }

    public async Task<(string id, string name)?> FindGameAsync(string name, CancellationToken ct)
    {
        var appToken = await _app.GetAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/games?name={Uri.EscapeDataString(name)}");
        _app.Apply(req, appToken);

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) return null;

        var json = await res.Content.ReadAsStringAsync(ct);
        var obj = JsonSerializer.Deserialize<GamesResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var list = obj?.data ?? new List<GameInfo>();
        if (list.Count == 0) return null;

        // Prefer exact (case-insensitive) match
        var exact = list.FirstOrDefault(g => string.Equals(g.name, name, StringComparison.OrdinalIgnoreCase));
        var pick = exact ?? list[0];
        return (pick.id, pick.name);
    }

    // ---------------- Chat: announcements / shoutouts (Bot token) ----------------

    public async Task<bool> SendAnnouncementAsync(string broadcasterId, string moderatorId, string message, string color, CancellationToken ct)
    {
        await _botAuth.EnsureValidAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.twitch.tv/helix/chat/announcements?broadcaster_id={broadcasterId}&moderator_id={moderatorId}");
        _botAuth.ApplyAuth(req);
        req.Headers.Add("Client-Id", _clientId);
        req.Content = JsonContent.Create(new { message, color });

        using var res = await _http.SendAsync(req, ct);
        return res.IsSuccessStatusCode;
    }

    public async Task<bool> ShoutoutAsync(string fromBroadcasterId, string toBroadcasterId, string moderatorId, CancellationToken ct)
    {
        await _botAuth.EnsureValidAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.twitch.tv/helix/chat/shoutouts?from_broadcaster_id={fromBroadcasterId}&to_broadcaster_id={toBroadcasterId}&moderator_id={moderatorId}");
        _botAuth.ApplyAuth(req);
        req.Headers.Add("Client-Id", _clientId);

        using var res = await _http.SendAsync(req, ct);
        // Even if this fails (cooldown), caller may still do an /announcement
        return res.IsSuccessStatusCode;
    }

    // ---------------- Models ----------------

    public sealed class StreamsResponse
    {
        public List<StreamItem> data { get; set; } = new();
        public Pagination? pagination { get; set; }
    }

    public sealed class StreamItem
    {
        public string id { get; set; } = "";
        public string user_id { get; set; } = "";
        public string user_login { get; set; } = "";
        public string user_name { get; set; } = "";
        public string game_id { get; set; } = "";
        public string game_name { get; set; } = ""; // add for shoutout text
        public string title { get; set; } = "";
        public DateTime started_at { get; set; } // ISO8601 UTC -> DateTime
    }

    public sealed class Pagination { public string? cursor { get; set; } }

    public sealed class ChannelsResponse { public List<ChannelInfo> data { get; set; } = new(); }

    public sealed class ChannelInfo
    {
        public string broadcaster_id { get; set; } = "";
        public string broadcaster_login { get; set; } = "";
        public string broadcaster_name { get; set; } = "";
        public string game_id { get; set; } = "";
        public string game_name { get; set; } = "";
        public string title { get; set; } = "";
    }

    public sealed class UsersResponse { public List<UserInfo> data { get; set; } = new(); }
    public sealed class UserInfo { public string id { get; set; } = ""; public string login { get; set; } = ""; public string display_name { get; set; } = ""; }

    public sealed class GamesResponse { public List<GameInfo> data { get; set; } = new(); }
    public sealed class GameInfo { public string id { get; set; } = ""; public string name { get; set; } = ""; }
}
