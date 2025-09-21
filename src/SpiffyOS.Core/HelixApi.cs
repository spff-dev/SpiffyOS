using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace SpiffyOS.Core;

/// <summary>
/// Helix helpers for SpiffyOS.
/// - Uses Broadcaster user token for read endpoints (e.g., /streams)
/// - Uses App token for Send Chat Message (/chat/messages)
/// 
/// Methods exposed:
///   SendChatMessageWithAppAsync(...)
///   SendChatMessageAsync(...)              // wrapper for compatibility, calls app-token path
///   IsLiveAsync(...)
///   GetUptimeAsync(...)
///   GetStreamAsync(...) -> StreamInfo
/// </summary>
public sealed class HelixApi
{
    private readonly HttpClient _http;
    private readonly TwitchAuth _broadcasterAuth;
    private readonly AppTokenProvider _app;
    private readonly string _clientId;

    public HelixApi(HttpClient http, TwitchAuth broadcasterAuth, string clientId, AppTokenProvider app)
    {
        _http = http;
        _broadcasterAuth = broadcasterAuth;
        _clientId = clientId;
        _app = app;
    }

    // ---------------- Chat (App token) ----------------

    /// <summary>
    /// Send a chat message using the **App Access Token** via Helix Send Chat Message API.
    /// Requires: the bot user (sender_id) is joined to chat and has user:bot + user:write:chat on the app.
    /// </summary>
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

        // Apply Authorization: Bearer <app> and Client-Id
        _app.Apply(req, appToken);

        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Compatibility wrapper. Historically callers used a "user-token" method.
    /// We route to the app-token Send Chat Message endpoint to keep behavior consistent.
    /// </summary>
    public Task SendChatMessageAsync(
        string broadcasterId,
        string senderUserId,
        string message,
        CancellationToken ct,
        string? replyParentMessageId = null)
        => SendChatMessageWithAppAsync(broadcasterId, senderUserId, message, ct, replyParentMessageId);

    // ---------------- Streams / status (Broadcaster token) ----------------

    /// <summary>True if the channel is currently live.</summary>
    public async Task<bool> IsLiveAsync(string broadcasterId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/streams?user_id={broadcasterId}");
        _broadcasterAuth.ApplyAuth(req);
        req.Headers.Add("Client-Id", _clientId);

        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            return data.GetArrayLength() > 0;
        return false;
    }

    /// <summary>Uptime if live; null if offline.</summary>
    public async Task<TimeSpan?> GetUptimeAsync(string broadcasterId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/streams?user_id={broadcasterId}");
        _broadcasterAuth.ApplyAuth(req);
        req.Headers.Add("Client-Id", _clientId);

        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return null;

        var startedAt = data[0].GetProperty("started_at").GetString();
        if (DateTime.TryParse(startedAt, out var startedUtc))
        {
            if (startedUtc.Kind != DateTimeKind.Utc)
                startedUtc = DateTime.SpecifyKind(startedUtc, DateTimeKind.Utc);
            return DateTime.UtcNow - startedUtc;
        }
        return null;
    }

    /// <summary>
    /// Compatibility shim for callers that expect a full stream object.
    /// Returns the first /streams item (or null if offline).
    /// </summary>
    public async Task<StreamInfo?> GetStreamAsync(string broadcasterId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/streams?user_id={broadcasterId}");
        _broadcasterAuth.ApplyAuth(req);
        req.Headers.Add("Client-Id", _clientId);

        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return null;

        var s = data[0];

        string id = TryGetString(s, "id");
        string userId = TryGetString(s, "user_id");
        string userLogin = TryGetString(s, "user_login");
        string userName = TryGetString(s, "user_name");
        string gameId = TryGetString(s, "game_id");
        string title = TryGetString(s, "title");
        DateTime startedAtUtc = DateTime.UtcNow;

        try
        {
            var startedAt = s.GetProperty("started_at").GetString();
            if (!DateTime.TryParse(startedAt, out startedAtUtc))
                startedAtUtc = DateTime.UtcNow;
            else if (startedAtUtc.Kind != DateTimeKind.Utc)
                startedAtUtc = DateTime.SpecifyKind(startedAtUtc, DateTimeKind.Utc);
        }
        catch { }

        return new StreamInfo(id, userId, userLogin, userName, gameId, title, startedAtUtc);
    }

    private static string TryGetString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) ? (v.GetString() ?? "") : "";

    // ---------------- Models ----------------

    public sealed record StreamInfo(
        string Id,
        string UserId,
        string UserLogin,
        string UserName,
        string GameId,
        string Title,
        DateTime StartedAt
    );
}
