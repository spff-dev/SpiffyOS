using Microsoft.Extensions.Logging;
using SpiffyOS.Core.ModTools;

namespace SpiffyOS.Core.Commands;

public sealed class GameCommandHandler : ICommandHandler
{
    private readonly ILogger _log;
    private readonly ModToolsConfigLoader _cfg;

    private static DateTime _nextSetAllowed = DateTime.MinValue;
    private static readonly object _lock = new();

    public GameCommandHandler(ILogger logger, string configDir)
    {
        _log = logger;
        _cfg = ModToolsConfigLoader.ForConfigDir(configDir);
    }

    public async Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        var trimmed = (args ?? "").Trim();

        // Getter: anyone
        if (string.IsNullOrEmpty(trimmed))
        {
            var ch = await ctx.Helix.GetChannelInfoAsync(ctx.BroadcasterId, ct);
            var g = ch?.game_name?.Trim();
            return string.IsNullOrEmpty(g) ? "Current category: (none)" : $"Current category: {g}";
        }

        // Setter: mod/broadcaster only + cooldown
        if (!(ctx.Message.IsModerator || ctx.Message.IsBroadcaster)) return null;

        int cd = Math.Max(0, _cfg.Current.Cooldowns.GameChangeSeconds);
        lock (_lock)
        {
            if (DateTime.UtcNow < _nextSetAllowed) return null; // silent
            _nextSetAllowed = DateTime.UtcNow.AddSeconds(cd);
        }

        string? gameId = null;
        string? gameName = null;

        // If args is numeric id, use directly
        if (trimmed.All(char.IsDigit))
        {
            gameId = trimmed;
            // We can still fetch a friendly name for confirmation
            try
            {
                var byName = await ctx.Helix.FindGameAsync(trimmed, ct);
                if (byName is not null) gameName = byName.Value.name;
            }
            catch { }
        }
        else
        {
            var found = await ctx.Helix.FindGameAsync(trimmed, ct);
            if (found is null) return null;
            gameId = found.Value.id;
            gameName = found.Value.name;
        }

        var ok = await ctx.Helix.UpdateGameAsync(ctx.BroadcasterId, gameId!, ct);
        if (!ok) return null;

        var show = gameName ?? trimmed;
        _log.LogInformation("Category changed by {Login} ({Id}) -> \"{Game}\"",
            ctx.Message.ChatterUserLogin, ctx.Message.ChatterUserId, show);

        return $"Game changed to --> {show}";
    }
}
