using System.Net.Http;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SpiffyOS.Core;

public sealed class EventSubWebSocket : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly TwitchAuth _auth;   // Token user for this socket (BOT or BROADCASTER)
    private readonly string _clientId;
    private readonly ILogger<EventSubWebSocket> _log;

    private ClientWebSocket? _ws;
    private readonly Uri _endpoint = new("wss://eventsub.wss.twitch.tv/ws");
    private string? _sessionId;

    // Graceful shutdown helpers
    private Task? _rxTask;
    private CancellationTokenSource? _rxCts;

    public EventSubWebSocket(HttpClient http, TwitchAuth auth, string clientId, AppTokenProvider _, ILogger<EventSubWebSocket> log)
    {
        _http = http;
        _auth = auth;
        _clientId = clientId;
        _log = log;
    }

    // events we emit to the app
    public event Action<ChatMessage>? ChatMessageReceived;
    public event Action<FollowEvent>? FollowReceived;
    public event Action<SubscriptionEvent>? SubscriptionReceived;
    public event Action<SubscriptionMessageEvent>? SubscriptionMessageReceived;
    public event Action<RedemptionEvent>? RedemptionReceived;
    public event Action<BitsEvent>? CheerReceived;
    public event Action<RaidEvent>? RaidReceived;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(_endpoint, ct);

        _rxCts?.Dispose();
        _rxCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _rxTask = Task.Run(() => ReceiveLoop(_rxCts.Token));
    }

    public async Task EnsureSubscriptionsBotAsync(string broadcasterId, string moderatorUserId, string botUserId, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        while (_sessionId is null && (DateTime.UtcNow - start).TotalSeconds < 10)
            await Task.Delay(100, ct);
        if (_sessionId is null)
            throw new InvalidOperationException("No EventSub session id (no session_welcome received).");

        await CreateSub("channel.chat.message", "1",
            new { broadcaster_user_id = broadcasterId, user_id = botUserId }, ct);

        await CreateSub("channel.follow", "2",
            new { broadcaster_user_id = broadcasterId, moderator_user_id = moderatorUserId }, ct);
    }

    public async Task EnsureSubscriptionsBroadcasterAsync(string broadcasterId, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        while (_sessionId is null && (DateTime.UtcNow - start).TotalSeconds < 10)
            await Task.Delay(100, ct);
        if (_sessionId is null)
            throw new InvalidOperationException("No EventSub session id (no session_welcome received).");

        await CreateSub("channel.subscribe", "1",
            new { broadcaster_user_id = broadcasterId }, ct);

        await CreateSub("channel.subscription.message", "1",
            new { broadcaster_user_id = broadcasterId }, ct);

        await CreateSub("channel.channel_points_custom_reward_redemption.add", "1",
            new { broadcaster_user_id = broadcasterId }, ct);

        await CreateSub("channel.cheer", "1",
            new { broadcaster_user_id = broadcasterId }, ct);

        await CreateSub("channel.raid", "1",
            new { to_broadcaster_user_id = broadcasterId }, ct);
    }

    private async Task CreateSub(string type, string version, object condition, CancellationToken ct)
    {
        await _auth.EnsureValidAsync(ct);

        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.twitch.tv/helix/eventsub/subscriptions");
        _auth.ApplyAuth(req);

        var payload = new { type, version, condition, transport = new { method = "websocket", session_id = _sessionId } };
        req.Content = JsonContent.Create(payload);

        using var res = await _http.SendAsync(req, ct);
        var code = (int)res.StatusCode;

        if (res.IsSuccessStatusCode)
        {
            _log.LogInformation("EventSub create OK: {Type} v{Version} -> {Status}", type, version, code);
        }
        else
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            _log.LogWarning("EventSub create FAIL: {Type} v{Version} -> {Status} {Body}", type, version, code, body);
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
                } while (!r.EndOfMessage);

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

                            string text = "";
                            try { text = ev.GetProperty("message").GetProperty("text").GetString() ?? ""; } catch { }

                            string? msgId = null;
                            try { msgId = ev.GetProperty("message_id").GetString(); } catch { }
                            if (string.IsNullOrEmpty(msgId))
                            {
                                try { msgId = ev.GetProperty("message").GetProperty("id").GetString(); } catch { }
                            }

                            string? replyParentId = null;
                            try { replyParentId = ev.GetProperty("message").GetProperty("reply").GetProperty("parent_message_id").GetString(); } catch { }

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
                            catch { }

                            var broadId = ev.GetProperty("broadcaster_user_id").GetString() ?? "";
                            var chatterId = ev.GetProperty("chatter_user_id").GetString() ?? "";
                            string chatterLogin = "", chatterName = "";
                            try { chatterLogin = ev.GetProperty("chatter_user_login").GetString() ?? ""; } catch { }
                            try { chatterName = ev.GetProperty("chatter_user_name").GetString() ?? ""; } catch { }

                            if (!string.IsNullOrWhiteSpace(text))
                                Console.WriteLine($"Chat msg -> {text}");

                            ChatMessageReceived?.Invoke(new ChatMessage(
                                broadId, chatterId, chatterLogin, chatterName, text, msgId,
                                isBroadcaster, isMod, isVip, isSub,
                                replyParentId
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
                        else if (subType == "channel.subscribe")
                        {
                            var ev = payload.GetProperty("event");
                            var broadId = ev.GetProperty("broadcaster_user_id").GetString() ?? "";
                            var userId = ev.GetProperty("user_id").GetString() ?? "";
                            string userLogin = "", userName = "";
                            try { userLogin = ev.GetProperty("user_login").GetString() ?? ""; } catch { }
                            try { userName = ev.GetProperty("user_name").GetString() ?? ""; } catch { }

                            bool isGift = false;
                            string? tier = null;
                            string? gifterId = null, gifterLogin = null, gifterName = null;
                            try { isGift = ev.GetProperty("is_gift").GetBoolean(); } catch { }
                            try { tier = ev.GetProperty("tier").GetString(); } catch { }
                            try { gifterId = ev.GetProperty("gifter_user_id").GetString(); } catch { }
                            try { gifterLogin = ev.GetProperty("gifter_user_login").GetString(); } catch { }
                            try { gifterName = ev.GetProperty("gifter_user_name").GetString(); } catch { }

                            SubscriptionReceived?.Invoke(new SubscriptionEvent(
                                broadId, userId, userLogin, userName, isGift, tier, gifterId, gifterLogin, gifterName
                            ));
                        }
                        else if (subType == "channel.subscription.message")
                        {
                            var ev = payload.GetProperty("event");
                            var broadId = ev.GetProperty("broadcaster_user_id").GetString() ?? "";
                            var userId = ev.GetProperty("user_id").GetString() ?? "";
                            string userLogin = "", userName = "";
                            try { userLogin = ev.GetProperty("user_login").GetString() ?? ""; } catch { }
                            try { userName = ev.GetProperty("user_name").GetString() ?? ""; } catch { }

                            int cumulativeMonths = 0, streakMonths = 0;
                            string? tier = null, message = null;
                            try { cumulativeMonths = ev.GetProperty("cumulative_months").GetInt32(); } catch { }
                            try { streakMonths = ev.GetProperty("streak_months").GetInt32(); } catch { }
                            try { tier = ev.GetProperty("tier").GetString(); } catch { }
                            try { message = ev.GetProperty("message").GetProperty("text").GetString(); } catch { }

                            SubscriptionMessageReceived?.Invoke(new SubscriptionMessageEvent(
                                broadId, userId, userLogin, userName, cumulativeMonths, streakMonths, tier, message
                            ));
                        }
                        else if (subType == "channel.channel_points_custom_reward_redemption.add")
                        {
                            var ev = payload.GetProperty("event");
                            var broadId = ev.GetProperty("broadcaster_user_id").GetString() ?? "";
                            var userId = ev.GetProperty("user_id").GetString() ?? "";

                            string userLogin = "", userName = "", status = "", userInput = "";
                            string? rewardId = null, rewardTitle = null, rewardPrompt = null;
                            int rewardCost = 0;

                            try { userLogin = ev.GetProperty("user_login").GetString() ?? ""; } catch { }
                            try { userName = ev.GetProperty("user_name").GetString() ?? ""; } catch { }
                            try { status = ev.GetProperty("status").GetString() ?? ""; } catch { }
                            try { userInput = ev.GetProperty("user_input").GetString() ?? ""; } catch { }

                            try
                            {
                                var rewardEl = ev.GetProperty("reward");
                                rewardId = rewardEl.TryGetProperty("id", out var rid) ? rid.GetString() : null;
                                rewardTitle = rewardEl.TryGetProperty("title", out var rt) ? rt.GetString() : null;
                                rewardPrompt = rewardEl.TryGetProperty("prompt", out var rp) ? rp.GetString() : null;
                                try { rewardCost = rewardEl.GetProperty("cost").GetInt32(); } catch { }
                            }
                            catch { }

                            RedemptionReceived?.Invoke(new RedemptionEvent(
                                broadId, userId, userLogin, userName, status, userInput, rewardId, rewardTitle, rewardPrompt, rewardCost
                            ));
                        }
                        else if (subType == "channel.cheer")
                        {
                            var ev = payload.GetProperty("event");
                            var broadId = ev.GetProperty("broadcaster_user_id").GetString() ?? "";

                            bool isAnon = false;
                            int bits = 0;
                            string? message = null;

                            try { isAnon = ev.GetProperty("is_anonymous").GetBoolean(); } catch { }
                            try { bits = ev.GetProperty("bits").GetInt32(); } catch { }
                            try { message = ev.GetProperty("message").GetString(); } catch { }

                            string? userId = null, userLogin = null, userName = null;
                            if (!isAnon)
                            {
                                try { userId = ev.GetProperty("user_id").GetString(); } catch { }
                                try { userLogin = ev.GetProperty("user_login").GetString(); } catch { }
                                try { userName = ev.GetProperty("user_name").GetString(); } catch { }
                            }

                            CheerReceived?.Invoke(new BitsEvent(
                                broadId, isAnon, userId, userLogin, userName, bits, message
                            ));
                        }
                        else if (subType == "channel.raid")
                        {
                            var ev = payload.GetProperty("event");

                            string fromId = "", fromLogin = "", fromName = "";
                            string toId = "", toLogin = "", toName = "";
                            int viewers = 0;

                            try { fromId = ev.GetProperty("from_broadcaster_user_id").GetString() ?? ""; } catch { }
                            try { fromLogin = ev.GetProperty("from_broadcaster_user_login").GetString() ?? ""; } catch { }
                            try { fromName = ev.GetProperty("from_broadcaster_user_name").GetString() ?? ""; } catch { }

                            try { toId = ev.GetProperty("to_broadcaster_user_id").GetString() ?? ""; } catch { }
                            try { toLogin = ev.GetProperty("to_broadcaster_user_login").GetString() ?? ""; } catch { }
                            try { toName = ev.GetProperty("to_broadcaster_user_name").GetString() ?? ""; } catch { }

                            try { viewers = ev.GetProperty("viewers").GetInt32(); } catch { }

                            RaidReceived?.Invoke(new RaidEvent(
                                fromId, fromLogin, fromName,
                                toId, toLogin, toName,
                                viewers
                            ));
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
        try { _rxCts?.Cancel(); } catch { }
        if (_rxTask is not null) { try { await Task.WhenAny(_rxTask, Task.Delay(500)); } catch { } }
        _rxCts?.Dispose(); _rxCts = null; _rxTask = null;

        var ws = _ws; _ws = null;
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

    // payload types we surface
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
        bool IsSubscriber,
        string? ReplyParentMessageId // NEW
    );

    public record FollowEvent(
        string BroadcasterUserId,
        string UserId,
        string UserLogin,
        string UserName
    );

    public record SubscriptionEvent(
        string BroadcasterUserId,
        string UserId,
        string UserLogin,
        string UserName,
        bool IsGift,
        string? Tier,
        string? GifterUserId,
        string? GifterUserLogin,
        string? GifterUserName
    );

    public record SubscriptionMessageEvent(
        string BroadcasterUserId,
        string UserId,
        string UserLogin,
        string UserName,
        int CumulativeMonths,
        int StreakMonths,
        string? Tier,
        string? Message
    );

    public record RedemptionEvent(
        string BroadcasterUserId,
        string UserId,
        string UserLogin,
        string UserName,
        string Status,
        string UserInput,
        string? RewardId,
        string? RewardTitle,
        string? RewardPrompt,
        int RewardCost
    );

    public record BitsEvent(
        string BroadcasterUserId,
        bool IsAnonymous,
        string? UserId,
        string? UserLogin,
        string? UserName,
        int Bits,
        string? Message
    );

    public record RaidEvent(
        string FromBroadcasterUserId,
        string FromBroadcasterUserLogin,
        string FromBroadcasterUserName,
        string ToBroadcasterUserId,
        string ToBroadcasterUserLogin,
        string ToBroadcasterUserName,
        int Viewers
    );
}
