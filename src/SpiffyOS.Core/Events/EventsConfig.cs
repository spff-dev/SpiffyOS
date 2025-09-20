using System.Text.Json;

namespace SpiffyOS.Core.Events;

public sealed class EventsConfigProvider
{
    private readonly string _path;
    private readonly object _lock = new();
    private EventsConfig _cfg = new();

    public EventsConfigProvider(string configDir)
    {
        _path = Path.Combine(configDir, "events.json");
        Load();
        Watch(configDir);
    }

    public EventsConfig Snapshot()
    {
        lock (_lock) return _cfg;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) { lock (_lock) _cfg = new(); return; }
            var json = File.ReadAllText(_path);
            var cfg = JsonSerializer.Deserialize<EventsConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new EventsConfig();
            lock (_lock) _cfg = cfg;
            Console.WriteLine($"EventsConfig loaded from {_path}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EventsConfig load error: {ex.Message}");
        }
    }

    private void Watch(string dir)
    {
        try
        {
            var fsw = new FileSystemWatcher(dir, "events.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            fsw.Changed += (_, __) => Load();
            fsw.Created += (_, __) => Load();
            fsw.Renamed += (_, __) => Load();
            fsw.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"EventsConfig watcher error: {ex.Message}");
        }
    }
}

public sealed class EventsConfig
{
    public double RateLimitSeconds { get; set; } = 1.2;
    public FollowsConfig Follows { get; set; } = new();
    public SubsConfig Subs { get; set; } = new();
    public BitsConfig Bits { get; set; } = new();
    public RaidsConfig Raids { get; set; } = new();
    public RedemptionsConfig Redemptions { get; set; } = new();
}

public sealed class FollowsConfig
{
    public bool Enabled { get; set; } = true;
    public int CooldownSeconds { get; set; } = 5;
    public int DedupeWindowSeconds { get; set; } = 15;
    public string Template { get; set; } = "❤️ Thanks for the follow, {user.name}!";
    public FollowBatchingConfig Batching { get; set; } = new();
}

public sealed class FollowBatchingConfig
{
    public bool Enabled { get; set; } = false;
    public int WindowSeconds { get; set; } = 20;
    public string Template { get; set; } = "❤️ Thanks for the follows: {user.list}";
}

public sealed class SubsConfig
{
    public bool Enabled { get; set; } = false;
    public int CooldownSeconds { get; set; } = 3;
    public string TemplateNew { get; set; } = "🎉 {user.name} just subscribed at {sub.tier}!";
    public string TemplateGift { get; set; } = "🎁 {gifter.name} gifted a sub to {user.name}!";
    public string TemplateResub { get; set; } = "🔁 {user.name} resubbed ({sub.months} months)!";
    public string TemplateMessage { get; set; } = "💬 {user.name}: {message}";
}

public sealed class BitsConfig
{
    public bool Enabled { get; set; } = false;
    public int CooldownSeconds { get; set; } = 2;
    public string Template { get; set; } = "✨ {user.name} cheered {bits.amount} bits!";
}

public sealed class RaidsConfig
{
    public bool Enabled { get; set; } = false;
    public int CooldownSeconds { get; set; } = 5;
    public string Template { get; set; } = "🚀 Raid from {raider.name} with {raider.viewers} viewers — welcome in!";
}

public sealed class RedemptionsConfig
{
    public bool Enabled { get; set; } = false;
    public int CooldownSeconds { get; set; } = 2;
    public string Template { get; set; } = "🟣 {user.name} redeemed “{reward.title}”{reward.input}";
}
