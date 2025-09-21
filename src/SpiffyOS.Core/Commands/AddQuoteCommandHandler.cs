namespace SpiffyOS.Core.Commands;

public sealed class AddQuoteCommandHandler : ICommandHandler
{
    public async Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        var store = ctx.Store;
        if (store is null) return null;

        var text = (args ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text)) return "Please provide a quote text.";

        var id = await store.AddQuoteAsync(
            text,
            ctx.Message.ChatterUserId,
            string.IsNullOrWhiteSpace(ctx.Message.ChatterUserLogin) ? ctx.Message.ChatterUserName : ctx.Message.ChatterUserLogin,
            ct);

        return $"Quote #{id} added.";
    }
}
