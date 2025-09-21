using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SpiffyOS.Core.Announcements;

public sealed class AnnouncementsConfigProvider : IDisposable
{
    private readonly string _path;
    private readonly ILogger<AnnouncementsConfigProvider> _log;
    private readonly FileSystemWatcher _fsw;

    private AnnouncementsConfig _current = new();

    public AnnouncementsConfigProvider(string configDir, ILogger<AnnouncementsConfigProvider> log)
    {
        _path = Path.Combine(configDir, "announcements.json");
        _log = log;

        Load();

        _fsw = new FileSystemWatcher(configDir, "announcements.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
        };
        _fsw.Changed += (_, __) => SafeReload();
        _fsw.Created += (_, __) => SafeReload();
        _fsw.Renamed += (_, __) => SafeReload();
        _fsw.EnableRaisingEvents = true;
    }

    public AnnouncementsConfig Snapshot()
    {
        // shallow copy is fine for read-only use by callers
        return _current;
    }

    private void SafeReload()
    {
        try { Load(); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to reload announcements.json"); }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            _log.LogInformation("Announcements config not found at {Path}; using defaults (disabled=false by file presence).", _path);
            _current = new AnnouncementsConfig { Enabled = false }; // if file missing, default to off
            return;
        }

        var json = File.ReadAllText(_path);
        var cfg = JsonSerializer.Deserialize<AnnouncementsConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new AnnouncementsConfig();

        // normalize weights
        foreach (var m in cfg.Messages)
            if (m.Weight < 1) m.Weight = 1;

        _current = cfg;
        _log.LogInformation("AnnouncementsConfig loaded: Enabled={Enabled} OnlineOnly={OnlineOnly} Messages={Count}",
            _current.Enabled, _current.OnlineOnly, _current.Messages.Count);
    }

    public void Dispose() => _fsw.Dispose();
}
