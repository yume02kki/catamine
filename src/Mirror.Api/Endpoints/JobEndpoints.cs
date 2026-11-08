using Mirror.Core.Models;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mirror.Api.Jobs;
using Mirror.Api.Services;
using Mirror.Core.Rules;
using Mirror.Data;
using Npgsql;

namespace Mirror.Api.Endpoints;

public static class JobEndpoints
{
    public static void Map(WebApplication app)
    {
        var m = app.MapGroup("/api/missions/{missionId:guid}");

        m.MapPost("/jobs/rediff", (Guid missionId, RediffParams? body, MirrorDbContext db, JobScheduler jobs, CancellationToken ct) =>
            Enqueue(db, jobs, JobTypes.Rediff, missionId, body ?? new RediffParams(null), ct));

        m.MapPost("/jobs/replay", (Guid missionId, ReplayParams? body, MirrorDbContext db, JobScheduler jobs, CancellationToken ct) =>
            Enqueue(db, jobs, JobTypes.Replay, missionId, body ?? new ReplayParams(null), ct));

        m.MapPost("/jobs/fuzz", (Guid missionId, FuzzParams body, MirrorDbContext db, JobScheduler jobs, CancellationToken ct) =>
        {
            if (body.SeedLogId is null && body.Sample is null)
                return Task.FromResult(Results.BadRequest(new { error = "seedLogId or sample is required" }));
            if (body.Sample is { } s && MissionMapping.UnsafeMethods.Contains(s.Method.ToUpperInvariant()) && !body.AllowUnsafeMethods)
                return Task.FromResult(Results.BadRequest(new { error = $"Fuzzing {s.Method} sends many writes to Main and Dev. Set allowUnsafeMethods=true to confirm." }));
            return Enqueue(db, jobs, JobTypes.Fuzz, missionId, body, ct);
        });

        m.MapPost("/jobs/export", (Guid missionId, MirrorDbContext db, JobScheduler jobs, CancellationToken ct) =>
            Enqueue(db, jobs, JobTypes.Export, missionId, new ExportParams(), ct));

        // Direct streaming export (no job): rows are read from a keyset cursor and written as they come.
        m.MapGet("/export", async (Guid missionId, HttpContext http, MirrorDbContext db, NpgsqlDataSource ds, CancellationToken ct) =>
        {
            var mission = await db.Missions.AsNoTracking().Include(x => x.Rules).FirstOrDefaultAsync(x => x.Id == missionId, ct);
            if (mission is null) { http.Response.StatusCode = 404; return; }
            http.Response.ContentType = "application/gzip";
            http.Response.Headers.ContentDisposition = $"attachment; filename=\"{mission.Slug}-{DateTime.UtcNow:yyyyMMddHHmm}.ndjson.gz\"";
            await NdjsonFormat.WriteAsync(http.Response.Body, mission, ds, null, ct);
        });

        // Import: the upload is streamed to the export volume, then processed by a job (survives restarts).
        app.MapPost("/api/import", async (HttpRequest req, string? slug, IOptions<ApiOptions> options, JobScheduler jobs, CancellationToken ct) =>
        {
            var dir = Path.Combine(options.Value.ExportDir, "imports");
            Directory.CreateDirectory(dir);
            var name = $"{Uuid7.NewGuid():N}.ndjson.gz";
            var path = Path.Combine(dir, name);
            Stream source = req.HasFormContentType ? (await req.ReadFormAsync(ct)).Files.FirstOrDefault()?.OpenReadStream() ?? Stream.Null : req.Body;
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await source.CopyToAsync(file, ct);
            if (new FileInfo(path).Length == 0) { File.Delete(path); return Results.BadRequest(new { error = "empty upload" }); }
            var job = await jobs.EnqueueAsync(JobTypes.Import, null, new ImportParams(name, slug), ct);
            return Results.Accepted($"/api/jobs/{job.Id}", JobDto.From(job));
        }).DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(long.MaxValue));

        var j = app.MapGroup("/api/jobs");

        j.MapGet("", async (Guid? missionId, string? status, string? cursor, int? limit, MirrorDbContext db, CancellationToken ct) =>
        {
            var q = db.Jobs.AsNoTracking();
            if (missionId is { } mid) q = q.Where(x => x.MissionId == mid);
            if (!string.IsNullOrEmpty(status) && Enum.TryParse<JobStatus>(status, true, out var st)) q = q.Where(x => x.Status == st);
            if (!string.IsNullOrEmpty(cursor) && long.TryParse(cursor, out var ticks))
            {
                var before = new DateTimeOffset(ticks, TimeSpan.Zero);
                q = q.Where(x => x.CreatedAt < before);
            }
            var n = Math.Clamp(limit ?? 50, 1, 200);
            var items = await q.OrderByDescending(x => x.CreatedAt).Take(n + 1).ToListAsync(ct);
            var next = items.Count > n ? items[n - 1].CreatedAt.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            return Results.Ok(new { items = items.Take(n).Select(JobDto.From), nextCursor = next });
        });

        j.MapGet("/{id:guid}", async (Guid id, MirrorDbContext db, CancellationToken ct) =>
            await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) is { } job ? Results.Ok(JobDto.From(job)) : Results.NotFound());

        j.MapPost("/{id:guid}/cancel", async (Guid id, MirrorDbContext db, CancellationToken ct) =>
        {
            var job = await db.Jobs.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (job is null) return Results.NotFound();
            if (job.Status == JobStatus.Queued)
            {
                job.Status = JobStatus.Cancelled;
                job.FinishedAt = DateTimeOffset.UtcNow;
                job.Error = "cancelled";
            }
            else if (job.Status == JobStatus.Running) job.CancelRequested = true;
            await db.SaveChangesAsync(ct);
            return Results.Ok(JobDto.From(job));
        });

        j.MapGet("/{id:guid}/download", async (Guid id, MirrorDbContext db, IOptions<ApiOptions> options, CancellationToken ct) =>
        {
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (job is null || job.Type != JobTypes.Export || job.Status != JobStatus.Succeeded) return Results.NotFound();
            var path = ExportJob.FileFor(options.Value, id);
            if (!File.Exists(path)) return Results.NotFound(new { error = "export file no longer available" });
            var slug = job.MissionId is { } mid ? (await db.Missions.AsNoTracking().Where(x => x.Id == mid).Select(x => x.Slug).FirstOrDefaultAsync(ct)) : null;
            return Results.File(path, "application/gzip", $"{slug ?? "mission"}-{id:N}.ndjson.gz", enableRangeProcessing: true);
        });

        // ── Rule types & validation ──
        app.MapGet("/api/rule-types", () => Results.Ok(RuleRegistry.Types.Select(t => new { type = t.Type, description = t.Description, example = t.ExampleConfig })));
        app.MapPost("/api/rules/validate", (RuleValidationRequest body) =>
        {
            var errors = RuleRegistry.Validate(body.Type, body.Config);
            return Results.Ok(new { valid = errors.Count == 0, errors });
        });

        // ── Proxy fleet (heartbeats written by each proxy instance) ──
        app.MapGet("/api/proxy/instances", async (NpgsqlDataSource ds, CancellationToken ct) =>
        {
            await using var cmd = ds.CreateCommand("""
                SELECT instance_id, last_seen, started_at, metrics::text FROM proxy_instances
                WHERE last_seen > now() - interval '2 minutes' ORDER BY instance_id
                """);
            var list = new List<object>();
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                list.Add(new { instanceId = r.GetString(0), lastSeen = r.GetFieldValue<DateTimeOffset>(1), startedAt = r.GetFieldValue<DateTimeOffset>(2),
                    metrics = JsonDocument.Parse(r.GetString(3)).RootElement.Clone() });
            return Results.Ok(list);
        });
    }

    private static async Task<IResult> Enqueue(MirrorDbContext db, JobScheduler jobs, string type, Guid missionId, object body, CancellationToken ct)
    {
        if (!await db.Missions.AnyAsync(x => x.Id == missionId, ct)) return Results.NotFound();
        var job = await jobs.EnqueueAsync(type, missionId, body, ct);
        return Results.Accepted($"/api/jobs/{job.Id}", JobDto.From(job));
    }
}
