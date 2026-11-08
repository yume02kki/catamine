using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mirror.Api.Services;
using Mirror.Core.Compression;
using Mirror.Core.Diffing;
using Mirror.Core.Rules;
using Mirror.Data;
using Npgsql;

namespace Mirror.Api.Endpoints;

public static class LogEndpoints
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/missions/{missionId:guid}");

        g.MapGet("/logs", async (Guid missionId, [AsParameters] LogListQuery q, LogRepository repo, CancellationToken ct) =>
        {
            var query = q.ToFilter().ToQuery(missionId, q.Sort, q.Cursor, q.Limit ?? 100);
            if (query.Sort is LogSort.DevMsDesc or LogSort.DiffCountDesc && query.From is null)
                query = query with { From = DateTime.UtcNow.AddHours(-24) }; // non-time sorts need a bounded window
            var page = await repo.ListAsync(query, ct);
            return Results.Ok(new { items = page.Items.Select(LogItemDto.From), nextCursor = page.NextCursor });
        });

        g.MapGet("/logs/{id:guid}", async (Guid missionId, Guid id, LogRepository repo, CancellationToken ct) =>
            await repo.GetAsync(missionId, id, ct) is { } row ? Results.Ok(LogItemDto.From(row)) : Results.NotFound());

        g.MapGet("/logs/{id:guid}/detail", async (Guid missionId, Guid id, LogRepository repo, CancellationToken ct) =>
        {
            var row = await repo.GetAsync(missionId, id, ct);
            if (row is null) return Results.NotFound();
            var body = await repo.GetBodyAsync(missionId, id, ct);
            var diffs = body?.Diff is { } d
                ? JsonSerializer.Deserialize<List<Difference>>(BodyCodec.Decompress(d), LogRecord.SummaryJsonOptions) ?? []
                : [];
            var basePath = $"/api/missions/{missionId}/logs/{id}/body";
            return Results.Ok(new LogDetailDto(
                LogItemDto.From(row),
                new ExchangePart(null, LogRecord.UnpackHeaders(body?.RequestHeaders), Size(body?.RequestBody), $"{basePath}/request"),
                new ExchangePart(row.MainStatus, LogRecord.UnpackHeaders(body?.MainHeaders), Size(body?.MainBody), $"{basePath}/main"),
                body?.DevHeaders is null && row.DevStatus is null ? null
                    : new ExchangePart(row.DevStatus, LogRecord.UnpackHeaders(body?.DevHeaders), Size(body?.DevBody), $"{basePath}/dev"),
                diffs));
        });

        // Raw decoded body, fetched lazily by the diff viewer (which parses and diffs it in a Web Worker).
        // ?normalized=true returns it after the mission's text/document rules, pretty-printed.
        g.MapGet("/logs/{id:guid}/body/{side}", async (Guid missionId, Guid id, string side, bool? normalized,
            LogRepository repo, MirrorDbContext db, CancellationToken ct) =>
        {
            var body = await repo.GetBodyAsync(missionId, id, ct);
            if (body is null) return Results.NotFound();
            var (bytes, headers) = side.ToLowerInvariant() switch
            {
                "request" => (body.RequestBody, body.RequestHeaders),
                "main" => (body.MainBody, body.MainHeaders),
                "dev" => (body.DevBody, body.DevHeaders),
                _ => (null, null),
            };
            if (side is not ("request" or "main" or "dev")) return Results.BadRequest(new { error = "side must be request, main or dev" });
            var data = BodyCodec.Decompress(bytes);
            var contentType = LogRecord.UnpackHeaders(headers).GetValueOrDefault("content-type") ?? "application/octet-stream";
            if (normalized == true && side != "request")
            {
                var m = await db.Missions.AsNoTracking().Include(x => x.Rules).FirstAsync(x => x.Id == missionId, ct);
                var text = DiffEngine.Normalize(data, side == "main" ? Side.Main : Side.Dev, MissionMapping.CompileRules(m, m.Rules));
                return Results.Text(text ?? "", "application/json", Encoding.UTF8);
            }
            return Results.Bytes(data, contentType);
        });

        g.MapPost("/logs/{id:guid}/replay", async (Guid missionId, Guid id, [FromQuery] bool? allowUnsafe,
            LogRepository repo, MirrorDbContext db, ReplayService replay, NpgsqlDataSource ds, LogNotifier notifier, CancellationToken ct) =>
        {
            var m = await db.Missions.AsNoTracking().Include(x => x.Rules).FirstOrDefaultAsync(x => x.Id == missionId, ct);
            var original = await repo.GetAsync(missionId, id, ct);
            if (m is null || original is null) return Results.NotFound();
            if (MissionMapping.UnsafeMethods.Contains(original.Method) && allowUnsafe != true)
                return Results.BadRequest(new { error = $"Replaying {original.Method} re-sends a write to Main and Dev. Pass allowUnsafe=true to confirm." });
            var (headers, reqBody) = ReplayService.StoredRequest(await repo.GetBodyAsync(missionId, id, ct));
            var (record, diff) = await replay.SendBothAsync(m, MissionMapping.CompileRules(m, m.Rules), original.Method, original.Path, original.Query,
                headers, reqBody, new RecordOptions(true, LogSource.Replay, null, id), ct);
            await LogCopyWriter.WriteAsync(ds, [record], updateStats: false, ct);
            notifier.Touch(missionId);
            var saved = await repo.GetAsync(missionId, record.Id, ct);
            return Results.Ok(new ReplayResultDto(LogItemDto.From(original), LogItemDto.From(saved!), diff?.Differences ?? []));
        });

        g.MapGet("/stats", async (Guid missionId, DateTime? from, DateTime? to, int? bucketMinutes, LogRepository repo, CancellationToken ct) =>
        {
            var t = to?.ToUniversalTime() ?? DateTime.UtcNow;
            var f = from?.ToUniversalTime() ?? t.AddHours(-1);
            var counts = await repo.StatsAsync(missionId, f, t, ct);
            var timeline = await repo.TimelineAsync(missionId, f, t, bucketMinutes ?? Math.Max(1, (int)(t - f).TotalMinutes / 60), ct);
            var total = counts.Sum(c => c.Count);
            var compared = counts.Where(c => c.Verdict is Verdict.Match or Verdict.Mismatch).Sum(c => c.Count);
            var match = counts.FirstOrDefault(c => c.Verdict == Verdict.Match)?.Count ?? 0;
            return Results.Ok(new
            {
                from = f, to = t, total,
                matchRate = compared == 0 ? (double?)null : (double)match / compared,
                verdicts = counts.ToDictionary(c => c.Verdict.ToWire(), c => new { count = c.Count, mainMsAvg = c.MainMsAvg, devMsAvg = c.DevMsAvg }),
                timeline = timeline.GroupBy(b => b.Bucket).Select(gr => new { bucket = gr.Key, counts = gr.ToDictionary(x => x.Verdict.ToWire(), x => x.Count) }),
            });
        });

        g.MapGet("/clusters", async (Guid missionId, DateTime? from, DateTime? to, int? limit, LogRepository repo, CancellationToken ct) =>
        {
            var t = to?.ToUniversalTime() ?? DateTime.UtcNow;
            var f = from?.ToUniversalTime() ?? t.AddHours(-1);
            var clusters = await repo.ClustersAsync(missionId, f, t, limit ?? 50, ct);
            return Results.Ok(clusters.Select(c => new
            {
                signature = c.Signature.ToString(System.Globalization.CultureInfo.InvariantCulture),
                c.Count, c.FirstSeen, c.LastSeen,
                example = new { id = c.ExampleId, method = c.ExampleMethod, path = c.ExamplePath },
                summary = c.ExampleSummary is null ? (JsonElement?)null : JsonDocument.Parse(c.ExampleSummary).RootElement.Clone(),
            }));
        });

        // Live updates: one event per second at most, carrying every new row since the last event (capped).
        g.MapGet("/live", async (Guid missionId, string? cursor, HttpContext http, LogRepository repo, LogNotifier notifier, CancellationToken ct) =>
        {
            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            var (lastTs, lastId) = Data.Cursor.TryDecode(cursor, out var c) ? (c.Ts, c.Id) : (DateTime.UtcNow, Guid.Empty);
            var seen = -1L;
            var idle = 0;
            await http.Response.WriteAsync("event: ready\ndata: {}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct);
                var v = notifier.Version(missionId);
                if (v != seen)
                {
                    seen = v;
                    var rows = await repo.NewerThanAsync(missionId, lastTs, lastId, 200, ct);
                    if (rows.Count > 0)
                    {
                        lastTs = rows[^1].Ts;
                        lastId = rows[^1].Id;
                        var payload = JsonSerializer.Serialize(new
                        {
                            items = rows.Select(LogItemDto.From),
                            truncated = rows.Count == 200,
                            cursor = Data.Cursor.Encode(new Data.Cursor(0, lastTs, lastId)),
                        }, WebJson);
                        await http.Response.WriteAsync($"event: logs\ndata: {payload}\n\n", ct);
                        await http.Response.Body.FlushAsync(ct);
                        idle = 0;
                        continue;
                    }
                }
                if (++idle % 15 == 0)
                {
                    await http.Response.WriteAsync(": keep-alive\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                }
            }
        });
    }

    private static int Size(byte[]? compressed) => compressed is null ? 0 : BodyCodec.Decompress(compressed).Length;

    public sealed record LogListQuery(
        string? Verdict, string? DiffKind, string? Path, string? PathMode, string? Method, int? MainStatus, int? DevStatus,
        string? Source, Guid? JobId, Guid? ReplayOf, string? Signature, DateTime? From, DateTime? To, string? Sort, string? Cursor, int? Limit)
    {
        public LogFilter ToFilter() => new()
        {
            Verdict = Verdict, DiffKind = DiffKind, Path = Path, PathMode = PathMode, Method = Method, MainStatus = MainStatus,
            DevStatus = DevStatus, Source = Source, JobId = JobId, ReplayOf = ReplayOf, Signature = Signature, From = From, To = To,
        };
    }
}
