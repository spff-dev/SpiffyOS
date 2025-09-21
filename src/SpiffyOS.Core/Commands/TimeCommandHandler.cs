using System.Globalization;

namespace SpiffyOS.Core.Commands;

public sealed class TimeCommandHandler : ICommandHandler
{
    public Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        // Configurable timezone via env var; default to Europe/London
        var tzId = Environment.GetEnvironmentVariable("SPIFFYOS_TZ") ?? "Europe/London";

        TimeZoneInfo? tzi = null;
        try { tzi = TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch { /* fall back to UTC below */ }

        var nowUtc = DateTime.UtcNow;
        var local = tzi is null ? nowUtc : TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tzi);

        // 12-hour with leading zero + lowercase am/pm
        var time12 = local.ToString("hh:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant();

        // Zone suffix: BST/GMT for Europe/London; otherwise UTC offset fallback
        string zoneSuffix;
        if (tzi != null && (
                string.Equals(tzi.Id, "Europe/London", StringComparison.OrdinalIgnoreCase) ||
                tzi.DisplayName.Contains("London", StringComparison.OrdinalIgnoreCase)))
        {
            zoneSuffix = tzi.IsDaylightSavingTime(local) ? "BST" : "GMT";
        }
        else if (tzi != null)
        {
            var off = tzi.GetUtcOffset(local);
            zoneSuffix = off == TimeSpan.Zero
                ? "UTC"
                : $"UTC{(off < TimeSpan.Zero ? "-" : "+")}{off:hh\\:mm}";
        }
        else
        {
            zoneSuffix = "UTC";
        }

        var text = $"⌚ The time for Spiff is: {time12} {zoneSuffix}";
        return Task.FromResult<string?>(text);
    }
}
