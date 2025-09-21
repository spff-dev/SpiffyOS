namespace SpiffyOS.Core.Commands;

public sealed class UptimeCommandHandler : ICommandHandler
{
    public async Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        var uptime = await ctx.Helix.GetUptimeAsync(ctx.BroadcasterId, ct);
        if (uptime is null) return "Stream offline";

        var t = uptime.Value;
        var fmt = $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"Uptime: {fmt}";
    }
}
