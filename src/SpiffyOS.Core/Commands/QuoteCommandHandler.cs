namespace SpiffyOS.Core.Commands;

public sealed class QuoteCommandHandler : ICommandHandler
{
    public async Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        var store = ctx.Store;
        if (store is null) return null; // silently ignore if persistence not available

        args = (args ?? "").Trim();
        if (string.IsNullOrEmpty(args))
        {
            var r = await store.GetRandomQuoteAsync(ct);
            return r is null ? "No quotes yet." : $"Quote #{r.Value.id}: {r.Value.text}";
        }

        // id lookup?
        if (long.TryParse(args, out var id))
        {
            var r = await store.GetQuoteByIdAsync(id, ct);
            return r is null ? $"No quote with id {id}." : $"Quote #{r.Value.id}: {r.Value.text}";
        }

        // text search
        var s = await store.SearchQuoteAsync(args, ct);
        return s is null ? "No matching quote found." : $"Quote #{s.Value.id}: {s.Value.text}";
    }
}
