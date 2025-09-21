using System.Text.RegularExpressions;

namespace SpiffyOS.Core.Commands;

public sealed class TitleCommandHandler : ICommandHandler
{
    public async Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        var trimmed = (args ?? "").Trim();

        // No args → show current title
        if (string.IsNullOrEmpty(trimmed))
        {
            var ch = await ctx.Helix.GetChannelInfoAsync(ctx.BroadcasterId, ct);
            if (ch is null) return null; // silent on failure
            return $"Current title: {ch.title}";
        }

        // Mutating form requires mod/broadcaster; the router already enforces perms by config,
        // but we'll be extra defensive here:
        if (!(ctx.Message.IsModerator || ctx.Message.IsBroadcaster)) return null;

        // Basic sanitisation
        trimmed = Regex.Replace(trimmed, @"\s+", " ").Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        await ctx.Helix.UpdateTitleAsync(ctx.BroadcasterId, trimmed, ct);
        return $"Title changed to --> {trimmed}";
    }
}
