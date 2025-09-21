using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;
using SpiffyOS.Core;
using SpiffyOS.Core.Events; // <-- added

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
    .WriteTo.Console()
    .CreateLogger();

var builder = Host.CreateDefaultBuilder(args)
    .UseSerilog((ctx, cfg) =>
    {
        var logsDir = Environment.GetEnvironmentVariable("SPIFFYOS_LOGS") ?? "logs";
        Directory.CreateDirectory(logsDir);
        cfg.MinimumLevel.Debug()
           .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
           .Enrich.FromLogContext()
           .WriteTo.Console()
           .WriteTo.File(Path.Combine(logsDir, $"bot-{DateTime.UtcNow:yyyyMMdd}.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14);
    })
    .ConfigureServices((ctx, services) =>
    {
        services.AddHttpClient();
        services.AddHostedService<BotService>();
    });

var host = builder.Build();
await host.RunAsync();

public sealed class BotService : BackgroundService
{
    private readonly ILogger<BotService> _log;
    private readonly ILoggerFactory _lf;
    private readonly IHttpClientFactory _httpFactory;

    public BotService(ILogger<BotService> log, ILoggerFactory lf, IHttpClientFactory httpFactory)
    {
        _log = log;
        _lf = lf;
        _httpFactory = httpFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var configDir = Environment.GetEnvironmentVariable("SPIFFYOS_CONFIG") ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "config");
        var tokensDir = Environment.GetEnvironmentVariable("SPIFFYOS_TOKENS") ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "secrets");
        var logsDir = Environment.GetEnvironmentVariable("SPIFFYOS_LOGS") ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "logs");

        Directory.CreateDirectory(configDir);
        Directory.CreateDirectory(tokensDir);
        Directory.CreateDirectory(logsDir);

        _log.LogInformation("Paths: CONFIG={Config} TOKENS={Tokens} LOGS={Logs}", configDir, tokensDir, logsDir);

        var appSettingsPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var appSettings = await File.ReadAllTextAsync(appSettingsPath, stoppingToken);
        var twitch = System.Text.Json.JsonSerializer.Deserialize<TwitchSettings>(appSettings, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!.Twitch;

        var broadcasterId = twitch.BroadcasterId;
        var botUserId = twitch.BotUserId;
        var moderatorUserId = twitch.ModeratorUserId ?? broadcasterId;

        var http = _httpFactory.CreateClient();

        var clientId = twitch.ClientId;
        var clientSecret = twitch.ClientSecret;

        // Auth providers (use your TwitchAuth with token file paths)
        var appToken = new AppTokenProvider(http, clientId, clientSecret);
        var botAuth = new TwitchAuth(http, clientId, clientSecret, Path.Combine(tokensDir, "bot.json"));
        var broadcasterAuth = new TwitchAuth(http, clientId, clientSecret, Path.Combine(tokensDir, "broadcaster.json"));

        // Helix API with both user tokens + app token
        var helix = new HelixApi(http, broadcasterAuth, botAuth, clientId, appToken);

        // Correctly typed loggers
        var wsLogger = _lf.CreateLogger<EventSubWebSocket>();
        var routerLogger = _lf.CreateLogger<SpiffyOS.Core.Commands.CommandRouter>();

        // EventSub sockets
        var wsBot = new EventSubWebSocket(http, botAuth, clientId, appToken, wsLogger);
        var wsBroad = new EventSubWebSocket(http, broadcasterAuth, clientId, appToken, wsLogger);

        // Router
        var router = new SpiffyOS.Core.Commands.CommandRouter(helix, routerLogger, broadcasterId, botUserId, configDir);

        // Events config + announcer
        var eventsCfg = new EventsConfigProvider(configDir);
        var announcer = new EventsAnnouncer(
            helix,
            _lf.CreateLogger<EventsAnnouncer>(),
            eventsCfg,
            broadcasterId,
            botUserId
        );

        wsBot.ChatMessageReceived += async (m) =>
        {
            try { await router.HandleAsync(m, stoppingToken); }
            catch (Exception ex) { _log.LogError(ex, "Router error"); }
        };

        // Wire announcer to EventSub events
        wsBot.FollowReceived += ev =>
{
    _log.LogInformation("EVT follow rx: {User} ({Id})", ev.UserName ?? ev.UserLogin ?? "(unknown)", ev.UserId);
    _ = announcer.HandleFollowAsync(ev, stoppingToken);
};
wsBroad.SubscriptionReceived += ev =>
{
    _log.LogInformation("EVT sub rx: {User} Gift={Gift} Tier={Tier}", ev.UserName ?? ev.UserLogin ?? "(unknown)", ev.IsGift, ev.Tier ?? "(none)");
    _ = announcer.HandleSubscribeAsync(ev, stoppingToken);
};
wsBroad.SubscriptionMessageReceived += ev =>
{
    _log.LogInformation("EVT resub rx: {User} Months={Months}", ev.UserName ?? ev.UserLogin ?? "(unknown)", ev.CumulativeMonths);
    _ = announcer.HandleSubscriptionMessageAsync(ev, stoppingToken);
};
wsBroad.RedemptionReceived += ev =>
{
    _log.LogInformation("EVT redemption rx: {Title} by {User} Status={Status}", ev.RewardTitle ?? "(unknown)", ev.UserName ?? ev.UserLogin ?? "(unknown)", ev.Status ?? "");
    _ = announcer.HandleRedemptionAsync(ev, stoppingToken);
};
wsBroad.CheerReceived += ev =>
{
    _log.LogInformation("EVT cheer rx: Bits={Bits} Anon={Anon}", ev.Bits, ev.IsAnonymous);
    _ = announcer.HandleBitsAsync(ev, stoppingToken);
};
wsBroad.RaidReceived += ev =>
{
    _log.LogInformation("EVT raid rx: {From} -> {To} Viewers={Viewers}", ev.FromBroadcasterUserName ?? ev.FromBroadcasterUserLogin ?? "(unknown)", ev.ToBroadcasterUserName ?? ev.ToBroadcasterUserLogin ?? "(unknown)", ev.Viewers);
    _ = announcer.HandleRaidAsync(ev, stoppingToken);
};

        await wsBot.ConnectAsync(stoppingToken);
        await wsBroad.ConnectAsync(stoppingToken);

        await wsBot.EnsureSubscriptionsBotAsync(broadcasterId, moderatorUserId, botUserId, stoppingToken);
        await wsBroad.EnsureSubscriptionsBroadcasterAsync(broadcasterId, stoppingToken);

        _log.LogInformation("Application started. Press Ctrl+C to shut down.");

        while (!stoppingToken.IsCancellationRequested)
        {
            _log.LogInformation("Bot heartbeat OK");
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }

        await wsBot.DisposeAsync();
        await wsBroad.DisposeAsync();
    }

    private sealed class TwitchSettings
    {
        public TwitchApp Twitch { get; set; } = new();
        public sealed class TwitchApp
        {
            public string ClientId { get; set; } = "";
            public string ClientSecret { get; set; } = "";
            public string BroadcasterId { get; set; } = "";
            public string BotUserId { get; set; } = "";
            public string? ModeratorUserId { get; set; }
        }
    }
}
