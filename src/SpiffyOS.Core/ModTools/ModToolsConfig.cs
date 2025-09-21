using System.Text.Json;

namespace SpiffyOS.Core.ModTools;

public sealed class ModToolsConfig
{
    public ShoutoutConfig Shoutout { get; set; } = new();
    public CooldownsConfig Cooldowns { get; set; } = new();
    public SanitizationConfig Sanitization { get; set; } = new();

    public sealed class ShoutoutConfig
    {
        public bool Enabled { get; set; } = true;
        public bool AlwaysAnnouncement { get; set; } = true;
        public string AnnouncementColor { get; set; } = "green"; // primary|blue|green|orange|purple
        public string LiveTemplate { get; set; } =
            "Please go and follow {user.display}! They were last seen streaming {game} — https://www.twitch.tv/{user.login}";
        public string OfflineTemplate { get; set; } =
            "Please go and follow {user.display}! Catch them at https://www.twitch.tv/{user.login}";
    }

    public sealed class CooldownsConfig
    {
        public int GameChangeSeconds { get; set; } = 5;
        public int TitleChangeSeconds { get; set; } = 5;
    }

    public sealed class SanitizationConfig
    {
        public bool CollapseWhitespace { get; set; } = true;
        public bool StripControlChars { get; set; } = true;
        public bool Trim { get; set; } = true;
    }
}

public sealed class ModToolsConfigLoader
{
    private readonly string _path;
    private ModToolsConfig _cfg = new();
    private readonly object _lock = new();

    private ModToolsConfigLoader(string path)
    {
        _path = path;
        Load();
        try
        {
            var fsw = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            fsw.Changed += (_, __) => Load();
            fsw.Created += (_, __) => Load();
            fsw.Renamed += (_, __) => Load();
            fsw.EnableRaisingEvents = true;
        }
        catch { /* non-fatal */ }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var cfg = JsonSerializer.Deserialize<ModToolsConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new ModToolsConfig();
                lock (_lock) { _cfg = cfg; }
            }
        }
        catch { /* keep old */ }
    }

    public ModToolsConfig Current { get { lock (_lock) return _cfg; } }

    // cache per path
    private static readonly Dictionary<string, ModToolsConfigLoader> _byPath = new(StringComparer.OrdinalIgnoreCase);
    public static ModToolsConfigLoader ForConfigDir(string configDir)
    {
        var path = Path.Combine(configDir, "modtools.json");
        lock (_byPath)
        {
            if (!_byPath.TryGetValue(path, out var l))
                _byPath[path] = l = new ModToolsConfigLoader(path);
            return l;
        }
    }
}
