using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using SpiffyOS.Core;
using SpiffyOS.Core.Commands;
using SpiffyOS.Core.Events;
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

        // App token (Send Chat Message + general Helix app calls)
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

        // Helix (broadcaster auth + app token for /chat/messages)
        services.AddSingleton(sp => new HelixApi(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<BroadcasterAuth>().Value,
            cfg["Twitch:ClientId"]!,
            sp.GetRequiredService<AppTokenProvider>()
        ));

        // EventSub sockets (order matters when we enumerate later):
        // 0) BOT socket: chat + follows
        services.AddSingleton(sp => new EventSubWebSocket(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<BotAuth>().Value,
            cfg["Twitch:ClientId"]!,
            sp.GetRequiredService<AppTokenProvider>(),
            sp.GetRequiredService<ILogger<EventSubWebSocket>>()
        ));
        // 1) BROADCASTER socket: subs + resub messages (later bits/raids/redemptions)
        services.AddSingleton(sp => new EventSubWebSocket(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<BroadcasterAuth>().Value,
            cfg["Twitch:ClientId"]!,
            sp.GetRequiredService<AppTokenProvider>(),
            sp.GetRequiredService<ILogger<EventSubWebSocket>>()
        ));

        // Commands
        services.AddSingleton(sp => new CommandRouter(
            sp.GetRequiredService<HelixApi>(),
            sp.GetRequiredService<ILogger<CommandRouter>>(),
            cfg["Twitch:BroadcasterId"]!, cfg["Twitch:BotUserId"]!,
            configDir
        ));

        // Events
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

public sealed record BroadcasterAuth(TwitchAuth Value);
public sealed record BotAuth(TwitchAuth Value);

public sealed class BotService : BackgroundService
{
    private readonly ILogger<BotService> _log;
    private readonly IConfiguration _cfg;
    private readonly IEnumerable<EventSubWebSocket> _sockets;
    private readonly CommandRouter _router;
    private readonly EventsAnnouncer _events;
    private readonly BootDirs _dirs;

    private string BroadcasterId => _cfg["Twitch:BroadcasterId"]!;
    private string BotUserId => _cfg["Twitch:BotUserId"]!;
    private string ModeratorId => _cfg["Twitch:ModeratorUserId"] ?? BroadcasterId;

    public BotService(ILogger<BotService> log, IConfiguration cfg,
                      IEnumerable<EventSubWebSocket> sockets, CommandRouter router, EventsAnnouncer events, BootDirs dirs)
    { _log = log; _cfg = cfg; _sockets = sockets; _router = router; _events = events; _dirs = dirs; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("Paths: CONFIG={Config} TOKENS={Tokens} LOGS={Logs}", _dirs.Config, _dirs.Tokens, _dirs.Logs);

        try
        {
            // sockets[0] => BOT; sockets[1] => BROADCASTER (registered in that order above)
            var arr = _sockets.ToArray();
            var botSock = arr[0];
            var brdSock = arr[1];

            await botSock.ConnectAsync(stoppingToken);
            await brdSock.ConnectAsync(stoppingToken);

            await botSock.EnsureSubscriptionsBotAsync(BroadcasterId, ModeratorId, BotUserId, stoppingToken);
            await brdSock.EnsureSubscriptionsBroadcasterAsync(BroadcasterId, stoppingToken);

            botSock.ChatMessageReceived += async msg =>
            {
                try { await _router.HandleAsync(msg, stoppingToken); }
                catch (Exception ex) { _log.LogError(ex, "Command router error"); }
            };

            botSock.FollowReceived += async ev =>
            {
                try { await _events.HandleFollowAsync(ev, stoppingToken); }
                catch (Exception ex) { _log.LogError(ex, "Follow announce error"); }
            };

            brdSock.SubscriptionReceived += async ev =>
            {
                try { await _events.HandleSubscribeAsync(ev, stoppingToken); }
                catch (Exception ex) { _log.LogError(ex, "Sub announce error"); }
            };

            brdSock.SubscriptionMessageReceived += async ev =>
            {
                try { await _events.HandleSubscriptionMessageAsync(ev, stoppingToken); }
                catch (Exception ex) { _log.LogError(ex, "Resub announce error"); }
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
