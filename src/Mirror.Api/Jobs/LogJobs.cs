using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Mirror.Api.Services;
using Mirror.Core.Compression;
using Mirror.Core.Diffing;
using Mirror.Core.Fuzzing;
using Mirror.Core.Rules;
using Mirror.Data;
using Npgsql;

namespace Mirror.Api.Jobs;

internal static class JobSupport
{
    public static async Task<(MissionEntity Mission, CompiledRuleSet Rules)> LoadMissionAsync(IServiceScopeFactory scopes, Guid? missionId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MirrorDbContext>();
        var m = await db.Missions.AsNoTracking().Include(x => x.Rules).FirstOrDefaultAsync(x => x.Id == missionId, ct)
                ?? throw new InvalidOperationException("mission not found");
        return (m, MissionMapping.CompileRules(m, m.Rules));
    }

    public static string EncodeCursor(DateTime ts, Guid id) => $"{ts.Ticks}|{id:N}";

    public static (DateTime, Guid)? DecodeCursor(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        var p = s.Split('|');
        return (new DateTime(long.Parse(p[0]), DateTimeKind.Utc), Guid.ParseExact(p[1], "N"));
    }

    public static async Task<long?> CountAsync(NpgsqlDataSource ds, LogQuery q, CancellationToken ct)
    {
        // Exact count for progress; bounded by a statement timeout so a huge unfiltered log never stalls the job.
        try
        {
            await using var conn = await ds.OpenConnectionAsync(ct);
            await using (var t = new NpgsqlCommand("SET statement_timeout = '15s'", conn)) await t.ExecuteNonQueryAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT count(*) FROM traffic_log WHERE mission_id = @m"
                + (q.Verdicts is { Length: > 0 } ? " AND verdict = ANY(@v)" : "")
                + (q.Source is not null ? " AND source = @s" : "")
                + (q.From is not null ? " AND ts >= @f" : "")
                + (q.To is not null ? " AND ts < @t" : ""), conn);
            cmd.Parameters.AddWithValue("m", q.MissionId);
            if (q.Verdicts is { Length: > 0 }) cmd.Parameters.AddWithValue("v", q.Verdicts.Select(v => (short)v).ToArray());
            if (q.Source is not null) cmd.Parameters.AddWithValue("s", (short)q.Source);
            if (q.From is not null) cmd.Parameters.AddWithValue("f", q.From.Value);
            if (q.To is not null) cmd.Parameters.AddWithValue("t", q.To.Value);
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (PostgresException) { return null; }
    }

    public static RateLimiter Limiter(double perSecond)
    {
        var rate = Math.Clamp(perSecond, 0.1, 10_000);
        var tokens = Math.Max(1, (int)Math.Ceiling(rate / 10));
        return new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = tokens,
            TokensPerPeriod = tokens,
            ReplenishmentPeriod = TimeSpan.FromSeconds(tokens / rate),
            QueueLimit = int.MaxValue,
            AutoReplenishment = true,
        });
    }

    public static Dictionary<string, long> Bump(this Dictionary<string, long> d, string key)
    {
        d[key] = d.TryGetValue(key, out var v) ? v + 1 : 1;
        return d;
    }
}

// ───────────────────────────── Re-diff ─────────────────────────────

public sealed record RediffParams(LogFilter? Filter);

/// <summary>Re-runs the diff on stored responses with the mission's current rules. No requests are sent.</summary>
public sealed class RediffJob(IServiceScopeFactory scopes, NpgsqlDataSource ds, LogNotifier notifier) : IJobHandler
{
    public string Type => JobTypes.Rediff;

    public async Task<object?> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var p = ctx.Params<RediffParams>();
        var (mission, rules) = await JobSupport.LoadMissionAsync(scopes, ctx.MissionId, ct);
        var settings = MissionMapping.DiffSettings(mission);
        var query = (p.Filter ?? new LogFilter()).ToQuery(mission.Id) with
        {
            Verdicts = [Verdict.Match, Verdict.Mismatch, Verdict.DevError],
        };
        var repo = new LogRepository(ds);
        var total = await JobSupport.CountAsync(ds, query, ct);
        var resultPrev = ctx.Checkpoint is null ? null : JsonSerializer.Deserialize<RediffState>(ctx.Checkpoint);
        var state = resultPrev ?? new RediffState();

        var batch = new List<DiffUpdate>(200);
        await foreach (var (log, body) in repo.StreamWithBodiesAsync(query, JobSupport.DecodeCursor(state.Cursor), 200, ct))
        {
            if (log.DevStatus is null) { state.Skipped++; continue; } // Dev never answered: nothing to compare
            var diff = DiffEngine.Compare(new DiffInput
            {
                MainStatus = log.MainStatus,
                DevStatus = log.DevStatus.Value,
                MainBody = BodyCodec.Decompress(body.MainBody),
                DevBody = BodyCodec.Decompress(body.DevBody),
                MainHeaders = LogRecord.UnpackHeaders(body.MainHeaders),
                DevHeaders = LogRecord.UnpackHeaders(body.DevHeaders),
            }, rules, settings);
            var verdict = DiffEngine.DecideVerdict(DevOutcome.Ok, log.MainStatus, log.DevStatus.Value, diff);
            if (verdict != log.Verdict) state.Changes.Bump($"{log.Verdict.ToWire()}->{verdict.ToWire()}");
            state.Verdicts.Bump(verdict.ToWire());

            var rec = new LogRecord { Method = log.Method, Path = log.Path };
            rec.ApplyDiff(diff, verdict, rules.Version);
            batch.Add(new DiffUpdate(log.MissionId, log.Ts, log.Id, verdict, rec.DiffKinds, rec.DiffCount, rec.Signature, rules.Version,
                rec.SummaryJson, rec.Diff, null));
            state.Processed++;
            state.Cursor = JobSupport.EncodeCursor(log.Ts, log.Id);

            if (batch.Count >= 200)
            {
                await repo.UpdateDiffsAsync(batch, ct);
                batch.Clear();
                await ctx.ReportAsync(state.Processed + state.Skipped, total, JsonSerializer.Serialize(state));
                notifier.Touch(mission.Id);
            }
        }
        await repo.UpdateDiffsAsync(batch, ct);
        await ctx.ReportAsync(state.Processed + state.Skipped, total, JsonSerializer.Serialize(state), force: true);
        notifier.Touch(mission.Id);
        return new { rulesVersion = rules.Version, processed = state.Processed, skipped = state.Skipped, verdicts = state.Verdicts, changes = state.Changes };
    }

    private sealed class RediffState
    {
        public string? Cursor { get; set; }
        public long Processed { get; set; }
        public long Skipped { get; set; }
        public Dictionary<string, long> Verdicts { get; set; } = [];
        public Dictionary<string, long> Changes { get; set; } = [];
    }
}

// ───────────────────────────── Replay all ─────────────────────────────

public sealed record ReplayParams(LogFilter? Filter, double RatePerSecond = 10, long? Limit = null, bool AllowUnsafeMethods = false);

/// <summary>Re-sends logged requests to both Main and Dev at a limited rate and compares against the original verdicts.</summary>
public sealed class ReplayAllJob(IServiceScopeFactory scopes, NpgsqlDataSource ds, ReplayService replay, LogNotifier notifier) : IJobHandler
{
    public string Type => JobTypes.Replay;

    public async Task<object?> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var p = ctx.Params<ReplayParams>();
        var (mission, rules) = await JobSupport.LoadMissionAsync(scopes, ctx.MissionId, ct);
        var filter = p.Filter ?? new LogFilter { Source = "live" };
        var query = filter.ToQuery(mission.Id);
        if (query.Verdicts is null) query = query with { Verdicts = [Verdict.Match, Verdict.Mismatch, Verdict.DevError, Verdict.DevTimeout] };
        var repo = new LogRepository(ds);
        var total = await JobSupport.CountAsync(ds, query, ct);
        if (p.Limit is { } lim && (total is null || total > lim)) total = lim;

        var state = ctx.Checkpoint is null ? new ReplayState() : JsonSerializer.Deserialize<ReplayState>(ctx.Checkpoint)!;
        using var limiter = JobSupport.Limiter(p.RatePerSecond);
        var pending = new List<LogRecord>(50);

        await foreach (var (log, body) in repo.StreamWithBodiesAsync(query, JobSupport.DecodeCursor(state.Cursor), 100, ct))
        {
            if (p.Limit is { } l && state.Sent + state.Skipped >= l) break;
            state.Cursor = JobSupport.EncodeCursor(log.Ts, log.Id);
            if (!p.AllowUnsafeMethods && MissionMapping.UnsafeMethods.Contains(log.Method))
            {
                state.Skipped++;
                continue;
            }
            using var lease = await limiter.AcquireAsync(1, ct);
            var (headers, reqBody) = ReplayService.StoredRequest(body);
            var (record, _) = await replay.SendBothAsync(mission, rules, log.Method, log.Path, log.Query, headers, reqBody,
                new RecordOptions(true, LogSource.Replay, ctx.Id, log.Id), ct);
            state.Sent++;
            state.Verdicts.Bump(record.Verdict.ToWire());
            var transition = (log.Verdict, record.Verdict) switch
            {
                (Verdict.Match, Verdict.Match) => "still_matching",
                (Verdict.Mismatch, Verdict.Match) or (Verdict.DevError, Verdict.Match) or (Verdict.DevTimeout, Verdict.Match) => "now_matching",
                (Verdict.Match, _) => "regressed",
                _ => "still_different",
            };
            state.Transitions.Bump(transition);
            pending.Add(record);
            if (pending.Count >= 50) await FlushAsync();
            await ctx.ReportAsync(state.Sent + state.Skipped, total, JsonSerializer.Serialize(state));
        }
        await FlushAsync();
        await ctx.ReportAsync(state.Sent + state.Skipped, total, JsonSerializer.Serialize(state), force: true);
        return new { sent = state.Sent, skipped = state.Skipped, verdicts = state.Verdicts, transitions = state.Transitions };

        async Task FlushAsync()
        {
            if (pending.Count == 0) return;
            await LogCopyWriter.WriteAsync(ds, pending, updateStats: false, ct);
            pending.Clear();
            notifier.Touch(mission.Id);
        }
    }

    private sealed class ReplayState
    {
        public string? Cursor { get; set; }
        public long Sent { get; set; }
        public long Skipped { get; set; }
        public Dictionary<string, long> Verdicts { get; set; } = [];
        public Dictionary<string, long> Transitions { get; set; } = [];
    }
}

// ───────────────────────────── Fuzz ─────────────────────────────

public sealed record FuzzSample(string Method, string Path, string? Query, Dictionary<string, string>? Headers, string? Body);

public sealed record FuzzParams(
    Guid? SeedLogId, FuzzSample? Sample, int Volume = 500, double RatePerSecond = 20, string[]? Mutations = null,
    int HugeStringLength = 65536, bool AllowUnsafeMethods = false, int Seed = 42);

/// <summary>Generates synthetic variants of a request and sends them to both Main and Dev.</summary>
public sealed class FuzzJob(IServiceScopeFactory scopes, NpgsqlDataSource ds, ReplayService replay, LogNotifier notifier) : IJobHandler
{
    public string Type => JobTypes.Fuzz;

    public async Task<object?> RunAsync(JobContext ctx, CancellationToken ct)
    {
        var p = ctx.Params<FuzzParams>();
        var (mission, rules) = await JobSupport.LoadMissionAsync(scopes, ctx.MissionId, ct);
        var seed = await SeedAsync(p, mission.Id, ct);
        if (!p.AllowUnsafeMethods && MissionMapping.UnsafeMethods.Contains(seed.Method.ToUpperInvariant()))
            throw new InvalidOperationException($"{seed.Method} fuzzing sends writes to Main and Dev; set allowUnsafeMethods to confirm");

        var cases = FuzzGenerator.Generate(seed, new FuzzOptions
        {
            Volume = Math.Clamp(p.Volume, 1, 1_000_000),
            Mutations = p.Mutations ?? [],
            HugeStringLength = Math.Clamp(p.HugeStringLength, 16, 8 * 1024 * 1024),
            Seed = p.Seed,
        });

        var state = ctx.Checkpoint is null ? new FuzzState() : JsonSerializer.Deserialize<FuzzState>(ctx.Checkpoint)!;
        using var limiter = JobSupport.Limiter(p.RatePerSecond);
        var pending = new List<LogRecord>(50);
        var index = 0;
        foreach (var c in cases)
        {
            if (index++ < state.Sent) continue; // resume
            using var lease = await limiter.AcquireAsync(1, ct);
            var body = c.Body is null ? null : System.Text.Encoding.UTF8.GetBytes(c.Body);
            var headers = c.Headers.ToList();
            if (c.Body is not null && !headers.Any(h => h.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase)))
                headers.Add(new("Content-Type", "application/json"));
            var (record, _) = await replay.SendBothAsync(mission, rules, c.Method, c.Path, c.Query, headers, body,
                new RecordOptions(true, LogSource.Fuzz, ctx.Id, Note: $"fuzz {c.Mutation} {c.Target}"), ct);
            state.Sent++;
            state.Verdicts.Bump(record.Verdict.ToWire());
            if (record.Verdict != Verdict.Match)
            {
                state.MismatchesByMutation.Bump(c.Mutation);
                state.MismatchesByTarget.Bump(c.Target);
                if (state.Examples.Count < 25) state.Examples.Add(new FuzzExample(record.Id, c.Mutation, c.Target, record.Verdict.ToWire()));
            }
            pending.Add(record);
            if (pending.Count >= 50)
            {
                await LogCopyWriter.WriteAsync(ds, pending, updateStats: false, ct);
                pending.Clear();
                notifier.Touch(mission.Id);
            }
            await ctx.ReportAsync(state.Sent, p.Volume, JsonSerializer.Serialize(state));
        }
        if (pending.Count > 0) await LogCopyWriter.WriteAsync(ds, pending, updateStats: false, ct);
        notifier.Touch(mission.Id);
        await ctx.ReportAsync(state.Sent, state.Sent, JsonSerializer.Serialize(state), force: true);
        return new
        {
            sent = state.Sent,
            verdicts = state.Verdicts,
            mismatchesByMutation = state.MismatchesByMutation,
            topTargets = state.MismatchesByTarget.OrderByDescending(x => x.Value).Take(20).ToDictionary(),
            examples = state.Examples,
        };
    }

    private async Task<FuzzSeed> SeedAsync(FuzzParams p, Guid missionId, CancellationToken ct)
    {
        if (p.Sample is { } s) return new FuzzSeed(s.Method.ToUpperInvariant(), s.Path, s.Query, s.Headers, s.Body);
        if (p.SeedLogId is not { } id) throw new InvalidOperationException("seedLogId or sample is required");
        var repo = new LogRepository(ds);
        var log = await repo.GetAsync(missionId, id, ct) ?? throw new InvalidOperationException("seed log not found");
        var (headers, body) = ReplayService.StoredRequest(await repo.GetBodyAsync(missionId, id, ct));
        return new FuzzSeed(log.Method, log.Path, log.Query, headers.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase),
            body is null ? null : System.Text.Encoding.UTF8.GetString(body));
    }

    public sealed record FuzzExample(Guid LogId, string Mutation, string Target, string Verdict);

    private sealed class FuzzState
    {
        public long Sent { get; set; }
        public Dictionary<string, long> Verdicts { get; set; } = [];
        public Dictionary<string, long> MismatchesByMutation { get; set; } = [];
        public Dictionary<string, long> MismatchesByTarget { get; set; } = [];
        public List<FuzzExample> Examples { get; set; } = [];
    }
}
