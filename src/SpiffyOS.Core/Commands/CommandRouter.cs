using System.Text.Json;

namespace SpiffyOS.Core.Commands;

public sealed class CommandRouter
{
    private readonly SpiffyOS.Core.HelixApi _helix;
    private readonly string _broadcasterId;
    private readonly string _botUserId;
    private readonly string _configPath;
    private readonly object _lock = new();

    private CommandFile _cfg = new();
    private Dictionary<string, CommandDef> _byName = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> _aliasToName = new(StringComparer.OrdinalIgnoreCase);

    // cooldowns/usages (per-stream)
    private readonly Dictionary<string, DateTime> _nextGlobal = new();                            // cmd -> time
    private readonly Dictionary<(string cmd, string user), DateTime> _nextUser = new();           // (cmd,user) -> time
    private readonly Dictionary<string, int> _globalUsage = new();                                // cmd -> count
    private readonly Dictionary<(string cmd, string user), int> _userUsage = new();               // (cmd,user) -> count
    private string? _currentStreamId;
    private DateTime _lastStreamCheck = DateTime.MinValue;

    private readonly StaticCommandHandler _static = new();
    private readonly UptimeCommandHandler _uptime = new();

    public CommandRouter(SpiffyOS.Core.HelixApi helix, string broadcasterId, string botUserId, string configDir)
    {
        _helix = helix;
        _broadcasterId = broadcasterId;
        _botUserId = botUserId;
        _configPath = Path.Combine(configDir, "commands.json");
        LoadConfig();
        WatchConfig(configDir);
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                _cfg = new CommandFile();
                _byName.Clear();
                _aliasToName.Clear();
                return;
            }
            var json = File.ReadAllText(_configPath);
            var cfg = JsonSerializer.Deserialize<CommandFile>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new CommandFile();

            var byName = new Dictionary<string, CommandDef>(StringComparer.OrdinalIgnoreCase);
            var aliasTo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in cfg.Commands)
            {
                if (string.IsNullOrWhiteSpace(c.Name)) continue;
                byName[c.Name] = c;
                foreach (var a in c.Aliases ?? new())
                    aliasTo[a] = c.Name;
            }

            lock (_lock)
            {
                _cfg = cfg;
                _byName = byName;
                _aliasToName = aliasTo;
                // Reset cooldowns for safety on config change
                _nextGlobal.Clear(); _nextUser.Clear(); _globalUsage.Clear(); _userUsage.Clear();
            }

            Console.WriteLine($"CommandRouter loaded {_byName.Count} commands (prefix '{_cfg.Prefix}') from {_configPath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CommandRouter config load error: {ex.Message}");
        }
    }

    private void WatchConfig(string dir)
    {
        try
        {
            var fsw = new FileSystemWatcher(dir, "commands.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };
            fsw.Changed += (_, __) => LoadConfig();
            fsw.Created += (_, __) => LoadConfig();
            fsw.Renamed += (_, __) => LoadConfig();
            fsw.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CommandRouter watcher error: {ex.Message}");
        }
    }

    public async Task HandleAsync(EventSubWebSocket.ChatMessage msg, CancellationToken ct)
    {
        CommandDef? def;
        string args = "";
        string prefix;

        lock (_lock) { prefix = _cfg.Prefix; }

        if (string.IsNullOrWhiteSpace(msg.Text) || !msg.Text.StartsWith(prefix)) return;

        var body = msg.Text[prefix.Length..].Trim();
        if (string.IsNullOrEmpty(body)) return;

        var parts = body.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var token = parts[0];
        args = parts.Length > 1 ? parts[1] : "";

        // Resolve to canonical name via aliases
        string name;
        lock (_lock)
        {
            if (_byName.ContainsKey(token)) name = token;
            else if (_aliasToName.TryGetValue(token, out var canon)) name = canon;
            else return; // unknown command
            def = _byName[name];
        }

        if (def is null) return;

        // Access check
        if (!HasPermission(def, msg)) { Console.WriteLine($"Permission denied for {token}"); return; }

        // Per-stream usage accounting (refresh stream id occasionally)
        await EnsureStreamContext(ct);

        // Cooldowns & usage checks
        if (!AllowByCooldowns(def, name, msg.ChatterUserId)) { Console.WriteLine($"Cooldown active for {name}"); return; }
        if (!AllowByUsage(def, name, msg.ChatterUserId)) { Console.WriteLine($"Usage limit reached for {name}"); return; }

        // Execute
        string? text = null;
        if (def.Type.Equals("static", StringComparison.OrdinalIgnoreCase))
            text = await _static.ExecuteAsync(BuildCtx(msg), def, args, ct);
        else if (def.Type.Equals("dynamic", StringComparison.OrdinalIgnoreCase))
        {
            // Only 'uptime' implemented for now
            if (def.Name.Equals("uptime", StringComparison.OrdinalIgnoreCase))
                text = await _uptime.ExecuteAsync(BuildCtx(msg), def, args, ct);
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            var replyId = def.ReplyToUser ? msg.MessageId : null;
            await _helix.SendChatMessageWithAppAsync(_broadcasterId, _botUserId, text!, ct, replyId);
            TouchCooldowns(def, name, msg.ChatterUserId);
            TouchUsage(def, name, msg.ChatterUserId);
            Console.WriteLine($"Command '{name}' sent.");
        }
    }

    private CommandContext BuildCtx(EventSubWebSocket.ChatMessage msg) => new()
    {
        Helix = _helix,
        BroadcasterId = _broadcasterId,
        BotUserId = _botUserId,
        Message = msg
    };

    private bool HasPermission(CommandDef def, EventSubWebSocket.ChatMessage msg)
    {
        var p = CommandPermissionParser.Parse(def.Permission);

        return p switch
        {
            CommandPermission.Everyone => true,
            CommandPermission.Subscriber => msg.IsSubscriber || msg.IsVIP || msg.IsModerator || msg.IsBroadcaster,
            CommandPermission.VIP => msg.IsVIP || msg.IsModerator || msg.IsBroadcaster,
            CommandPermission.Mod => msg.IsModerator || msg.IsBroadcaster,
            CommandPermission.Broadcaster => msg.IsBroadcaster,
            _ => true
        };
    }

    private async Task EnsureStreamContext(CancellationToken ct)
    {
        if ((DateTime.UtcNow - _lastStreamCheck) < TimeSpan.FromSeconds(60)) return;
        _lastStreamCheck = DateTime.UtcNow;

        var r = await _helix.GetStreamAsync(_broadcasterId, ct);
        var id = r.data.FirstOrDefault()?.id;

        if (!string.Equals(id, _currentStreamId, StringComparison.Ordinal))
        {
            _currentStreamId = id;
            // Reset per-stream usage counts
            _globalUsage.Clear();
            _userUsage.Clear();
        }
    }

    private static DateTime Now => DateTime.UtcNow;

    private bool AllowByCooldowns(CommandDef def, string name, string userId)
    {
        var now = Now;

        if (def.GlobalCooldown > 0 &&
            _nextGlobal.TryGetValue(name, out var next) &&
            now < next)
            return false;

        if (def.UserCooldown > 0 &&
            _nextUser.TryGetValue((name, userId), out var nextU) &&
            now < nextU)
            return false;

        return true;
    }

    private void TouchCooldowns(CommandDef def, string name, string userId)
    {
        var now = Now;

        if (def.GlobalCooldown > 0)
            _nextGlobal[name] = now.AddSeconds(def.GlobalCooldown);

        if (def.UserCooldown > 0)
            _nextUser[(name, userId)] = now.AddSeconds(def.UserCooldown);
    }

    private bool AllowByUsage(CommandDef def, string name, string userId)
    {
        if (def.GlobalUsage > 0)
        {
            _globalUsage.TryGetValue(name, out var c);
            if (c >= def.GlobalUsage) return false;
        }

        if (def.UserUsage > 0)
        {
            _userUsage.TryGetValue((name, userId), out var cu);
            if (cu >= def.UserUsage) return false;
        }

        return true;
    }

    private void TouchUsage(CommandDef def, string name, string userId)
    {
        if (def.GlobalUsage > 0)
            _globalUsage[name] = _globalUsage.TryGetValue(name, out var c) ? c + 1 : 1;

        if (def.UserUsage > 0)
        {
            var key = (name, userId);
            _userUsage[key] = _userUsage.TryGetValue(key, out var cu) ? cu + 1 : 1;
        }
    }
}
