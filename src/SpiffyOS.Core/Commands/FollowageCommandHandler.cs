namespace SpiffyOS.Core.Commands;

public sealed class FollowageCommandHandler : ICommandHandler
{
    public async Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        // Who are we checking?
        var targetLogin = (args ?? "").Trim().TrimStart('@');
        string targetUserId;
        string targetLabel; // for output

        // Mods/broadcaster may supply a username; everyone else = self
        bool canTargetOthers = ctx.Message.IsModerator || ctx.Message.IsBroadcaster;

        if (string.IsNullOrEmpty(targetLogin) || !canTargetOthers)
        {
            targetUserId = ctx.Message.ChatterUserId;
            targetLabel = string.IsNullOrWhiteSpace(ctx.Message.ChatterUserName)
                ? ctx.Message.ChatterUserLogin ?? "you"
                : ctx.Message.ChatterUserName!;
        }
        else
        {
            var u = await ctx.Helix.GetUserByLoginAsync(targetLogin, ct);
            if (u is null) return $"User '{targetLogin}' not found.";

            // Use the resolved user
            targetUserId = u.id;
            targetLabel = string.IsNullOrWhiteSpace(u.display_name) ? (u.login ?? targetLogin) : u.display_name!;
        }

        // Use the bot as moderator_id (your bot is a moderator)
        var since = await ctx.Helix.GetFollowSinceAsync(ctx.BroadcasterId, targetUserId, ctx.BotUserId, ct);
        if (since is null) return canTargetOthers
            ? $"😢 {targetLabel} is not following."
            : "😢 You're not following.";

        var age = FormatAge(since.Value, DateTime.UtcNow);
        var sinceStr = since.Value.ToString("yyyy-MM-dd");
        return canTargetOthers
            ? $"📏 Followage for {targetLabel}: {age} (since {sinceStr})"
            : $"📏 You've been following for {age} (since {sinceStr}).";
    }

    private static string FormatAge(DateTime sinceUtc, DateTime nowUtc)
    {
        // y/m/d (calendar-accurate)
        var start = sinceUtc.Date;
        var end = nowUtc.Date;

        int y = 0, m = 0, d = 0;
        while (start.AddYears(1) <= end) { start = start.AddYears(1); y++; }
        while (start.AddMonths(1) <= end) { start = start.AddMonths(1); m++; }
        d = (end - start).Days;

        var parts = new List<string>();
        if (y > 0) parts.Add($"{y}y");
        if (m > 0) parts.Add($"{m}m");
        if (d > 0 || parts.Count == 0) parts.Add($"{d}d");
        return string.Join(" ", parts);
    }
}
