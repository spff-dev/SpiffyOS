namespace SpiffyOS.Core.Commands;

public sealed class UptimeCommandHandler : ICommandHandler
{
    public async Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        // Uses HelixApi.GetUptimeAsync() which returns the UTC start time or null
        var startedAt = await ctx.Helix.GetUptimeAsync(ctx.BroadcasterId, ct);
        if (startedAt is null) return "Stream offline";

        var delta = DateTime.UtcNow - startedAt.Value; // TimeSpan
        string fmt = $"{(int)delta.TotalHours:D2}:{delta.Minutes:D2}:{delta.Seconds:D2}";
        return $"Uptime: {fmt}";
    }
}
