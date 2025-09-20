namespace SpiffyOS.Core.Commands;

public sealed class CommandFile
{
    public string Prefix { get; set; } = "!";
    public List<CommandDef> Commands { get; set; } = new();
}

public sealed class CommandDef
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "static"; // "static" | "dynamic"
    public List<string> Aliases { get; set; } = new();

    public string Permission { get; set; } = "everyone"; // everyone|subscriber|vip|mod|broadcaster
    public int GlobalCooldown { get; set; } = 0;         // seconds
    public int UserCooldown { get; set; } = 0;           // seconds
    public int GlobalUsage { get; set; } = 0;            // 0 = unlimited
    public int UserUsage { get; set; } = 0;              // 0 = unlimited

    public bool ReplyToUser { get; set; } = false;

    // Free-form data; static uses { text }, dynamic handlers ignore or use as needed
    public System.Text.Json.JsonElement Data { get; set; }
}

public enum CommandPermission
{
    Everyone, Subscriber, VIP, Mod, Broadcaster
}

public static class CommandPermissionParser
{
    public static CommandPermission Parse(string s) => (s ?? "").ToLowerInvariant() switch
    {
        "broadcaster" => CommandPermission.Broadcaster,
        "mod" => CommandPermission.Mod,
        "vip" => CommandPermission.VIP,
        "subscriber" => CommandPermission.Subscriber,
        _ => CommandPermission.Everyone
    };
}