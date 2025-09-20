using System.Net.Http.Json;
using System.Text.Json;

namespace SpiffyOS.Core;

public sealed class HelixApi
{
    private readonly HttpClient _http;
    private readonly TwitchAuth _auth;           // Broadcaster user token (for broadcaster-only endpoints)
    private readonly string _clientId;
    private readonly AppTokenProvider _app;      // App token (for Send Chat Message, badge-compliant)

    public HelixApi(HttpClient http, TwitchAuth auth, string clientId, AppTokenProvider app)
    {
        _http = http;
        _auth = auth;
        _clientId = clientId;
        _app = app;
    }

    private async Task<T> SendWithUserAsync<T>(HttpRequestMessage req, CancellationToken ct)
    {
        await _auth.EnsureValidAsync(ct);
        _auth.ApplyAuth(req);

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Helix {res.StatusCode}: {body}");
        }

        if (typeof(T) == typeof(object)) return default!;
        return await res.Content.ReadFromJsonAsync<T>(cancellationToken: ct) ?? throw new("Empty response");
    }

    // Use APP token for chat send (required for Chat Bot badge/listing).
    // replyParentMessageId is optional; when set, Twitch will thread the reply.
    public async Task SendChatMessageWithAppAsync(string broadcasterId, string senderUserId, string text, CancellationToken ct, string? replyParentMessageId = null)
    {
        var token = await _app.GetAsync(ct);

        object payload = replyParentMessageId is null
            ? new { broadcaster_id = broadcasterId, sender_id = senderUserId, message = text }
            : new { broadcaster_id = broadcasterId, sender_id = senderUserId, message = text, reply_parent_message_id = replyParentMessageId };

        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.twitch.tv/helix/chat/messages")
        {
            Content = JsonContent.Create(payload)
        };
        _app.Apply(req, token);

        using var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }

    // Broadcaster-only example (title/category).
    public Task ModifyChannelAsync(string broadcasterId, string? title, string? gameId, CancellationToken ct)
        => SendWithUserAsync<object>(
            new(HttpMethod.Patch, $"https://api.twitch.tv/helix/channels?broadcaster_id={broadcasterId}")
            { Content = JsonContent.Create(new { title, game_id = gameId }) }, ct);

    // Used by uptime handler
    public Task<StreamsResponse> GetStreamAsync(string userId, CancellationToken ct)
        => SendWithUserAsync<StreamsResponse>(new(HttpMethod.Get, $"https://api.twitch.tv/helix/streams?user_id={userId}"), ct);

    public record StreamsResponse(Stream[] data);
    public record Stream(string id, string user_id, DateTime started_at);
}
