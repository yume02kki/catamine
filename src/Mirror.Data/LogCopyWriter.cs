using Npgsql;
using NpgsqlTypes;

namespace Mirror.Data;

/// <summary>Writes batches of log records with binary COPY (log index + bodies), then updates minute stats and notifies listeners.</summary>
public static class LogCopyWriter
{
    public const string NotifyChannel = "mirror_log";

    private const string CopyLog = """
        COPY traffic_log (id, mission_id, ts, method, path, query, main_status, dev_status, main_ms, dev_ms,
                          verdict, diff_kinds, diff_count, signature, rules_version, source, job_id, replay_of, note, summary)
        FROM STDIN (FORMAT BINARY)
        """;

    private const string CopyBody = """
        COPY traffic_body (log_id, mission_id, ts, codec, request_headers, request_body, main_headers, main_body, dev_headers, dev_body, diff)
        FROM STDIN (FORMAT BINARY)
        """;

    private const string UpsertStats = """
        INSERT INTO traffic_stats_minute AS s (mission_id, bucket, verdict, n, main_ms_sum, dev_ms_sum)
        SELECT * FROM unnest(@m::uuid[], @b::timestamptz[], @v::smallint[], @n::bigint[], @mm::float8[], @dm::float8[])
        ON CONFLICT (mission_id, bucket, verdict) DO UPDATE
        SET n = s.n + excluded.n, main_ms_sum = s.main_ms_sum + excluded.main_ms_sum, dev_ms_sum = s.dev_ms_sum + excluded.dev_ms_sum
        """;

    /// <summary>Writes the batch in one transaction. If a partition is missing it is created and the batch retried once.</summary>
    public static async Task WriteAsync(NpgsqlDataSource dataSource, IReadOnlyList<LogRecord> batch, bool updateStats, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        try
        {
            await WriteOnceAsync(dataSource, batch, updateStats, ct);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation && ex.Message.Contains("partition", StringComparison.OrdinalIgnoreCase))
        {
            await EnsurePartitionsAsync(dataSource, batch, ct);
            await WriteOnceAsync(dataSource, batch, updateStats, ct);
        }
    }

    private static async Task WriteOnceAsync(NpgsqlDataSource dataSource, IReadOnlyList<LogRecord> batch, bool updateStats, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await using (var w = await conn.BeginBinaryImportAsync(CopyLog, ct))
        {
            foreach (var r in batch)
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(r.Id, NpgsqlDbType.Uuid, ct);
                await w.WriteAsync(r.MissionId, NpgsqlDbType.Uuid, ct);
                await w.WriteAsync(r.Ts, NpgsqlDbType.TimestampTz, ct);
                await w.WriteAsync(r.Method, NpgsqlDbType.Text, ct);
                await w.WriteAsync(r.Path, NpgsqlDbType.Text, ct);
                await WriteNullable(w, r.Query, NpgsqlDbType.Text, ct);
                await w.WriteAsync(r.MainStatus, NpgsqlDbType.Smallint, ct);
                await WriteNullable(w, r.DevStatus, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync(r.MainMs, NpgsqlDbType.Real, ct);
                await WriteNullable(w, r.DevMs, NpgsqlDbType.Real, ct);
                await w.WriteAsync((short)r.Verdict, NpgsqlDbType.Smallint, ct);
                await w.WriteAsync(r.DiffKinds, NpgsqlDbType.Integer, ct);
                await w.WriteAsync(r.DiffCount, NpgsqlDbType.Integer, ct);
                await w.WriteAsync(r.Signature, NpgsqlDbType.Bigint, ct);
                await w.WriteAsync(r.RulesVersion, NpgsqlDbType.Integer, ct);
                await w.WriteAsync((short)r.Source, NpgsqlDbType.Smallint, ct);
                await WriteNullable(w, r.JobId, NpgsqlDbType.Uuid, ct);
                await WriteNullable(w, r.ReplayOf, NpgsqlDbType.Uuid, ct);
                await WriteNullable(w, r.Note, NpgsqlDbType.Text, ct);
                await WriteNullable(w, r.SummaryJson, NpgsqlDbType.Jsonb, ct);
            }
            await w.CompleteAsync(ct);
        }

        await using (var w = await conn.BeginBinaryImportAsync(CopyBody, ct))
        {
            foreach (var r in batch)
            {
                await w.StartRowAsync(ct);
                await w.WriteAsync(r.Id, NpgsqlDbType.Uuid, ct);
                await w.WriteAsync(r.MissionId, NpgsqlDbType.Uuid, ct);
                await w.WriteAsync(r.Ts, NpgsqlDbType.TimestampTz, ct);
                await w.WriteAsync((short)1, NpgsqlDbType.Smallint, ct);
                await WriteNullable(w, r.RequestHeaders, NpgsqlDbType.Bytea, ct);
                await WriteNullable(w, r.RequestBody, NpgsqlDbType.Bytea, ct);
                await WriteNullable(w, r.MainHeaders, NpgsqlDbType.Bytea, ct);
                await WriteNullable(w, r.MainBody, NpgsqlDbType.Bytea, ct);
                await WriteNullable(w, r.DevHeaders, NpgsqlDbType.Bytea, ct);
                await WriteNullable(w, r.DevBody, NpgsqlDbType.Bytea, ct);
                await WriteNullable(w, r.Diff, NpgsqlDbType.Bytea, ct);
            }
            await w.CompleteAsync(ct);
        }

        if (updateStats)
        {
            var groups = batch
                .GroupBy(r => (r.MissionId, Bucket: new DateTime(r.Ts.Ticks - r.Ts.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc), r.Verdict))
                .ToList();
            await using var cmd = new NpgsqlCommand(UpsertStats, conn, tx);
            cmd.Parameters.AddWithValue("m", groups.Select(g => g.Key.MissionId).ToArray());
            cmd.Parameters.AddWithValue("b", groups.Select(g => g.Key.Bucket).ToArray());
            cmd.Parameters.AddWithValue("v", groups.Select(g => (short)g.Key.Verdict).ToArray());
            cmd.Parameters.AddWithValue("n", groups.Select(g => (long)g.Count()).ToArray());
            cmd.Parameters.AddWithValue("mm", groups.Select(g => g.Sum(r => (double)r.MainMs)).ToArray());
            cmd.Parameters.AddWithValue("dm", groups.Select(g => g.Sum(r => (double)(r.DevMs ?? 0))).ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var notify = new NpgsqlCommand("SELECT pg_notify(@c, @p)", conn, tx))
        {
            notify.Parameters.AddWithValue("c", NotifyChannel);
            notify.Parameters.AddWithValue("p", string.Join(",", batch.Select(r => r.MissionId).Distinct()));
            await notify.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    public static async Task EnsurePartitionsAsync(NpgsqlDataSource dataSource, IEnumerable<LogRecord> batch, CancellationToken ct)
    {
        var needed = batch.GroupBy(r => r.MissionId)
            .Select(g => (Mission: g.Key, From: DateOnly.FromDateTime(g.Min(r => r.Ts)), To: DateOnly.FromDateTime(g.Max(r => r.Ts))));
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        foreach (var (mission, from, to) in needed)
            await EnsurePartitionsAsync(conn, mission, from, to.DayNumber - from.DayNumber + 1, ct);
    }

    public static async Task EnsurePartitionsAsync(NpgsqlConnection conn, Guid mission, DateOnly from, int days, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT mirror_ensure_partitions(@m, @d, @n)", conn);
        cmd.Parameters.AddWithValue("m", mission);
        cmd.Parameters.AddWithValue("d", from);
        cmd.Parameters.AddWithValue("n", days);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Task WriteNullable<T>(NpgsqlBinaryImporter w, T? value, NpgsqlDbType type, CancellationToken ct) where T : class =>
        value is null ? w.WriteNullAsync(ct) : w.WriteAsync(value, type, ct);

    private static Task WriteNullable<T>(NpgsqlBinaryImporter w, T? value, NpgsqlDbType type, CancellationToken ct) where T : struct =>
        value is null ? w.WriteNullAsync(ct) : w.WriteAsync(value.Value, type, ct);
}
