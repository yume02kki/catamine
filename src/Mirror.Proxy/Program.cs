using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Options;
using Mirror.Proxy;
using Mirror.Proxy.Capture;
using Mirror.Proxy.Config;
using Mirror.Proxy.Logging;
using Mirror.Proxy.Metrics;
using Mirror.Proxy.Mirroring;
using Npgsql;
using Prometheus;
using Yarp.ReverseProxy.Forwarder;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Yarp", LogLevel.Warning);
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = null; // the proxy forwards bodies of any size; mirroring has its own limit
});

builder.Services.Configure<ProxyOptions>(builder.Configuration.GetSection("Mirror"));
builder.Services.AddHttpForwarder();

builder.Services.AddSingleton(sp =>
{
    var cs = builder.Configuration.GetConnectionString("Postgres")
             ?? "Host=localhost;Database=mirror;Username=mirror;Password=mirror";
    var csb = new NpgsqlConnectionStringBuilder(cs)
    {
        // Fail fast: Postgres problems must never hold resources for long.
        Timeout = 3,
        CommandTimeout = 30,
        KeepAlive = 30,
        ApplicationName = "mirror-proxy",
    };
    if (csb.MaxPoolSize == 100) csb.MaxPoolSize = 10;
    return new NpgsqlDataSourceBuilder(csb.ConnectionString).Build();
});

builder.Services.AddSingleton<MissionConfigStore>();
builder.Services.AddSingleton(sp => new CaptureBudget(sp.GetRequiredService<IOptions<ProxyOptions>>().Value.MaxInFlightCaptureBytes));
builder.Services.AddSingleton<MirrorQueue>();
builder.Services.AddSingleton<LogQueue>();
builder.Services.AddSingleton<DevPipelines>();
builder.Services.AddKeyedSingleton("dev", (sp, _) => DevPipelines.CreateDevClient(sp.GetRequiredService<IOptions<ProxyOptions>>().Value));
builder.Services.AddKeyedSingleton("main", (sp, _) =>
{
    var o = sp.GetRequiredService<IOptions<ProxyOptions>>().Value;
    return new HttpMessageInvoker(new SocketsHttpHandler
    {
        UseProxy = o.UseSystemProxy,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        EnableMultipleHttp2Connections = true,
        ActivityHeadersPropagator = new ReverseProxyPropagator(DistributedContextPropagator.Current),
        ConnectTimeout = TimeSpan.FromMilliseconds(o.MainConnectTimeoutMs),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
    });
});
builder.Services.AddSingleton<ProxyHandler>();
builder.Services.AddSingleton<HeartbeatService>();

builder.Services.AddHostedService<ConfigRefresher>();
builder.Services.AddHostedService<MirrorWorkerService>();
builder.Services.AddHostedService<LogWriterService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HeartbeatService>());

var app = builder.Build();

// Liveness: the process is up. Readiness: we have a config snapshot (from Postgres or the snapshot file).
// Readiness deliberately does NOT depend on Postgres staying up — the proxy keeps forwarding without it.
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/readyz", (MissionConfigStore store) => store.HasSnapshot
    ? Results.Ok(new { status = "ready", config = store.Source, loadedAt = store.LoadedAt, missions = store.All.Count })
    : Results.Json(new { status = "waiting for config" }, statusCode: 503));
app.MapMetrics("/metrics");
app.MapGet("/metrics/json", (HeartbeatService hb) => Results.Ok(hb.Snapshot()));

var handler = app.Services.GetRequiredService<ProxyHandler>();
app.Map("/m", m => m.Run(handler.InvokeAsync));

app.Run();

public partial class Program;
