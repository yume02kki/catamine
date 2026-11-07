using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mirror.Api.Services;
using Mirror.Core.Diffing;
using Mirror.Data;
using Npgsql;

namespace Mirror.Api.Jobs;

/// <summary>
/// Streaming export format: gzip-compressed NDJSON, one JSON object per line.
/// <code>
/// {"type":"header","format":"mirror-export","version":1,"exportedAt":"…"}
/// {"type":"mission","mission":{…},"rules":[…]}
/// {"type":"log","log":{…},"body":{"codec":1,"requestHeaders":"base64",…}}   ← repeated
/// {"type":"end","count":N}
/// </code>
/// Body fields are base64 of the stored bytes (codec 1 = Brotli), so export and import never re-compress.
/// </summary>
public static class NdjsonFormat
{
    public const string Format = "mirror-export";
    public const int Version = 1;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public sealed record ExportMission(
        string Slug, string Name, string? Description, string MainUrl, string DevUrl, bool Enabled, bool MirrorEnabled, double SampleRate,
        string[] MirrorMethods, int MaxMirrorBodyBytes, int DevTimeoutMs, int DevMaxConcurrency, JsonElement HeaderPolicy,
        string[] ComparedHeaders, int RulesVersion, int RetentionDays, int? MaxStorageMb);

    public sealed record ExportRule(int Order, string Type, string? Name, bool Enabled, JsonElement Config);

    public sealed record ExportLog(
        Guid Id, DateTime Ts, string Method, string Path, string? Query, short MainStatus, short? DevStatus, float MainMs, float? DevMs,
        short Verdict, int DiffKinds, int DiffCount, long Signature, int RulesVersion, short Source, Guid? ReplayOf, string? Note, JsonElement? Summary);

    public sealed record ExportBody(short Codec, string? RequestHeaders, string? RequestBody, string? MainHeaders, string? MainBody,
        string? DevHeaders, string? DevBody, string? Diff);

    public sealed record Line(
        string Type, string? Format = null, int? Version = null, DateTimeOffset? ExportedAt = null,
        ExportMission? Mission = null, List<ExportRule>? Rules = null, ExportLog? Log = null, ExportBody? Body = null, long? Count = null);

    public static async Task<long> WriteAsync(Stream output, MissionEntity mission, NpgsqlDataSource ds, Func<long, Task>? progress, CancellationToken ct)
    {
        // Both wrappers leave their inner stream open and are disposed asynchronously in order, so the gzip
        // footer is written with async I/O (Kestrel response bodies reject synchronous writes).
        var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
        await using var _gz = gz;
        var w = new StreamWriter(gz, new UTF8Encoding(false), 1 << 16, leaveOpen: true);
        await using var _w = w;

        async Task Write(Line line) => await w.WriteLineAsync(JsonSerializer.Serialize(line, Options).AsMemory(), ct);

        await Write(new Line("header", Format, Version, DateTimeOffset.UtcNow));
        await Write(new Line("mission",
            Mission: new ExportMission(mission.Slug, mission.Name, mission.Description, mission.MainUrl, mission.DevUrl, mission.Enabled,
                mission.MirrorEnabled, mission.SampleRate, mission.MirrorMethods, mission.MaxMirrorBodyBytes, mission.DevTimeoutMs,
                mission.DevMaxConcurrency, JsonDocument.Parse(mission.HeaderPolicy).RootElement.Clone(), mission.ComparedHeaders,
                mission.RulesVersion, mission.RetentionDays, mission.MaxStorageMb),
            Rules: mission.Rules.OrderBy(r => r.Order)
                .Select(r => new ExportRule(r.Order, r.Type, r.Name, r.Enabled, JsonDocument.Parse(r.Config).RootElement.Clone())).ToList()));

        var repo = new LogRepository(ds);
        long count = 0;
        await foreach (var (log, body) in repo.StreamWithBodiesAsync(new LogQuery { MissionId = mission.Id }, null, 500, ct))
        {
            await Write(new Line("log",
                Log: new ExportLog(log.Id, log.Ts, log.Method, log.Path, log.Query, log.MainStatus, log.DevStatus, log.MainMs, log.DevMs,
                    (short)log.Verdict, log.DiffKinds, log.DiffCount, log.Signature, log.RulesVersion, (short)log.Source, log.ReplayOf, log.Note,
                    log.SummaryJson is null ? null : JsonDocument.Parse(log.SummaryJson).RootElement.Clone()),
                Body: new ExportBody(1, B64(body.RequestHeaders), B64(body.RequestBody), B64(body.MainHeaders), B64(body.MainBody),
                    B64(body.DevHeaders), B64(body.DevBody), B64(body.Diff))));
            if (++count % 1000 == 0 && progress is not null) await progress(count);
        }
        await Write(new Line("end", Count: count));
        await w.FlushAsync(ct);
        await w.DisposeAsync();
        await gz.DisposeAsync();
        return count;
    }

    private static string? B64(byte[]? b) => b is null ? null : Convert.ToBase64String(b);
    private static byte[]? Unb64(string? s) => s is null ? null : Convert.FromBase64String(s);

    public static Line Parse(string line) => JsonSerializer.Deserialize<Line>(line, Options) ?? throw new InvalidDataException("empty line");

    public static LogRecord ToRecord(ExportLog l, ExportBody? b, Guid missionId) => new()
    {
        Id = l.Id,
        MissionId = missionId,
        Ts = DateTime.SpecifyKind(l.Ts, DateTimeKind.Utc),
        Method = l.Method,
        Path = l.Path,
        Query = l.Query,
        MainStatus = l.MainStatus,
        DevStatus = l.DevStatus,
        MainMs = l.MainMs,
        DevMs = l.DevMs,
        Verdict = (Verdict)l.Verdict,
        DiffKinds = l.DiffKinds,
        DiffCount = l.DiffCount,
        Signature = l.Signature,
        RulesVersion = l.RulesVersion,
        Source = l.Source == (short)LogSource.Live ? LogSource.Import : (LogSource)l.Source,
        ReplayOf = l.ReplayOf,
        Note = l.Note,
        SummaryJson = l.Summary?.GetRawText(),
        RequestHeaders = Unb64(b?.RequestHeaders),
        RequestBody = Unb64(b?.RequestBody),
        MainHeaders = Unb64(b?.MainHeaders),
        MainBody = Unb64(b?.MainBody),
        DevHeaders = Unb64(b?.DevHeaders),
        DevBody = Unb64(b?.DevBody),
        Diff = Unb64(b?.Diff),
    };
}

public sealed record ExportParams;

/// <summary>Writes a mission export to the export volume. Restarts from scratch if interrupted (a gzip stream cannot be resumed).</summary>
public sealed class ExportJob(IServiceScopeFactory scopes, NpgsqlDataSource ds, IOptions<ApiOptions> options) : IJobHandler
{
    public string Type => JobTypes.Export;

    public static string FileFor(ApiOptions o, Guid jobId) => Path.Combine(o.ExportDir, $"{jobId:N}.ndjson.gz");

    public async Task<object?> RunAsync(JobContext ctx, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MirrorDbContext>();
        var mission = await db.Missions.AsNoTracking().Include(m => m.Rules).FirstOrDefaultAsync(m => m.Id == ctx.MissionId, ct)
                      ?? throw new InvalidOperationException("mission not found");
        Directory.CreateDirectory(options.Value.ExportDir);
        var path = FileFor(options.Value, ctx.Id);
        var tmp = path + ".part";
        long count;
        await using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            count = await NdjsonFormat.WriteAsync(file, mission, ds, n => ctx.ReportAsync(n, null, null), ct);
        File.Move(tmp, path, overwrite: true);
        await ctx.ReportAsync(count, count, null, force: true);
        return new { file = Path.GetFileName(path), bytes = new FileInfo(path).Length, logs = count, download = $"/api/jobs/{ctx.Id}/download" };
    }
}

public sealed record ImportParams(string File, string? Slug);

/// <summary>Streams an uploaded export into a new mission (COPY in batches); resumable by line number.</summary>
public sealed class ImportJob(IServiceScopeFactory scopes, NpgsqlDataSource ds, IOptions<ApiOptions> options, LogNotifier notifier) : IJobHandler
{
    public string Type => JobTypes.Import;

    public async Task<object?> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var p = ctx.Params<ImportParams>();
        var path = Path.Combine(options.Value.ExportDir, "imports", Path.GetFileName(p.File));
        var state = ctx.Checkpoint is null ? new ImportState() : JsonSerializer.Deserialize<ImportState>(ctx.Checkpoint)!;

        await using var file = File.OpenRead(path);
        await using var gz = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gz, Encoding.UTF8);
        var batch = new List<LogRecord>(500);
        long line = 0;
        string? text;
        while ((text = await reader.ReadLineAsync(ct)) is not null)
        {
            line++;
            if (line <= state.Line || string.IsNullOrWhiteSpace(text)) continue;
            var l = NdjsonFormat.Parse(text);
            switch (l.Type)
            {
                case "header" when l.Format != NdjsonFormat.Format:
                    throw new InvalidDataException("not a mirror export file");
                case "mission" when state.MissionId is null:
                    state.MissionId = await CreateMissionAsync(l, p.Slug, ct);
                    state.Line = line;
                    await ctx.ReportAsync(state.Logs, null, JsonSerializer.Serialize(state), force: true);
                    break;
                case "log":
                    if (state.MissionId is null) throw new InvalidDataException("log line before mission line");
                    batch.Add(NdjsonFormat.ToRecord(l.Log!, l.Body, state.MissionId.Value));
                    if (batch.Count >= 500) await FlushAsync(line);
                    break;
                case "end":
                    await FlushAsync(line);
                    break;
            }
        }
        await FlushAsync(line);
        await ctx.ReportAsync(state.Logs, state.Logs, JsonSerializer.Serialize(state), force: true);
        try { File.Delete(path); } catch (IOException) { }
        return new { missionId = state.MissionId, logs = state.Logs };

        async Task FlushAsync(long upTo)
        {
            if (batch.Count == 0) return;
            try
            {
                await LogCopyWriter.WriteAsync(ds, batch, updateStats: true, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Resumed after a crash between commit and checkpoint: skip rows that already made it in.
                var existing = await ExistingIdsAsync(state.MissionId!.Value, batch.Select(b => b.Id).ToArray(), ct);
                var rest = batch.Where(b => !existing.Contains(b.Id)).ToList();
                if (rest.Count > 0) await LogCopyWriter.WriteAsync(ds, rest, updateStats: true, ct);
            }
            state.Logs += batch.Count;
            state.Line = upTo;
            batch.Clear();
            await ctx.ReportAsync(state.Logs, null, JsonSerializer.Serialize(state));
            notifier.Touch(state.MissionId!.Value);
        }
    }

    private async Task<HashSet<Guid>> ExistingIdsAsync(Guid mission, Guid[] ids, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("SELECT id FROM traffic_log WHERE mission_id = @m AND id = ANY(@ids)");
        cmd.Parameters.AddWithValue("m", mission);
        cmd.Parameters.AddWithValue("ids", ids);
        var set = new HashSet<Guid>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) set.Add(r.GetGuid(0));
        return set;
    }

    private async Task<Guid> CreateMissionAsync(NdjsonFormat.Line line, string? slug, CancellationToken ct)
    {
        var src = line.Mission ?? throw new InvalidDataException("mission line without mission");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MirrorDbContext>();
        var baseSlug = slug ?? src.Slug + "-import";
        var finalSlug = baseSlug;
        for (var i = 2; await db.Missions.AnyAsync(m => m.Slug == finalSlug, ct); i++) finalSlug = $"{baseSlug}-{i}";
        var now = DateTimeOffset.UtcNow;
        var m = new MissionEntity
        {
            Id = Guid.CreateVersion7(),
            Slug = finalSlug,
            Name = src.Name + " (imported)",
            Description = src.Description,
            MainUrl = src.MainUrl,
            DevUrl = src.DevUrl,
            Enabled = false,          // imported missions do not start proxying until enabled
            MirrorEnabled = src.MirrorEnabled,
            SampleRate = src.SampleRate,
            MirrorMethods = src.MirrorMethods,
            MaxMirrorBodyBytes = src.MaxMirrorBodyBytes,
            DevTimeoutMs = src.DevTimeoutMs,
            DevMaxConcurrency = src.DevMaxConcurrency,
            HeaderPolicy = src.HeaderPolicy.GetRawText(),
            ComparedHeaders = src.ComparedHeaders,
            RulesVersion = src.RulesVersion,
            // Imported history is older than "now - retention"; keep it until someone lowers the limit.
            RetentionDays = Math.Max(src.RetentionDays, 3650),
            MaxStorageMb = src.MaxStorageMb,
            CreatedAt = now,
            UpdatedAt = now,
            Rules = (line.Rules ?? []).Select(r => new MissionRuleEntity
            {
                Id = Guid.CreateVersion7(), Order = r.Order, Type = r.Type, Name = r.Name, Enabled = r.Enabled, Config = r.Config.GetRawText(),
            }).ToList(),
        };
        db.Missions.Add(m);
        await db.SaveChangesAsync(ct);
        return m.Id;
    }

    private sealed class ImportState
    {
        public long Line { get; set; }
        public Guid? MissionId { get; set; }
        public long Logs { get; set; }
    }
}
