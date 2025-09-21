using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace SpiffyOS.Core;

/// <summary>
/// Helix helpers for SpiffyOS.
/// - Broadcaster user token for read endpoints (e.g., /streams)
/// - App token for Send Chat Message (/chat/messages)
///
/// Methods:
///   SendChatMessageWithAppAsync(...)
///   SendChatMessageAsync(...)              // wrapper for compatibility, calls app-token path
///   IsLiveAsync(...)
///   GetUptimeAsync(...)
///   GetStreamAsync(...) -> StreamsResponse (with .data[] as callers expect)
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
    /// Requires: the bot user (sender_id) is joined to chat and app has user:bot + user:write:chat.
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
    /// Compatibility wrapper. Historically callers used a user-token send; we route to app-token path.
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
        var resp = await GetStreamAsync(broadcasterId, ct);
        if (resp is null || resp.data is null || resp.data.Count == 0)
            return null;

        var startedUtc = resp.data[0].started_at;
        if (startedUtc.Kind != DateTimeKind.Utc)
            startedUtc = DateTime.SpecifyKind(startedUtc, DateTimeKind.Utc);
        return DateTime.UtcNow - startedUtc;
    }

    /// <summary>
    /// Returns the /streams response object with a .data array (shape matches current callers).
    /// Null if offline or request failed to parse.
    /// </summary>
    public async Task<StreamsResponse?> GetStreamAsync(string broadcasterId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.twitch.tv/helix/streams?user_id={broadcasterId}");
        _broadcasterAuth.ApplyAuth(req);
        req.Headers.Add("Client-Id", _clientId);

        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadAsStringAsync(ct);
        var resp = System.Text.Json.JsonSerializer.Deserialize<StreamsResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        return resp;
    }

    // ---------------- Models (match Helix /streams minimal fields we use) ----------------

    public sealed class StreamsResponse
    {
        public List<StreamItem> data { get; set; } = new();
        public Pagination? pagination { get; set; } // present for API parity; currently unused
    }

    public sealed class StreamItem
    {
        public string id { get; set; } = "";
        public string user_id { get; set; } = "";
        public string user_login { get; set; } = "";
        public string user_name { get; set; } = "";
        public string game_id { get; set; } = "";
        public string title { get; set; } = "";
        public DateTime started_at { get; set; } // ISO8601 UTC
    }

    public sealed class Pagination
    {
        public string? cursor { get; set; }
    }
}
