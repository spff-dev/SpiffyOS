using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using SpiffyOS.Core;
using SpiffyOS.Core.Commands;
using SpiffyOS.Core.Events;
using System.Text.Json;
using Serilog;

var configDir = Environment.GetEnvironmentVariable("SPIFFYOS_CONFIG")
               ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../config"));
var tokensDir = Environment.GetEnvironmentVariable("SPIFFYOS_TOKENS")
               ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../secrets"));
var logsDir = Environment.GetEnvironmentVariable("SPIFFYOS_LOGS")
               ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../logs"));

Directory.CreateDirectory(logsDir);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(logsDir, "bot-.log"),
                  rollingInterval: RollingInterval.Day,
                  retainedFileCountLimit: 30,
                  shared: true)
    .CreateLogger();

var host = Host.CreateDefaultBuilder(args)
    .UseSerilog()
    .ConfigureAppConfiguration((ctx, cfg) =>
    {
        cfg.AddJsonFile(Path.Combine(configDir, "commands.json"), optional: true, reloadOnChange: true);
        cfg.AddJsonFile(Path.Combine(configDir, "events.json"), optional: true, reloadOnChange: true);
        cfg.AddJsonFile(Path.Combine(configDir, "announcements.json"), optional: true, reloadOnChange: true);
    })
    .ConfigureServices((ctx, services) =>
    {
        var cfg = ctx.Configuration;
        services.AddHttpClient();

        // App token provider (for Send Chat Message API + EventSub chat identity classification)
        services.AddSingleton(sp => new AppTokenProvider(
            sp.GetRequiredService<HttpClient>(),
            cfg["Twitch:ClientId"]!, cfg["Twitch:ClientSecret"]!
        ));

        // Broadcaster user token (SPIFFgg) -> secrets/broadcaster.json
        services.AddSingleton(sp => new BroadcasterAuth(new TwitchAuth(
            sp.GetRequiredService<HttpClient>(),
            cfg["Twitch:ClientId"]!, cfg["Twitch:ClientSecret"]!,
            Path.Combine(tokensDir, "broadcaster.json")
        )));

        // Bot user token (SpiffyOS) -> secrets/bot.json
        services.AddSingleton(sp => new BotAuth(new TwitchAuth(
            sp.GetRequiredService<HttpClient>(),
            cfg["Twitch:ClientId"]!, cfg["Twitch:ClientSecret"]!,
            Path.Combine(tokensDir, "bot.json")
        )));

        // Helix + EventSub
        services.AddSingleton(sp => new HelixApi(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<BroadcasterAuth>().Value,
            cfg["Twitch:ClientId"]!,
            sp.GetRequiredService<AppTokenProvider>()
        ));
        services.AddSingleton(sp => new EventSubWebSocket(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<BotAuth>().Value,
            cfg["Twitch:ClientId"]!,
            sp.GetRequiredService<AppTokenProvider>()
        ));

        // Commands
        services.AddSingleton(sp => new CommandRouter(
            sp.GetRequiredService<HelixApi>(),
            sp.GetRequiredService<ILogger<CommandRouter>>(),
            cfg["Twitch:BroadcasterId"]!, cfg["Twitch:BotUserId"]!,
            configDir
        ));

        // Events (follows now; other topics later)
        services.AddSingleton(sp => new EventsConfigProvider(configDir));
        services.AddSingleton(sp => new EventsAnnouncer(
            sp.GetRequiredService<HelixApi>(),
            sp.GetRequiredService<ILogger<EventsAnnouncer>>(),
            sp.GetRequiredService<EventsConfigProvider>(),
            cfg["Twitch:BroadcasterId"]!, cfg["Twitch:BotUserId"]!
        ));

        services.AddHostedService<BotService>();

        services.AddSingleton(new BootDirs(configDir, tokensDir, logsDir));
    })
    .Build();

await host.RunAsync();

public sealed class BootDirs
{
    public string Config { get; }
    public string Tokens { get; }
    public string Logs { get; }
    public BootDirs(string c, string t, string l) { Config = c; Tokens = t; Logs = l; }
}

public sealed class BotService : BackgroundService
{
    private readonly ILogger<BotService> _log;
    private readonly IConfiguration _cfg;
    private readonly EventSubWebSocket _es;
    private readonly CommandRouter _router;
    private readonly EventsAnnouncer _events;
    private readonly BootDirs _dirs;

    private string BroadcasterId => _cfg["Twitch:BroadcasterId"]!;
    private string BotUserId => _cfg["Twitch:BotUserId"]!;
    private string ModeratorId => _cfg["Twitch:ModeratorUserId"] ?? BroadcasterId;

    public BotService(ILogger<BotService> log, IConfiguration cfg,
                      EventSubWebSocket es, CommandRouter router, EventsAnnouncer events, BootDirs dirs)
    { _log = log; _cfg = cfg; _es = es; _router = router; _events = events; _dirs = dirs; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Paths: CONFIG={Config} TOKENS={Tokens} LOGS={Logs}", _dirs.Config, _dirs.Tokens, _dirs.Logs);

        try
        {
            await _es.ConnectAsync(stoppingToken);
            await _es.EnsureSubscriptionsAsync(BroadcasterId, ModeratorId, BotUserId, stoppingToken);

            _es.ChatMessageReceived += async msg =>
            {
                try { await _router.HandleAsync(msg, stoppingToken); }
                catch (Exception ex) { _log.LogError(ex, "Command router error"); }
            };

            _es.FollowReceived += async ev =>
            {
                try { await _events.HandleFollowAsync(ev, stoppingToken); }
                catch (Exception ex) { _log.LogError(ex, "Follow announce error"); }
            };

            _log.LogInformation("EventSub connected and subscriptions created.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "EventSub connection/subscription failed.");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            _log.LogInformation("Bot heartbeat OK");
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }
}

// ---- types must come AFTER top-level statements ----
public sealed record BroadcasterAuth(TwitchAuth Value);
public sealed record BotAuth(TwitchAuth Value);
