using Microsoft.AspNetCore.Http;
using Serilog;

var logsDir = Environment.GetEnvironmentVariable("SPIFFYOS_LOGS")
               ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../logs"));
Directory.CreateDirectory(logsDir);

// Structured logs to console + rolling files
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "{Timestamp:HH:mm:ss} {Level:u3} {SourceContext}: {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(Path.Combine(logsDir, "overlay-.log"),
                  rollingInterval: RollingInterval.Day,
                  retainedFileCountLimit: 30,
                  shared: true)
    .CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    var portStr = Environment.GetEnvironmentVariable("SPIFFYOS_OVERLAY_PORT");
    if (!int.TryParse(portStr, out var port)) port = 5100;

    var overlayToken = Environment.GetEnvironmentVariable("SPIFFYOS_OVERLAY_TOKEN");

    builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));
    builder.Services.AddRouting();
    builder.Services.AddResponseCompression();

    var app = builder.Build();

    Log.Information("Overlay starting on :{Port}", port);

    // Health check (always open)
    app.MapGet("/healthz", () => Results.Text("ok"));

    // Shared-secret gate: require ?k=<token> for everything except /healthz
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/healthz"))
        {
            await next();
            return;
        }

        if (string.IsNullOrWhiteSpace(overlayToken))
        {
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await ctx.Response.WriteAsync("Overlay token not configured");
            return;
        }

        var provided = ctx.Request.Query["k"].ToString();
        if (!string.Equals(provided, overlayToken, StringComparison.Ordinal))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsync("Forbidden");
            return;
        }

        await next();
    });

    // Basic index (you can replace with static files later)
    app.MapGet("/", () =>
    {
        var html = """
        <!doctype html>
        <html>
          <head><meta charset="utf-8"><title>SpiffyOS Overlay</title></head>
          <body style="margin:0;background:transparent;color:#fff;font-family:system-ui,Segoe UI,Arial">
            <div style="position:fixed;top:10px;left:10px;padding:8px 12px;background:#0008;border-radius:8px;">
              Overlay OK — secured by shared token
            </div>
          </body>
        </html>
        """;
        return Results.Content(html, "text/html");
    });

    // If you later add assets under wwwroot/, enable these:
    // app.UseDefaultFiles();
    // app.UseStaticFiles();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Overlay host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
