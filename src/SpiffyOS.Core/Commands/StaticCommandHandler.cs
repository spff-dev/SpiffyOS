using System.Text.Json;

namespace SpiffyOS.Core.Commands;

public sealed class StaticCommandHandler : ICommandHandler
{
    public Task<string?> ExecuteAsync(CommandContext ctx, CommandDef def, string args, CancellationToken ct)
    {
        if (def.Data.ValueKind == JsonValueKind.Object &&
            def.Data.TryGetProperty("text", out var textEl))
        {
            var val = textEl.GetString();
            return Task.FromResult<string?>(string.IsNullOrWhiteSpace(val) ? null : val);
        }
        return Task.FromResult<string?>(null);
    }
}