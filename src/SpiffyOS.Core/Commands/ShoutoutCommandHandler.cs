using Microsoft.Extensions.Logging;
using SpiffyOS.Core.ModTools;

namespace SpiffyOS.Core.Commands;

public sealed class ShoutoutCommandHandler : ICommandHandler
{
    private readonly ILogger _log;
    private readonly ModToolsConfigLoader _cfg;

    public ShoutoutCommandHandler(ILogger logger, string configDir)
    {
        _log = logger;
        _cfg = ModToolsConfigLoader.ForConfigDir(configDir);
    }

    public async Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        // must be mod/broadcaster
        if (!(ctx.Message.IsModerator || ctx.Message.IsBroadcaster))
            return null;

        var login = (args ?? "").Trim().TrimStart('@');
        if (string.IsNullOrWhiteSpace(login)) return null;

        var user = await ctx.Helix.GetUserByLoginAsync(login, ct);
        if (user is null) return null;

        // Try shoutout (official API) — failure is OK; we still /announce
        try
        {
            await ctx.Helix.ShoutoutAsync(ctx.BroadcasterId, user.id, ctx.BotUserId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Shoutout API failed for {Login} ({Id})", user.login, user.id);
        }

        // Build announcement
        var cfg = _cfg.Current;
        if (!cfg.Shoutout.Enabled) return null;

        // Determine live + game
        string? game = null;
        bool isLive = false;
        try
        {
            var s = await ctx.Helix.GetStreamAsync(user.id, ct);
            var item = s?.data?.FirstOrDefault();
            if (item is not null)
            {
                isLive = true;
                game = string.IsNullOrWhiteSpace(item.game_name) ? null : item.game_name;
            }
            else
            {
                var ch = await ctx.Helix.GetChannelInfoAsync(user.id, ct);
                game = string.IsNullOrWhiteSpace(ch?.game_name) ? null : ch!.game_name;
            }
        }
        catch { /* optional */ }

        var template = isLive ? cfg.Shoutout.LiveTemplate : cfg.Shoutout.OfflineTemplate;
        var text = template
            .Replace("{user.display}", user.display_name)
            .Replace("{user.login}", user.login)
            .Replace("{game}", game ?? "something great");

        try
        {
            await ctx.Helix.SendAnnouncementAsync(ctx.BroadcasterId, ctx.BotUserId, text, cfg.Shoutout.AnnouncementColor, ct);
            _log.LogInformation("Shoutout announced: target={Login} ({Id}) live={Live} game=\"{Game}\"",
                user.login, user.id, isLive, game ?? "(none)");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to send announcement for shoutout target={Login} ({Id})", user.login, user.id);
        }

        // We don't return a normal chat message; announcement already posted.
        return null;
    }
}
