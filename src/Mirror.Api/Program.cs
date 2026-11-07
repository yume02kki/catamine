using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Mirror.Api;
using Mirror.Api.Endpoints;
using Mirror.Api.Jobs;
using Mirror.Api.Services;
using Mirror.Data;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.Warning);

var connectionString = builder.Configuration.GetConnectionString("Postgres")
                       ?? "Host=localhost;Database=mirror;Username=mirror;Password=mirror";
builder.Services.Configure<ApiOptions>(builder.Configuration.GetSection("Api"));

builder.Services.AddSingleton(_ =>
{
    var csb = new NpgsqlConnectionStringBuilder(connectionString) { ApplicationName = "mirror-api", CommandTimeout = 120 };
    return new NpgsqlDataSourceBuilder(csb.ConnectionString).Build();
});
builder.Services.AddDbContext<MirrorDbContext>((sp, o) => o
    .UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
    .UseSnakeCaseNamingConvention());

builder.Services.AddSingleton<LogRepository>();
builder.Services.AddSingleton<ReplayService>();
builder.Services.AddSingleton<LogNotifier>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LogNotifier>());
builder.Services.AddSingleton<PartitionMaintenance>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PartitionMaintenance>());
builder.Services.AddSingleton<JobRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobRunner>());
builder.Services.AddScoped<JobScheduler>();
builder.Services.AddSingleton<IJobHandler, RediffJob>();
builder.Services.AddSingleton<IJobHandler, ReplayAllJob>();
builder.Services.AddSingleton<IJobHandler, FuzzJob>();
builder.Services.AddSingleton<IJobHandler, ExportJob>();
builder.Services.AddSingleton<IJobHandler, ImportJob>();

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
});
builder.Services.AddProblemDetails();
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    var origins = builder.Configuration.GetSection("Api:CorsOrigins").Get<string[]>() ?? [];
    if (origins.Length > 0) p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod();
}));
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = null); // imports can be large; they are streamed to disk

var app = builder.Build();

// Migrations: on startup (default), or as a one-shot init job with `--migrate-only`.
var migrateOnly = args.Contains("--migrate-only");
if (migrateOnly || app.Configuration.GetValue("Api:MigrateOnStartup", true))
{
    await MigrateWithRetryAsync(app.Services, app.Logger);
    if (migrateOnly) return;
}

app.UseExceptionHandler(e => e.Run(async ctx =>
{
    var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var (status, message) = ex switch
    {
        BadRequestException b => (400, b.Message),
        BadHttpRequestException b => (400, b.Message),
        System.Text.Json.JsonException j => (400, "invalid JSON: " + j.Message),
        NpgsqlException n when n.IsTransient => (503, "database unavailable"),
        _ => (500, "internal error"),
    };
    ctx.Response.StatusCode = status;
    await ctx.Response.WriteAsJsonAsync(new { error = message });
}));
// CORS only when the UI is served from another origin (Api__CorsOrigins); same-origin needs none.
if ((app.Configuration.GetSection("Api:CorsOrigins").Get<string[]>() ?? []).Length > 0) app.UseCors();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapGet("/readyz", async (NpgsqlDataSource ds, CancellationToken ct) =>
{
    try
    {
        await using var cmd = ds.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(ct);
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
    {
        return Results.Json(new { status = "database unavailable" }, statusCode: 503);
    }
});

app.MapGet("/api/config", (Microsoft.Extensions.Options.IOptions<ApiOptions> o) =>
    Results.Ok(new { proxyPublicUrl = o.Value.ProxyPublicUrl.TrimEnd('/'), version = "2.0.0" }));

MissionEndpoints.Map(app);
LogEndpoints.Map(app);
JobEndpoints.Map(app);

app.Run();

static async Task MigrateWithRetryAsync(IServiceProvider services, ILogger log)
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MirrorDbContext>();
            await db.Database.MigrateAsync();
            log.LogInformation("Database schema is up to date");
            return;
        }
        catch (Exception ex) when (attempt < 30 && ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            log.LogWarning("Migration attempt {Attempt} failed ({Message}); retrying in 2s", attempt, ex.Message);
            await Task.Delay(2000);
        }
    }
}

public partial class Program;
