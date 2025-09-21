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

        // If you want to make the format tweakable later, we can, but keep it simple for now.
        var label = tzi?.DisplayName ?? (tzId + " (UTC fallback)");
        var text = $"Current time ({label}): {local:yyyy-MM-dd HH:mm:ss}";

        return Task.FromResult<string?>(text);
    }
}
