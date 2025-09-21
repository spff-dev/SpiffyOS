namespace SpiffyOS.Core.Commands;

public sealed class ShoutoutCommandHandler : ICommandHandler
{
    public async Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        var target = (args ?? "").Trim().TrimStart('@');
        if (string.IsNullOrEmpty(target)) return null;

        // Requires mod/broadcaster (router also enforces via config)
        if (!(ctx.Message.IsModerator || ctx.Message.IsBroadcaster)) return null;

        var toUser = await ctx.Helix.GetUserByLoginAsync(target, ct);
        if (toUser is null) return null;

        // Official shoutout using BOT token (bot is moderator)
        try
        {
            await ctx.Helix.ShoutoutAsync(
                fromBroadcasterId: ctx.BroadcasterId,
                toBroadcasterId: toUser.id,
                moderatorUserId: ctx.BotUserId,
                ct
            );
        }
        catch
        {
            // Swallow errors (cooldown, etc.). We’ll still post the chat announcement below.
        }

        // Build the configurable-style announcement message: live/offline + game
        bool isLive = await ctx.Helix.IsLiveAsync(toUser.id, ct);
        var ch = await ctx.Helix.GetChannelInfoAsync(toUser.id, ct);
        var gameName = ch?.game_name ?? "Just Chatting";

        // Default green announcement if not overridden elsewhere
        var msg = isLive
            ? $"👍 Please consider following the lovely {toUser.display_name} - they're LIVE NOW streaming a bit of {gameName}! https://www.twitch.tv/{toUser.login}"
            : $"👍 Please consider following the lovely {toUser.display_name} - they were last seen streaming a bit of {gameName}! https://www.twitch.tv/{toUser.login}";

        try
        {
            await ctx.Helix.SendAnnouncementAsync(ctx.BroadcasterId, ctx.BotUserId, msg, color: "green", ct);
        }
        catch
        {
            // keep silent on any errors
        }

        // No additional message from the command itself (avoid duplicate text)
        return null;
    }
}

