using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace SpiffyOS.Core.Announcements;

public sealed class AnnouncementsService : BackgroundService
{
    private readonly ILogger<AnnouncementsService> _log;
    private readonly AnnouncementsConfigProvider _cfgProvider;
    private readonly HelixApi _helix;
    private readonly IEnumerable<EventSubWebSocket> _sockets;
    private readonly string _broadcasterId;
    private readonly string _botUserId;

    private readonly Random _rng = new();

    // pacing state
    private DateTime _nextGlobalUtc = DateTime.MinValue;
    private readonly Dictionary<int, DateTime> _perMsgNextUtc = new(); // msgIndex -> earliest next send
    private int _lastIndex = -1;

    // activity state
    private DateTime _lastChatUtc = DateTime.UtcNow;

    public AnnouncementsService(
        ILogger<AnnouncementsService> log,
        AnnouncementsConfigProvider cfgProvider,
        HelixApi helix,
        IEnumerable<EventSubWebSocket> sockets,
        IConfiguration cfg)
    {
        _log = log;
        _cfgProvider = cfgProvider;
        _helix = helix;
        _sockets = sockets;
        _broadcasterId = cfg["Twitch:BroadcasterId"]!;
        _botUserId = cfg["Twitch:BotUserId"]!;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // subscribe to chat activity (for optional activity pause)
        foreach (var s in _sockets)
        {
            s.ChatMessageReceived += _ => _lastChatUtc = DateTime.UtcNow;
        }

        _log.LogInformation("Announcements service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Announcements service tick error");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var cfg = _cfgProvider.Snapshot();
        if (!cfg.Enabled) return;

        // online only?
        if (cfg.OnlineOnly)
        {
            var isLive = await _helix.IsLiveAsync(_broadcasterId, ct);
            if (!isLive) return;
        }

        // quiet hours?
        if (InQuietHours(cfg.QuietHours)) return;

        // activity pause?
        if (cfg.Activity.Enabled)
            if (DateTime.UtcNow - _lastChatUtc > TimeSpan.FromMinutes(Math.Max(1, cfg.Activity.NoChatMinutes)))
                return;

        // global gap
        if (DateTime.UtcNow < _nextGlobalUtc) return;

        var idx = PickMessageIndex(cfg);
        if (idx < 0) return; // nothing eligible

        var msg = cfg.Messages[idx].Text.Trim();
        if (string.IsNullOrEmpty(msg)) return;

        await _helix.SendChatMessageWithAppAsync(_broadcasterId, _botUserId, msg, ct);

        // set pacing
        _lastIndex = idx;
        _nextGlobalUtc = DateTime.UtcNow.AddMinutes(Math.Max(0.1, cfg.MinGapMinutes));
        var per = cfg.Messages[idx].MinIntervalMinutes;
        _perMsgNextUtc[idx] = DateTime.UtcNow.AddMinutes(Math.Max(0.1, per));

        _log.LogInformation("Announcement sent (#{Index}): {Text}", idx, msg);
    }

    private int PickMessageIndex(AnnouncementsConfig cfg)
    {
        if (cfg.Messages.Count == 0) return -1;

        var now = DateTime.UtcNow;

        // build eligible set (respect per-message intervals and avoid immediate repeat)
        var eligible = new List<(int idx, int weight)>();
        for (int i = 0; i < cfg.Messages.Count; i++)
        {
            if (i == _lastIndex && cfg.Messages.Count > 1) continue; // avoid repeating same message

            if (_perMsgNextUtc.TryGetValue(i, out var t) && now < t) continue;

            var w = Math.Max(1, cfg.Messages[i].Weight);
            eligible.Add((i, w));
        }

        if (eligible.Count == 0) return -1;

        // weighted random
        var total = eligible.Sum(e => e.weight);
        var roll = _rng.Next(total);
        var acc = 0;
        foreach (var e in eligible)
        {
            acc += e.weight;
            if (roll < acc) return e.idx;
        }
        return eligible[0].idx; // fallback
    }

    private static bool InQuietHours(QuietHoursConfig? qh)
    {
        if (qh is null) return false;

        try
        {
            // linux + windows both accept IANA ids like "Europe/London" on .NET 8 (tzdata installed on Linux)
            var tz = TimeZoneInfo.FindSystemTimeZoneById(qh.Timezone);
            var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);

            if (!TimeOnly.TryParse(qh.Start, out var start)) return false;
            if (!TimeOnly.TryParse(qh.End, out var end)) return false;

            var nowT = TimeOnly.FromDateTime(local);

            // handle windows over-midnight spans
            if (start <= end)
                return nowT >= start && nowT < end;
            else
                return nowT >= start || nowT < end;
        }
        catch
        {
            return false;
        }
    }
}
