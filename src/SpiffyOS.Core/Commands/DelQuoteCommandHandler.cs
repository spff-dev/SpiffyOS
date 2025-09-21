namespace SpiffyOS.Core.Commands;

public sealed class DelQuoteCommandHandler : ICommandHandler
{
    public async Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        var store = ctx.Store;
        if (store is null) return null;

        if (!long.TryParse((args ?? "").Trim(), out var id))
            return "Usage: !delquote <id>";

        var ok = await store.DeleteQuoteAsync(id, ct);
        return ok ? $"Quote #{id} deleted." : $"No quote with id {id}.";
    }
}
