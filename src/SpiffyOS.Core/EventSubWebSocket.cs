using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SpiffyOS.Core;

public sealed class EventSubWebSocket : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly TwitchAuth _auth;   // BOT user token (required for WS EventSub)
    private readonly string _clientId;

    private ClientWebSocket? _ws;
    private readonly Uri _endpoint = new("wss://eventsub.wss.twitch.tv/ws");
    private string? _sessionId;

    // Graceful shutdown helpers
    private Task? _rxTask;
    private CancellationTokenSource? _rxCts;

    public EventSubWebSocket(HttpClient http, TwitchAuth auth, string clientId, AppTokenProvider _)
    {
        _http = http;
        _auth = auth;      // must be BOT user token
        _clientId = clientId;
    }

    public event Action<ChatMessage>? ChatMessageReceived;
    public event Action<FollowEvent>? FollowReceived;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(_endpoint, ct);

        _rxCts?.Dispose();
        _rxCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _rxTask = Task.Run(() => ReceiveLoop(_rxCts.Token));
    }

    /// <summary>
    /// For follows (v2), the bot must be a moderator. We use:
    ///   condition = { broadcaster_user_id: <channel>, moderator_user_id: <bot> }
    /// </summary>
    public async Task EnsureSubscriptionsAsync(string broadcasterId, string moderatorId, string userId, CancellationToken ct)
    {
        // Wait up to ~10s for session_welcome to arrive (sets _sessionId)
        var start = DateTime.UtcNow;
        while (_sessionId is null && (DateTime.UtcNow - start).TotalSeconds < 10)
            await Task.Delay(100, ct);

        if (_sessionId is null)
            throw new InvalidOperationException("No EventSub session id (no session_welcome received).");

        // Chat messages (bot identity for classification)
        await CreateSub("channel.chat.message", "1",
            new { broadcaster_user_id = broadcasterId, user_id = userId }, ct);

        // Follows (v2) — require moderator id
        await CreateSub("channel.follow", "2",
            new { broadcaster_user_id = broadcasterId, moderator_user_id = moderatorId }, ct);

        // We’ll add subs/bits/raids/redemptions later.
    }

    private async Task CreateSub(string type, string version, object condition, CancellationToken ct)
    {
        await _auth.EnsureValidAsync(ct);

        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.twitch.tv/helix/eventsub/subscriptions");
        _auth.ApplyAuth(req); // USER token (BOT) for WebSocket transport

        var payload = new
        {
            type,
            version,
            condition,
            transport = new { method = "websocket", session_id = _sessionId }
        };
        req.Content = JsonContent.Create(payload);

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            Console.WriteLine($"EventSub {((int)res.StatusCode)} for {type}: {body}");
            res.EnsureSuccessStatusCode();
        }
    }

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buf = new byte[64 * 1024];

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, ct);
                    ms.Write(buf, 0, r.Count);
                }
                while (!r.EndOfMessage);

                var json = Encoding.UTF8.GetString(ms.ToArray());

                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var meta = root.GetProperty("metadata");
                    var mtype = meta.GetProperty("message_type").GetString();

                    if (mtype == "session_welcome")
                    {
                        _sessionId = root.GetProperty("payload").GetProperty("session").GetProperty("id").GetString();
                    }
                    else if (mtype == "notification")
                    {
                        var payload = root.GetProperty("payload");
                        var subType = payload.GetProperty("subscription").GetProperty("type").GetString();

                        if (subType == "channel.chat.message")
                        {
                            var ev = payload.GetProperty("event");

                            // Text
                            string text = "";
                            try { text = ev.GetProperty("message").GetProperty("text").GetString() ?? ""; }
                            catch (Exception ex) { Console.WriteLine($"Parse error reading event.message.text: {ex.Message}"); }

                            // Message IDs
                            string? msgId = null;
                            try { msgId = ev.GetProperty("message_id").GetString(); } catch { }
                            if (string.IsNullOrEmpty(msgId))
                            {
                                try { msgId = ev.GetProperty("message").GetProperty("id").GetString(); } catch { }
                            }

                            // Roles via badges
                            bool isBroadcaster = false, isMod = false, isVip = false, isSub = false;
                            try
                            {
                                if (ev.TryGetProperty("badges", out var badges) && badges.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var b in badges.EnumerateArray())
                                    {
                                        var set = b.TryGetProperty("set_id", out var sid) ? sid.GetString() : null;
                                        switch (set)
                                        {
                                            case "broadcaster": isBroadcaster = true; break;
                                            case "moderator": isMod = true; break;
                                            case "vip": isVip = true; break;
                                            case "subscriber": isSub = true; break;
                                        }
                                    }
                                }
                            }
                            catch { /* ignore */ }

                            var broadId = ev.GetProperty("broadcaster_user_id").GetString() ?? "";
                            var chatterId = ev.GetProperty("chatter_user_id").GetString() ?? "";

                            // usernames/logins
                            string chatterLogin = "";
                            string chatterName = "";
                            try { chatterLogin = ev.GetProperty("chatter_user_login").GetString() ?? ""; } catch { }
                            try { chatterName = ev.GetProperty("chatter_user_name").GetString() ?? ""; } catch { }

                            if (!string.IsNullOrWhiteSpace(text))
                                Console.WriteLine($"Chat msg -> {text}");

                            ChatMessageReceived?.Invoke(new ChatMessage(
                                broadId,
                                chatterId,
                                chatterLogin,
                                chatterName,
                                text,
                                msgId,
                                isBroadcaster, isMod, isVip, isSub
                            ));
                        }
                        else if (subType == "channel.follow")
                        {
                            var ev = payload.GetProperty("event");
                            var broadId = ev.GetProperty("broadcaster_user_id").GetString() ?? "";
                            var userId = ev.GetProperty("user_id").GetString() ?? "";
                            string userLogin = "", userName = "";
                            try { userLogin = ev.GetProperty("user_login").GetString() ?? ""; } catch { }
                            try { userName = ev.GetProperty("user_name").GetString() ?? ""; } catch { }

                            FollowReceived?.Invoke(new FollowEvent(broadId, userId, userLogin, userName));
                        }
                    }
                }
                catch
                {
                    // swallow parse errors
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ReceiveLoop ended: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Stop the receive loop first
        try { _rxCts?.Cancel(); } catch { }
        if (_rxTask is not null)
        {
            try { await Task.WhenAny(_rxTask, Task.Delay(500)); } catch { }
        }
        _rxCts?.Dispose();
        _rxCts = null;
        _rxTask = null;

        // Close the socket only if in a closable state
        var ws = _ws;
        _ws = null;
        if (ws is null) return;

        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            else if (ws.State == WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
        finally { ws.Dispose(); }
    }

    public record ChatMessage(
        string BroadcasterUserId,
        string ChatterUserId,
        string ChatterUserLogin,
        string ChatterUserName,
        string Text,
        string? MessageId,
        bool IsBroadcaster,
        bool IsModerator,
        bool IsVIP,
        bool IsSubscriber
    );

    public record FollowEvent(
        string BroadcasterUserId,
        string UserId,
        string UserLogin,
        string UserName
    );
}
