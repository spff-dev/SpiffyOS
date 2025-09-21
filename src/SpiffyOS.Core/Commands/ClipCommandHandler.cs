using System.Threading;
using System.Threading.Tasks;

namespace SpiffyOS.Core.Commands;

public sealed class ClipCommandHandler : ICommandHandler
{
    public async Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        // If not live → tell the user (threaded via CommandRouter using def.ReplyToUser)
        if (!await ctx.Helix.IsLiveAsync(ctx.BroadcasterId, ct))
            return "❌ Can't clip when offline";

        // Create clip with broadcaster token (has clips:edit)
        var clipId = await ctx.Helix.CreateClipAsync(ctx.BroadcasterId, ct);
        if (string.IsNullOrWhiteSpace(clipId))
            return null; // silent fail (API refused/ratelimit etc.)

        return $"📽️ Here's your clip! → https://clips.twitch.tv/{clipId}";
    }
}
