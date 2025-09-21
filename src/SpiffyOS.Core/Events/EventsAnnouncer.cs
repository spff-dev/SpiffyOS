using Microsoft.Extensions.Logging;
using System.Linq;

namespace SpiffyOS.Core.Events;

public sealed class EventsAnnouncer
{
    private readonly HelixApi _helix;
    private readonly ILogger<EventsAnnouncer> _log;
    private readonly EventsConfigProvider _cfgProvider;
    private readonly string _broadcasterId;
    private readonly string _botUserId;

    // global rate-limit
    private DateTime _nextSendUtc = DateTime.MinValue;

    // per-section cooldowns
    private DateTime _followsNextUtc = DateTime.MinValue;
    private DateTime _subsNextUtc = DateTime.MinValue;

    // follow dedupe: userId -> expiresAt
    private readonly Dictionary<string, DateTime> _followSeen = new();

    // batching
    private readonly HashSet<string> _batchNames = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _batchCts;

    public EventsAnnouncer(HelixApi helix, ILogger<EventsAnnouncer> log, EventsConfigProvider cfgProvider,
                           string broadcasterId, string botUserId)
    {
        _helix = helix;
        _log = log;
        _cfgProvider = cfgProvider;
        _broadcasterId = broadcasterId;
        _botUserId = botUserId;
    }

    // ---------- FOLLOWS ----------
    public async Task HandleFollowAsync(EventSubWebSocket.FollowEvent ev, CancellationToken ct)
    {
        var cfg = _cfgProvider.Snapshot();
        var f = cfg.Follows;
        if (!f.Enabled) return;

        var now = DateTime.UtcNow;

        // dedupe by user id
        Prune(_followSeen, now);
        if (_followSeen.TryGetValue(ev.UserId, out var until) && until > now)
        {
            _log.LogDebug("Follow deduped: {User} ({Id})", ev.UserNameOrLogin(), FollowEventExtensions.Mask(ev.UserId));
            return;
        }
        _followSeen[ev.UserId] = now.AddSeconds(Math.Max(1, f.DedupeWindowSeconds));

        // batching path
        if (f.Batching.Enabled)
        {
            lock (_batchNames) { _batchNames.Add(ev.UserNameOrLogin()); }
            ArmBatchTimer(f.Batching.WindowSeconds, ct);
            _log.LogInformation("Follow batched: {User}", ev.UserNameOrLogin());
            return;
        }

        // per-section cooldown + global rate-limit
        if (now < _followsNextUtc) { _log.LogInformation("Follow suppressed by follows cooldown"); return; }
        if (!CanSend(cfg)) { _log.LogInformation("Follow suppressed by global rate-limit"); return; }
        _followsNextUtc = now.AddSeconds(Math.Max(0, f.CooldownSeconds));

        var text = f.Template
            .Replace("{user.name}", ev.UserNameOrLogin())
            .Replace("{user.login}", Safe(ev.UserLogin));

        await SendAsync(text, ct);
        _log.LogInformation("Follow announced: {User}", ev.UserNameOrLogin());
    }

    // ---------- SUBSCRIPTIONS ----------
    public async Task HandleSubscribeAsync(EventSubWebSocket.SubscriptionEvent ev, CancellationToken ct)
    {
        var cfg = _cfgProvider.Snapshot();
        var s = cfg.Subs;
        if (!s.Enabled) return;

        var now = DateTime.UtcNow;
        if (now < _subsNextUtc) { _log.LogInformation("Sub suppressed by subs cooldown"); return; }
        if (!CanSend(cfg)) { _log.LogInformation("Sub suppressed by global rate-limit"); return; }
        _subsNextUtc = now.AddSeconds(Math.Max(0, s.CooldownSeconds));

        string text;
        if (ev.IsGift)
        {
            var gifter = NameOrLogin(ev.GifterUserName, ev.GifterUserLogin);
            text = s.TemplateGift
                .Replace("{gifter.name}", Safe(gifter))
                .Replace("{user.name}", NameOrLogin(ev.UserName, ev.UserLogin))
                .Replace("{sub.tier}", Safe(ev.Tier));
        }
        else
        {
            text = s.TemplateNew
                .Replace("{user.name}", NameOrLogin(ev.UserName, ev.UserLogin))
                .Replace("{sub.tier}", Safe(ev.Tier));
        }

        await SendAsync(text, ct);
        _log.LogInformation("Sub announced: {User} Gift={Gift} Tier={Tier}",
            NameOrLogin(ev.UserName, ev.UserLogin), ev.IsGift, ev.Tier ?? "(none)");
    }

    public async Task HandleSubscriptionMessageAsync(EventSubWebSocket.SubscriptionMessageEvent ev, CancellationToken ct)
    {
        var cfg = _cfgProvider.Snapshot();
        var s = cfg.Subs;
        if (!s.Enabled) return;

        var now = DateTime.UtcNow;
        if (now < _subsNextUtc) { _log.LogInformation("Resub suppressed by subs cooldown"); return; }
        if (!CanSend(cfg)) { _log.LogInformation("Resub suppressed by global rate-limit"); return; }
        _subsNextUtc = now.AddSeconds(Math.Max(0, s.CooldownSeconds));

        var who = NameOrLogin(ev.UserName, ev.UserLogin);

        var text1 = s.TemplateResub
            .Replace("{user.name}", who)
            .Replace("{sub.months}", ev.CumulativeMonths.ToString())
            .Replace("{sub.tier}", Safe(ev.Tier));

        await SendAsync(text1, ct);

        var message = (ev.Message ?? "").Trim();
        if (!string.IsNullOrEmpty(message))
        {
            var text2 = s.TemplateMessage
                .Replace("{user.name}", who)
                .Replace("{message}", message);
            await SendAsync(text2, ct);
        }

        _log.LogInformation("Resub announced: {User} Months={Months} Streak={Streak} Tier={Tier}",
            who, ev.CumulativeMonths, ev.StreakMonths, ev.Tier ?? "(none)");
    }

    // ---------- helpers ----------
    private void ArmBatchTimer(int windowSeconds, CancellationToken ct)
    {
        _batchCts?.Cancel();
        _batchCts?.Dispose();
        _batchCts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, windowSeconds)), _batchCts.Token);
                await FlushBatchAsync(ct);
            }
            catch (TaskCanceledException) { /* replaced window */ }
        });
    }

    private async Task FlushBatchAsync(CancellationToken ct)
    {
        var cfg = _cfgProvider.Snapshot();
        var f = cfg.Follows;
        if (!f.Enabled || !f.Batching.Enabled) return;

        string list;
        lock (_batchNames)
        {
            if (_batchNames.Count == 0) return;
            list = string.Join(", ", _batchNames);
            _batchNames.Clear();
        }

        if (!CanSend(cfg)) { _log.LogInformation("Follow batch suppressed by global rate-limit"); return; }

        var text = f.Batching.Template.Replace("{user.list}", list);
        await SendAsync(text, ct);
        _log.LogInformation("Follow batch announced: {List}", list);
    }

    private bool CanSend(EventsConfig cfg)
    {
        var now = DateTime.UtcNow;
        if (now < _nextSendUtc) return false;
        var gap = Math.Max(0.2, cfg.RateLimitSeconds);
        _nextSendUtc = now.AddSeconds(gap);
        return true;
    }

    private async Task SendAsync(string text, CancellationToken ct)
        => await _helix.SendChatMessageWithAppAsync(_broadcasterId, _botUserId, text, ct);

    private static void Prune(Dictionary<string, DateTime> map, DateTime now)
    {
        var expired = map.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
        foreach (var k in expired) map.Remove(k);
    }

    private static string Safe(string? s) => s ?? "";
    private static string NameOrLogin(string? name, string? login)
        => !string.IsNullOrWhiteSpace(name) ? name! :
           (!string.IsNullOrWhiteSpace(login) ? login! : "(someone)");
}

public static class FollowEventExtensions
{
    public static string UserNameOrLogin(this SpiffyOS.Core.EventSubWebSocket.FollowEvent ev)
        => !string.IsNullOrWhiteSpace(ev.UserName) ? ev.UserName :
           (!string.IsNullOrWhiteSpace(ev.UserLogin) ? ev.UserLogin : "(someone)");

    public static string Mask(string s)
        => string.IsNullOrEmpty(s) || s.Length <= 4 ? s : new string('*', s.Length - 4) + s[^4..];
}
