using System.Text.RegularExpressions;

namespace SpiffyOS.Core.Commands;

public sealed class GameCommandHandler : ICommandHandler
{
    public async Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        var trimmed = (args ?? "").Trim();

        // No args → show current category
        if (string.IsNullOrEmpty(trimmed))
        {
            var ch = await ctx.Helix.GetChannelInfoAsync(ctx.BroadcasterId, ct);
            if (ch is null) return null;
            return $"🎮 Current category: {ch.game_name}";
        }

        // Mutating form requires mod/broadcaster (router also enforces by config)
        if (!(ctx.Message.IsModerator || ctx.Message.IsBroadcaster)) return null;

        // Sanitise
        trimmed = Regex.Replace(trimmed, @"\s+", " ").Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        // Fuzzy search first, then fallback handled inside HelixApi.FindGameAsync
        var game = await ctx.Helix.FindGameAsync(trimmed, ct);
        if (game is null) return null; // silent on not found

        await ctx.Helix.UpdateGameAsync(ctx.BroadcasterId, game.id, ct);
        return $"✅ Category changed to → {game.name}";
    }
}
