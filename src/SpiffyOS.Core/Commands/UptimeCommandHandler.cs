namespace SpiffyOS.Core.Commands;

public sealed class UptimeCommandHandler : ICommandHandler
{
    public async Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        var res = await ctx.Helix.GetStreamAsync(ctx.BroadcasterId, ct);
        var stream = res.data.FirstOrDefault();
        if (stream is null) return "Stream offline";

        var delta = DateTime.UtcNow - stream.started_at.ToUniversalTime();
        string fmt = $"{(int)delta.TotalHours:D2}:{delta.Minutes:D2}:{delta.Seconds:D2}";
        return $"Uptime: {fmt}";
    }
}
