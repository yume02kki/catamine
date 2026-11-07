using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Mirror.Core.Diffing;
using Npgsql;
using NpgsqlTypes;

namespace Mirror.Data;

public enum LogSort { TsDesc, TsAsc, DevMsDesc, DiffCountDesc }

public enum PathMatchMode { Prefix, Contains, Exact }

public sealed record LogQuery
{
    public required Guid MissionId { get; init; }
    public Verdict[]? Verdicts { get; init; }
    public DiffKinds DiffKinds { get; init; }
    public string? Path { get; init; }
    public PathMatchMode PathMode { get; init; } = PathMatchMode.Prefix;
    public string? Method { get; init; }
    public int? MainStatus { get; init; }
    public int? DevStatus { get; init; }
    public LogSource? Source { get; init; }
    public Guid? JobId { get; init; }
    public Guid? ReplayOf { get; init; }
    public long? Signature { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public LogSort Sort { get; init; } = LogSort.TsDesc;
    public string? Cursor { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed record LogRow(
    Guid Id, Guid MissionId, DateTime Ts, string Method, string Path, string? Query,
    short MainStatus, short? DevStatus, float MainMs, float? DevMs, Verdict Verdict, int DiffKinds, int DiffCount,
    long Signature, int RulesVersion, LogSource Source, Guid? JobId, Guid? ReplayOf, string? Note, string? SummaryJson);

public sealed record BodyRow(
    byte[]? RequestHeaders, byte[]? RequestBody, byte[]? MainHeaders, byte[]? MainBody,
    byte[]? DevHeaders, byte[]? DevBody, byte[]? Diff);

public sealed record LogPage(IReadOnlyList<LogRow> Items, string? NextCursor);

public sealed record DiffUpdate(Guid MissionId, DateTime Ts, Guid Id, Verdict Verdict, int DiffKinds, int DiffCount, long Signature, int RulesVersion, string? SummaryJson, byte[]? Diff, string? Note);

/// <summary>Keyset-paginated reads over the partitioned log. Nothing here loads an unbounded result.</summary>
public sealed class LogRepository(NpgsqlDataSource ds)
{
    public const int MaxLimit = 1000;

    private const string Columns = """
        id, mission_id, ts, method, path, query, main_status, dev_status, main_ms, dev_ms, verdict, diff_kinds, diff_count,
        signature, rules_version, source, job_id, replay_of, note, summary::text
        """;

    public async Task<LogPage> ListAsync(LogQuery q, CancellationToken ct)
    {
        var limit = Math.Clamp(q.Limit, 1, MaxLimit);
        await using var cmd = ds.CreateCommand();
        var sql = new StringBuilder($"SELECT {Columns} FROM traffic_log WHERE mission_id = @mission");
        cmd.Parameters.AddWithValue("mission", q.MissionId);
        AppendFilters(q, sql, cmd);

        var (orderBy, sortExpr, desc) = q.Sort switch
        {
            LogSort.TsAsc => ("ts ASC, id ASC", (string?)null, false),
            LogSort.DevMsDesc => ("coalesce(dev_ms, -1) DESC, ts DESC, id DESC", "coalesce(dev_ms, -1)", true),
            LogSort.DiffCountDesc => ("diff_count DESC, ts DESC, id DESC", "diff_count", true),
            _ => ("ts DESC, id DESC", null, true),
        };

        if (Cursor.TryDecode(q.Cursor, out var cur))
        {
            var op = desc ? "<" : ">";
            if (sortExpr is null) sql.Append($" AND (ts, id) {op} (@cts, @cid)");
            else
            {
                sql.Append($" AND ({sortExpr}, ts, id) {op} (@cv, @cts, @cid)");
                cmd.Parameters.AddWithValue("cv", cur.Value);
            }
            cmd.Parameters.AddWithValue("cts", NpgsqlDbType.TimestampTz, cur.Ts);
            cmd.Parameters.AddWithValue("cid", cur.Id);
        }

        sql.Append(" ORDER BY ").Append(orderBy).Append(" LIMIT @limit");
        cmd.Parameters.AddWithValue("limit", limit + 1);
        cmd.CommandText = sql.ToString();

        var items = new List<LogRow>(limit + 1);
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) items.Add(Read(r));

        string? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(items.Count - 1);
            var last = items[^1];
            double value = q.Sort switch { LogSort.DevMsDesc => last.DevMs ?? -1, LogSort.DiffCountDesc => last.DiffCount, _ => 0 };
            next = Cursor.Encode(new Cursor(value, last.Ts, last.Id));
        }
        return new LogPage(items, next);
    }

    private static void AppendFilters(LogQuery q, StringBuilder sql, NpgsqlCommand cmd, string p = "")
    {
        if (q.Verdicts is { Length: > 0 })
        {
            sql.Append($" AND {p}verdict = ANY(@verdicts)");
            cmd.Parameters.AddWithValue("verdicts", q.Verdicts.Select(v => (short)v).ToArray());
        }
        if (q.DiffKinds != DiffKinds.None)
        {
            sql.Append($" AND ({p}diff_kinds & @kinds) <> 0");
            cmd.Parameters.AddWithValue("kinds", (int)q.DiffKinds);
        }
        if (!string.IsNullOrEmpty(q.Path))
        {
            switch (q.PathMode)
            {
                case PathMatchMode.Exact:
                    sql.Append($" AND {p}path = @path");
                    cmd.Parameters.AddWithValue("path", q.Path);
                    break;
                case PathMatchMode.Contains:
                    sql.Append($" AND {p}path ILIKE @path");
                    cmd.Parameters.AddWithValue("path", "%" + EscapeLike(q.Path) + "%");
                    break;
                default:
                    sql.Append($" AND {p}path LIKE @path");
                    cmd.Parameters.AddWithValue("path", EscapeLike(q.Path.TrimEnd('*')) + "%");
                    break;
            }
        }
        if (!string.IsNullOrEmpty(q.Method))
        {
            sql.Append($" AND {p}method = @method");
            cmd.Parameters.AddWithValue("method", q.Method.ToUpperInvariant());
        }
        if (q.MainStatus is { } ms) { sql.Append($" AND {p}main_status = @ms"); cmd.Parameters.AddWithValue("ms", (short)ms); }
        if (q.DevStatus is { } dvs) { sql.Append($" AND {p}dev_status = @dvs"); cmd.Parameters.AddWithValue("dvs", (short)dvs); }
        if (q.Source is { } src) { sql.Append($" AND {p}source = @src"); cmd.Parameters.AddWithValue("src", (short)src); }
        if (q.JobId is { } job) { sql.Append($" AND {p}job_id = @job"); cmd.Parameters.AddWithValue("job", job); }
        if (q.ReplayOf is { } rof) { sql.Append($" AND {p}replay_of = @rof"); cmd.Parameters.AddWithValue("rof", rof); }
        if (q.Signature is { } sig) { sql.Append($" AND {p}signature = @sig"); cmd.Parameters.AddWithValue("sig", sig); }
        if (q.From is { } from) { sql.Append($" AND {p}ts >= @from"); cmd.Parameters.AddWithValue("from", NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(from, DateTimeKind.Utc)); }
        if (q.To is { } to) { sql.Append($" AND {p}ts < @to"); cmd.Parameters.AddWithValue("to", NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(to, DateTimeKind.Utc)); }
    }

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    public async Task<LogRow?> GetAsync(Guid missionId, Guid id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"SELECT {Columns} FROM traffic_log WHERE mission_id = @m AND ts = @ts AND id = @id");
        cmd.Parameters.AddWithValue("m", missionId);
        cmd.Parameters.AddWithValue("ts", NpgsqlDbType.TimestampTz, LogRecord.TimestampOf(id));
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Read(r) : null;
    }

    public async Task<BodyRow?> GetBodyAsync(Guid missionId, Guid id, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT request_headers, request_body, main_headers, main_body, dev_headers, dev_body, diff
            FROM traffic_body WHERE mission_id = @m AND ts = @ts AND log_id = @id
            """);
        cmd.Parameters.AddWithValue("m", missionId);
        cmd.Parameters.AddWithValue("ts", NpgsqlDbType.TimestampTz, LogRecord.TimestampOf(id));
        cmd.Parameters.AddWithValue("id", id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        byte[]? B(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<byte[]>(i);
        return new BodyRow(B(0), B(1), B(2), B(3), B(4), B(5), B(6));
    }

    /// <summary>Streams log rows with their bodies in keyset batches (ascending), for jobs and export. Memory stays bounded by <paramref name="batchSize"/>.</summary>
    public async IAsyncEnumerable<(LogRow Log, BodyRow Body)> StreamWithBodiesAsync(
        LogQuery filter, (DateTime Ts, Guid Id)? after, int batchSize, [EnumeratorCancellation] CancellationToken ct)
    {
        var cursorTs = after?.Ts;
        var cursorId = after?.Id;
        while (true)
        {
            var batch = new List<(LogRow, BodyRow)>(batchSize);
            await using (var cmd = ds.CreateCommand())
            {
                var sql = new StringBuilder($"""
                    SELECT l.id, l.mission_id, l.ts, l.method, l.path, l.query, l.main_status, l.dev_status, l.main_ms, l.dev_ms,
                           l.verdict, l.diff_kinds, l.diff_count, l.signature, l.rules_version, l.source, l.job_id, l.replay_of, l.note,
                           l.summary::text, b.request_headers, b.request_body, b.main_headers, b.main_body, b.dev_headers, b.dev_body, b.diff
                    FROM traffic_log l
                    LEFT JOIN traffic_body b ON b.mission_id = l.mission_id AND b.ts = l.ts AND b.log_id = l.id
                    WHERE l.mission_id = @mission
                    """);
                cmd.Parameters.AddWithValue("mission", filter.MissionId);
                AppendFilters(filter, sql, cmd, "l.");
                if (cursorTs is not null)
                {
                    sql.Append(" AND (l.ts, l.id) > (@cts, @cid)");
                    cmd.Parameters.AddWithValue("cts", NpgsqlDbType.TimestampTz, cursorTs.Value);
                    cmd.Parameters.AddWithValue("cid", cursorId!.Value);
                }
                sql.Append(" ORDER BY l.ts, l.id LIMIT @limit");
                cmd.Parameters.AddWithValue("limit", batchSize);
                cmd.CommandText = sql.ToString();
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    byte[]? B(int i) => r.IsDBNull(i) ? null : r.GetFieldValue<byte[]>(i);
                    batch.Add((Read(r), new BodyRow(B(20), B(21), B(22), B(23), B(24), B(25), B(26))));
                }
            }
            foreach (var item in batch) yield return item;
            if (batch.Count < batchSize) yield break;
            cursorTs = batch[^1].Item1.Ts;
            cursorId = batch[^1].Item1.Id;
        }
    }

    public async Task UpdateDiffsAsync(IReadOnlyList<DiffUpdate> updates, CancellationToken ct)
    {
        if (updates.Count == 0) return;
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            UPDATE traffic_log l SET verdict = u.verdict, diff_kinds = u.kinds, diff_count = u.cnt, signature = u.sig,
                   rules_version = u.rv, summary = u.summary::jsonb, note = coalesce(u.note, l.note)
            FROM unnest(@m::uuid[], @ts::timestamptz[], @id::uuid[], @v::smallint[], @k::int[], @c::int[], @s::bigint[], @rv::int[], @sum::text[], @note::text[])
                 AS u(m, ts, id, verdict, kinds, cnt, sig, rv, summary, note)
            WHERE l.mission_id = u.m AND l.ts = u.ts AND l.id = u.id
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("m", updates.Select(u => u.MissionId).ToArray());
            cmd.Parameters.AddWithValue("ts", updates.Select(u => u.Ts).ToArray());
            cmd.Parameters.AddWithValue("id", updates.Select(u => u.Id).ToArray());
            cmd.Parameters.AddWithValue("v", updates.Select(u => (short)u.Verdict).ToArray());
            cmd.Parameters.AddWithValue("k", updates.Select(u => u.DiffKinds).ToArray());
            cmd.Parameters.AddWithValue("c", updates.Select(u => u.DiffCount).ToArray());
            cmd.Parameters.AddWithValue("s", updates.Select(u => u.Signature).ToArray());
            cmd.Parameters.AddWithValue("rv", updates.Select(u => u.RulesVersion).ToArray());
            cmd.Parameters.AddWithValue("sum", updates.Select(u => u.SummaryJson).ToArray());
            cmd.Parameters.AddWithValue("note", updates.Select(u => u.Note).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("""
            UPDATE traffic_body b SET diff = u.diff
            FROM unnest(@m::uuid[], @ts::timestamptz[], @id::uuid[], @d::bytea[]) AS u(m, ts, id, diff)
            WHERE b.mission_id = u.m AND b.ts = u.ts AND b.log_id = u.id
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("m", updates.Select(u => u.MissionId).ToArray());
            cmd.Parameters.AddWithValue("ts", updates.Select(u => u.Ts).ToArray());
            cmd.Parameters.AddWithValue("id", updates.Select(u => u.Id).ToArray());
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Array | NpgsqlDbType.Bytea) { Value = updates.Select(u => u.Diff).ToArray() });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Moves rows between verdicts in the per-minute rollup after a re-diff changed them, so mission stats stay
    /// consistent with the log. Only live rows are counted in the rollup.
    /// </summary>
    public async Task MoveStatsAsync(IReadOnlyList<(Guid MissionId, DateTime Ts, Verdict From, Verdict To, float MainMs, float? DevMs)> moves, CancellationToken ct)
    {
        if (moves.Count == 0) return;
        var rows = moves.SelectMany(m =>
        {
            var bucket = new DateTime(m.Ts.Ticks - m.Ts.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            return new[] { (m.MissionId, bucket, m.From, -1L, -(double)m.MainMs, -(double)(m.DevMs ?? 0)), (m.MissionId, bucket, m.To, 1L, (double)m.MainMs, (double)(m.DevMs ?? 0)) };
        }).GroupBy(r => (r.MissionId, r.bucket, r.Item3))
          .Select(g => (g.Key.MissionId, g.Key.bucket, g.Key.Item3, g.Sum(x => x.Item4), g.Sum(x => x.Item5), g.Sum(x => x.Item6)))
          .Where(r => r.Item4 != 0).ToList();
        if (rows.Count == 0) return;
        await using var cmd = ds.CreateCommand("""
            INSERT INTO traffic_stats_minute AS s (mission_id, bucket, verdict, n, main_ms_sum, dev_ms_sum)
            SELECT * FROM unnest(@m::uuid[], @b::timestamptz[], @v::smallint[], @n::bigint[], @mm::float8[], @dm::float8[])
            ON CONFLICT (mission_id, bucket, verdict) DO UPDATE
            SET n = greatest(0, s.n + excluded.n), main_ms_sum = s.main_ms_sum + excluded.main_ms_sum, dev_ms_sum = s.dev_ms_sum + excluded.dev_ms_sum
            """);
        cmd.Parameters.AddWithValue("m", rows.Select(r => r.MissionId).ToArray());
        cmd.Parameters.AddWithValue("b", rows.Select(r => r.bucket).ToArray());
        cmd.Parameters.AddWithValue("v", rows.Select(r => (short)r.Item3).ToArray());
        cmd.Parameters.AddWithValue("n", rows.Select(r => r.Item4).ToArray());
        cmd.Parameters.AddWithValue("mm", rows.Select(r => r.Item5).ToArray());
        cmd.Parameters.AddWithValue("dm", rows.Select(r => r.Item6).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public sealed record VerdictCount(Verdict Verdict, long Count, double MainMsAvg, double DevMsAvg);

    /// <summary>Counts per verdict over a window, from the per-minute rollup (cheap even for millions of rows).</summary>
    public async Task<IReadOnlyList<VerdictCount>> StatsAsync(Guid missionId, DateTime from, DateTime to, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT verdict, sum(n)::bigint, sum(main_ms_sum) / nullif(sum(n), 0), sum(dev_ms_sum) / nullif(sum(n), 0)
            FROM traffic_stats_minute WHERE mission_id = @m AND bucket >= @f AND bucket < @t GROUP BY verdict
            """);
        cmd.Parameters.AddWithValue("m", missionId);
        cmd.Parameters.AddWithValue("f", NpgsqlDbType.TimestampTz, from);
        cmd.Parameters.AddWithValue("t", NpgsqlDbType.TimestampTz, to);
        var list = new List<VerdictCount>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new VerdictCount((Verdict)r.GetInt16(0), r.GetInt64(1), r.IsDBNull(2) ? 0 : r.GetDouble(2), r.IsDBNull(3) ? 0 : r.GetDouble(3)));
        return list;
    }

    public sealed record OverviewRow(Guid MissionId, int Bucket, Verdict Verdict, long Count, double MainMsSum, double DevMsSum);

    /// <summary>Per-mission counts in <paramref name="buckets"/> equal time buckets over a window, from the minute rollup (one query for all missions).</summary>
    public async Task<IReadOnlyList<OverviewRow>> OverviewAsync(DateTime from, DateTime to, int buckets, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT mission_id,
                   least(@n - 1, floor(extract(epoch FROM bucket - @f) / @w)::int) AS b,
                   verdict, sum(n)::bigint, sum(main_ms_sum), sum(dev_ms_sum)
            FROM traffic_stats_minute WHERE bucket >= @f AND bucket < @t
            GROUP BY 1, 2, 3
            """);
        cmd.Parameters.AddWithValue("f", NpgsqlDbType.TimestampTz, from);
        cmd.Parameters.AddWithValue("t", NpgsqlDbType.TimestampTz, to);
        cmd.Parameters.AddWithValue("n", buckets);
        cmd.Parameters.AddWithValue("w", Math.Max(1.0, (to - from).TotalSeconds / buckets));
        var list = new List<OverviewRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new OverviewRow(r.GetGuid(0), r.GetInt32(1), (Verdict)r.GetInt16(2), r.GetInt64(3), r.GetDouble(4), r.GetDouble(5)));
        return list;
    }

    public sealed record TimeBucket(DateTime Bucket, Verdict Verdict, long Count);

    public async Task<IReadOnlyList<TimeBucket>> TimelineAsync(Guid missionId, DateTime from, DateTime to, int bucketMinutes, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT to_timestamp(floor(extract(epoch FROM bucket) / (@w * 60)) * (@w * 60)) AS b, verdict, sum(n)::bigint
            FROM traffic_stats_minute WHERE mission_id = @m AND bucket >= @f AND bucket < @t
            GROUP BY 1, 2 ORDER BY 1
            """);
        cmd.Parameters.AddWithValue("m", missionId);
        cmd.Parameters.AddWithValue("f", NpgsqlDbType.TimestampTz, from);
        cmd.Parameters.AddWithValue("t", NpgsqlDbType.TimestampTz, to);
        cmd.Parameters.AddWithValue("w", Math.Max(1, bucketMinutes));
        var list = new List<TimeBucket>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new TimeBucket(r.GetFieldValue<DateTime>(0), (Verdict)r.GetInt16(1), r.GetInt64(2)));
        return list;
    }

    public sealed record Cluster(long Signature, long Count, DateTime FirstSeen, DateTime LastSeen, Guid ExampleId, string ExamplePath, string ExampleMethod, string? ExampleSummary);

    /// <summary>Mismatches grouped by diff signature ("cause") in a time window.</summary>
    public async Task<IReadOnlyList<Cluster>> ClustersAsync(Guid missionId, DateTime from, DateTime to, int limit, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand("""
            SELECT signature, count(*), min(ts), max(ts),
                   (array_agg(id ORDER BY ts DESC))[1], (array_agg(path ORDER BY ts DESC))[1], (array_agg(method ORDER BY ts DESC))[1],
                   (array_agg(summary::text ORDER BY ts DESC))[1]
            FROM traffic_log
            WHERE mission_id = @m AND ts >= @f AND ts < @t AND signature <> 0
            GROUP BY signature ORDER BY count(*) DESC LIMIT @l
            """);
        cmd.Parameters.AddWithValue("m", missionId);
        cmd.Parameters.AddWithValue("f", NpgsqlDbType.TimestampTz, from);
        cmd.Parameters.AddWithValue("t", NpgsqlDbType.TimestampTz, to);
        cmd.Parameters.AddWithValue("l", Math.Clamp(limit, 1, 200));
        var list = new List<Cluster>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new Cluster(r.GetInt64(0), r.GetInt64(1), r.GetFieldValue<DateTime>(2), r.GetFieldValue<DateTime>(3), r.GetGuid(4),
                r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return list;
    }

    /// <summary>Rows newer than (ts, id), for live updates. Bounded by <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<LogRow>> NewerThanAsync(Guid missionId, DateTime ts, Guid id, int limit, CancellationToken ct)
    {
        await using var cmd = ds.CreateCommand($"""
            SELECT * FROM (
              SELECT {Columns} FROM traffic_log WHERE mission_id = @m AND ts >= @ts AND (ts, id) > (@ts, @id)
              ORDER BY ts DESC, id DESC LIMIT @l) x
            ORDER BY ts, id
            """);
        cmd.Parameters.AddWithValue("m", missionId);
        cmd.Parameters.AddWithValue("ts", NpgsqlDbType.TimestampTz, ts);
        cmd.Parameters.AddWithValue("id", id);
        cmd.Parameters.AddWithValue("l", limit);
        var list = new List<LogRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(Read(r));
        return list;
    }

    private static LogRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), DateTime.SpecifyKind(r.GetFieldValue<DateTime>(2), DateTimeKind.Utc), r.GetString(3), r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5), r.GetInt16(6), r.IsDBNull(7) ? null : r.GetInt16(7), r.GetFloat(8),
        r.IsDBNull(9) ? null : r.GetFloat(9), (Verdict)r.GetInt16(10), r.GetInt32(11), r.GetInt32(12), r.GetInt64(13), r.GetInt32(14),
        (LogSource)r.GetInt16(15), r.IsDBNull(16) ? null : r.GetGuid(16), r.IsDBNull(17) ? null : r.GetGuid(17),
        r.IsDBNull(18) ? null : r.GetString(18), r.IsDBNull(19) ? null : r.GetString(19));
}

/// <summary>Opaque keyset cursor: sort value + (ts, id).</summary>
public readonly record struct Cursor(double Value, DateTime Ts, Guid Id)
{
    public static string Encode(Cursor c)
    {
        var raw = string.Create(CultureInfo.InvariantCulture, $"{c.Value:R}|{c.Ts.Ticks}|{c.Id:N}");
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string? s, out Cursor c)
    {
        c = default;
        if (string.IsNullOrEmpty(s)) return false;
        try
        {
            var b64 = s.Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(b64)).Split('|');
            if (parts.Length != 3) return false;
            c = new Cursor(double.Parse(parts[0], CultureInfo.InvariantCulture), new DateTime(long.Parse(parts[1], CultureInfo.InvariantCulture), DateTimeKind.Utc), Guid.ParseExact(parts[2], "N"));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
