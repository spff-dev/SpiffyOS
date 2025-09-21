namespace SpiffyOS.Core.Commands;

public sealed class XmasCommandHandler : ICommandHandler
{
    public Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        var tzId = Environment.GetEnvironmentVariable("SPIFFYOS_TZ") ?? "Europe/London";
        TimeZoneInfo? tzi = null;
        try { tzi = TimeZoneInfo.FindSystemTimeZoneById(tzId); } catch { }

        var nowUtc = DateTime.UtcNow;
        var nowLocal = tzi is null ? nowUtc : TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tzi);

        var year = nowLocal.Year;
        var xmasThisYear = new DateTime(year, 12, 25);
        var target = nowLocal.Date <= xmasThisYear.Date ? xmasThisYear : new DateTime(year + 1, 12, 25);

        var days = (target.Date - nowLocal.Date).Days;

        string text = days == 0
            ? "🎄 It's Christmas today! 🎁"
            : $"🎄 There are {days} day{(days == 1 ? "" : "s")} to Christmas! 🎅";

        return Task.FromResult<string?>(text);
    }
}
