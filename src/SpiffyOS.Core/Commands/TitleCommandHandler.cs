using Microsoft.Extensions.Logging;
using SpiffyOS.Core.ModTools;
using System.Text;

namespace SpiffyOS.Core.Commands;

public sealed class TitleCommandHandler : ICommandHandler
{
    private readonly ILogger _log;
    private readonly ModToolsConfigLoader _cfg;

    private static DateTime _nextSetAllowed = DateTime.MinValue;
    private static readonly object _lock = new();

    public TitleCommandHandler(ILogger logger, string configDir)
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
            var title = ch?.title?.Trim();
            return string.IsNullOrEmpty(title) ? "Current title: (unknown)" : $"Current title: {title}";
        }

        // Setter: mod/broadcaster only + cooldown
        if (!(ctx.Message.IsModerator || ctx.Message.IsBroadcaster)) return null;

        int cd = Math.Max(0, _cfg.Current.Cooldowns.TitleChangeSeconds);
        lock (_lock)
        {
            if (DateTime.UtcNow < _nextSetAllowed) return null; // silent
            _nextSetAllowed = DateTime.UtcNow.AddSeconds(cd);
        }

        var clean = Sanitize(trimmed, _cfg.Current.Sanitization);
        if (string.IsNullOrWhiteSpace(clean)) return null;

        var ok = await ctx.Helix.UpdateTitleAsync(ctx.BroadcasterId, clean, ct);
        if (!ok) return null;

        _log.LogInformation("Title changed by {Login} ({Id}) -> \"{Title}\"",
            ctx.Message.ChatterUserLogin, ctx.Message.ChatterUserId, clean);

        // Router will thread (replyToUser true) to parent if present
        return $"Title changed to --> {clean}";
    }

    private static string Sanitize(string s, ModToolsConfig.SanitizationConfig z)
    {
        var t = s;
        if (z.StripControlChars)
        {
            var sb = new StringBuilder(t.Length);
            foreach (var ch in t) if (!char.IsControl(ch) || ch == ' ') sb.Append(ch);
            t = sb.ToString();
        }
        if (z.CollapseWhitespace)
        {
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ");
        }
        if (z.Trim) t = t.Trim();
        return t;
    }
}
