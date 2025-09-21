using System.Globalization;

namespace SpiffyOS.Core.Commands;

public sealed class FollowageCommandHandler : ICommandHandler
{
    public async Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        // Who are we checking?
        // - Everyone can check themselves (no args)
        // - Mods/Broadcaster can supply a username (arg)
        string? targetLogin = null;

        var arg = (args ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(arg))
        {
            // Only allow targeting others if mod/broadcaster
            if (!(ctx.Message.IsModerator || ctx.Message.IsBroadcaster))
                return null; // silently ignore

            targetLogin = NormalizeLogin(arg);
        }
        else
        {
            // default to the requester (self)
            targetLogin = NormalizeLogin(ctx.Message.ChatterUserLogin ?? "");
        }

        if (string.IsNullOrEmpty(targetLogin))
            return null; // nothing to do

        // Resolve user by login
        var user = await ctx.Helix.GetUserByLoginAsync(targetLogin, ct);
        if (user is null || string.IsNullOrEmpty(user.id))
            return null; // user not found -> silent

        var targetId = user.id;
        var display = !string.IsNullOrWhiteSpace(user.display_name) ? user.display_name : (user.login ?? targetLogin);

        // Special case: you can't follow yourself; if target is the broadcaster, show account creation date instead
        if (string.Equals(targetId, ctx.BroadcasterId, StringComparison.Ordinal))
        {
            // Twitch "Get Users" returns created_at; HelixApi.GetUserByLoginAsync should surface it.
            if (!DateTime.TryParse(user.created_at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var createdUtc))
                return $"❌ {display}'s account creation date isn't available right now.";

            var (local, tzSuffix) = ToLocal(createdUtc);
            var ago = HumanDelta(DateTime.UtcNow - createdUtc);

            // Example: "SPIFFgg's account was created on 2016-05-03 (8y 4m 12d ago)."
            return $"📅 {display}'s account was created on {local:yyyy-MM-dd} ({ago} ago).";
        }

        // Normal followage path
        // Requires moderator privileges; we use the bot's user id (the bot is a moderator on your channel)
        var since = await ctx.Helix.GetFollowSinceAsync(ctx.BroadcasterId, targetId, ctx.BotUserId, ct);
        if (since is null)
            return $"😢 {display} isn't following.";

        var delta = DateTime.UtcNow - since.Value;
        var human = HumanDelta(delta);
        return $"📅 {display} has been following for {human}.";
    }

    private static string NormalizeLogin(string s)
        => (s ?? "").Trim().TrimStart('@', '#').ToLowerInvariant();

    private static (DateTime local, string tzSuffix) ToLocal(DateTime utc)
    {
        var tzId = Environment.GetEnvironmentVariable("SPIFFYOS_TZ") ?? "Europe/London";
        TimeZoneInfo? tzi = null;
        try { tzi = TimeZoneInfo.FindSystemTimeZoneById(tzId); } catch { }
        if (tzi is null) return (utc, "UTC");

        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, tzi);
        var isDst = tzi.IsDaylightSavingTime(local);
        var suffix = tzId == "Europe/London" ? (isDst ? "BST" : "GMT") : tzi.StandardName;
        return (local, suffix);
    }

    private static string HumanDelta(TimeSpan d)
    {
        // Compact “Xy Xm Xd” style; never negative.
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;

        // Break into years/months/days roughly (months=30.44d; years=365.24d for a simple approximation)
        var totalDays = d.TotalDays;
        int years = (int)(totalDays / 365.2425);
        totalDays -= years * 365.2425;
        int months = (int)(totalDays / 30.436875);
        totalDays -= months * 30.436875;
        int days = (int)Math.Floor(totalDays);

        // Ensure we surface something meaningful even for short durations
        if (years > 0) return $"{years}y {months}m {days}d".Trim();
        if (months > 0) return $"{months}m {days}d".Trim();
        if (days > 0) return $"{days}d";

        // Fall back to HH:MM:SS for <1 day
        return $"{(int)d.TotalHours:D2}h {d.Minutes:D2}m {d.Seconds:D2}s";
    }
}
