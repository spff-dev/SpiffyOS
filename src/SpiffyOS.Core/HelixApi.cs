using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace SpiffyOS.Core;

/// <summary>
/// Minimal Helix helper for the bot. Uses:
///  - Broadcaster user token for most Helix GETs (e.g., /streams)
///  - App token for Send Chat Message (POST /chat/messages)
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

    /// <summary>
    /// Send a chat message using the **App Access Token** via Helix Send Chat Message API.
    /// Requires: app token; bot must be a chat participant; sender_id is the bot user id.
    /// </summary>
    public async Task SendChatMessageWithAppAsync(string broadcasterId, string senderUserId, string message, CancellationToken ct, string? replyParentMessageId = null)
    {
        // Get app token and apply headers (Authorization + Client-Id)
        var appToken = await _app.GetAsync(ct);

        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.twitch.tv/helix/chat/messages");

        object body = replyParentMessageId is string reply && !string.IsNullOrWhiteSpace(reply)
            ? new { broadcaster_id = broadcasterId, sender_id = senderUserId, message, reply_parent_message_id = reply }
            : new { broadcaster_id = broadcasterId, sender_id = senderUserId, message };

        req.Content = JsonContent.Create(body);

        // Apply (Bearer + Client-Id) using your AppTokenProvider
        _app.Apply(req, appToken);

        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Returns true if the channel is currently live.
    /// </summary>
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

    /// <summary>
    /// Get stream uptime if live; null if offline or unknown.
    /// </summary>
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
            if (startedUtc.Kind != DateTimeKind.Utc) startedUtc = DateTime.SpecifyKind(startedUtc, DateTimeKind.Utc);
            return DateTime.UtcNow - startedUtc;
        }

        return null;
    }
}
