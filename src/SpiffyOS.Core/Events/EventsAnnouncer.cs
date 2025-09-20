using Microsoft.Extensions.Logging;

namespace SpiffyOS.Core.Events;

public sealed class EventsAnnouncer
{
    private readonly HelixApi _helix;
    private readonly ILogger<EventsAnnouncer> _log;
    private readonly EventsConfigProvider _cfgProvider;
    private readonly string _broadcasterId;
    private readonly string _botUserId;

    // rate-limit
    private DateTime _nextSendUtc = DateTime.MinValue;

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

    public async Task HandleFollowAsync(EventSubWebSocket.FollowEvent ev, CancellationToken ct)
    {
        var cfg = _cfgProvider.Snapshot();
        var f = cfg.Follows;
        if (!f.Enabled) return;

        // dedupe
        var now = DateTime.UtcNow;
        Prune(_followSeen, now);
        if (_followSeen.TryGetValue(ev.UserId, out var until) && until > now)
        {
            _log.LogDebug("Follow deduped: {User} ({Id})", ev.UserNameOrLogin(), FollowEventExtensions.Mask(ev.UserId));
            return;
        }
        _followSeen[ev.UserId] = now.AddSeconds(Math.Max(1, f.DedupeWindowSeconds));

        if (f.Batching.Enabled)
        {
            lock (_batchNames) { _batchNames.Add(ev.UserNameOrLogin()); }
            ArmBatchTimer(f.Batching.WindowSeconds, ct);
            _log.LogInformation("Follow batched: {User}", ev.UserNameOrLogin());
            return;
        }

        // single-follow path with rate-limit
        if (!CanSend(cfg)) { _log.LogInformation("Follow suppressed by rate-limit"); return; }

        var text = f.Template
            .Replace("{user.name}", ev.UserNameOrLogin())
            .Replace("{user.login}", Safe(ev.UserLogin));

        await SendAsync(text, cfg, ct);
        _log.LogInformation("Follow announced: {User}", ev.UserNameOrLogin());
    }

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

        if (!CanSend(cfg)) { _log.LogInformation("Follow batch suppressed by rate-limit"); return; }

        var text = f.Batching.Template.Replace("{user.list}", list);
        await SendAsync(text, cfg, ct);
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

    private async Task SendAsync(string text, EventsConfig cfg, CancellationToken ct)
    {
        await _helix.SendChatMessageWithAppAsync(_broadcasterId, _botUserId, text, ct);
    }

    private static void Prune(Dictionary<string, DateTime> map, DateTime now)
    {
        var expired = map.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
        foreach (var k in expired) map.Remove(k);
    }

    private static string Safe(string s) => s ?? "";
}

public static class FollowEventExtensions
{
    public static string UserNameOrLogin(this SpiffyOS.Core.EventSubWebSocket.FollowEvent ev)
        => !string.IsNullOrWhiteSpace(ev.UserName) ? ev.UserName :
           (!string.IsNullOrWhiteSpace(ev.UserLogin) ? ev.UserLogin : "(someone)");

    public static string Mask(string s)
        => string.IsNullOrEmpty(s) || s.Length <= 4 ? s : new string('*', s.Length - 4) + s[^4..];
}
