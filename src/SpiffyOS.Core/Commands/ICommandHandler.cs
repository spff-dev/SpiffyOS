namespace SpiffyOS.Core.Commands;

public sealed class CommandContext
{
    public required SpiffyOS.Core.HelixApi Helix { get; init; }
    public required string BroadcasterId { get; init; }
    public required string BotUserId { get; init; }
    public required EventSubWebSocket.ChatMessage Message { get; init; }
    public SpiffyOS.Core.Data.DataStore? Store { get; init; }
}

public interface ICommandHandler
{
    // Return the message to send (or null if nothing to send).
    Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct);
}
