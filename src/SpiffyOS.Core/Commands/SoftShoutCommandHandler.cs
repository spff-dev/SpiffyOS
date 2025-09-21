using System.Text.Json;

namespace SpiffyOS.Core.Commands;

public sealed class SoftShoutCommandHandler : ICommandHandler
{
    public async Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        // target username required (silent if missing)
        var target = (args ?? "").Trim();
        if (string.IsNullOrWhiteSpace(target)) return null;
        if (target.StartsWith("@")) target = target[1..];

        // Resolve target user
        var user = await ctx.Helix.GetUserByLoginAsync(target, ct);
        if (user is null) return null;

        // Check live state (for template choice)
        var streamInfo = await ctx.Helix.GetStreamAsync(user.id, ct);
        var isLive = streamInfo?.data?.Count > 0;

        // Always read category from Channels endpoint (consistent field)
        var ch = await ctx.Helix.GetChannelInfoAsync(user.id, ct);
        var game = ch?.game_name;
        string gameSafe = string.IsNullOrWhiteSpace(game) ? "something great" : game!;

        // Defaults (overridable via def.Data)
        string color = "green";
        string liveTpl = "👍 Please consider following the lovely {name} - they are LIVE NOW streaming {game} ➡️ https://www.twitch.tv/{user.name}";
        string offlineTpl = "👍 Please consider following the lovely {name} - they were last seen streaming {game} ➡️ https://www.twitch.tv/{user.name}";

        try
        {
            var data = def.Data;
            if (data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("announce_color", out var c) && c.ValueKind == JsonValueKind.String)
                    color = c.GetString() ?? color;
                if (data.TryGetProperty("announce_live", out var al) && al.ValueKind == JsonValueKind.String)
                    liveTpl = al.GetString() ?? liveTpl;
                if (data.TryGetProperty("announce_offline", out var ao) && ao.ValueKind == JsonValueKind.String)
                    offlineTpl = ao.GetString() ?? offlineTpl;
            }
        }
        catch { /* keep defaults */ }

        string display = string.IsNullOrWhiteSpace(user.display_name) ? user.login : user.display_name!;
        string urlName = user.login;

        string text = (isLive ? liveTpl : offlineTpl)
            .Replace("{name}", display)
            .Replace("{user.name}", urlName)
            .Replace("{game}", gameSafe);

        // Announcement only; no normal chat line
        await ctx.Helix.SendAnnouncementAsync(ctx.BroadcasterId, ctx.BotUserId, text, color, ct);

        return null;
    }
}
