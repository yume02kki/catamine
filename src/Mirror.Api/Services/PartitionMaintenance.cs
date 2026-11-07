using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mirror.Data;
using Npgsql;

namespace Mirror.Api.Services;

/// <summary>
/// Creates partitions ahead of time and enforces per-mission retention and size limits by dropping whole day partitions.
/// Runs on one api instance at a time (advisory lock).
/// </summary>
public sealed class PartitionMaintenance(
    IServiceScopeFactory scopes,
    NpgsqlDataSource ds,
    IOptions<ApiOptions> options,
    ILogger<PartitionMaintenance> log) : BackgroundService
{
    private const long LockKey = 0x6D6972726F72_01; // "mirror" + 1

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Max(1, options.Value.MaintenanceIntervalMinutes)));
        do
        {
            try { await RunOnceAsync(ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning(ex, "Partition maintenance failed"); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_try_advisory_lock(@k)", conn))
        {
            lockCmd.Parameters.AddWithValue("k", LockKey);
            if (await lockCmd.ExecuteScalarAsync(ct) is not true) return;
        }
        try
        {
            List<MissionEntity> missions;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<MirrorDbContext>();
                missions = await db.Missions.AsNoTracking().ToListAsync(ct);
            }
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            foreach (var m in missions)
            {
                await LogCopyWriter.EnsurePartitionsAsync(conn, m.Id, today, Math.Max(1, options.Value.PartitionsAhead), ct);
                await EnforceAsync(conn, m, today, ct);
            }
            await using (var cmd = new NpgsqlCommand("DELETE FROM proxy_instances WHERE last_seen < now() - interval '1 day'", conn))
                await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
            unlock.Parameters.AddWithValue("k", LockKey);
            await unlock.ExecuteScalarAsync(CancellationToken.None);
        }
    }

    private async Task EnforceAsync(NpgsqlConnection conn, MissionEntity m, DateOnly today, CancellationToken ct)
    {
        var parts = new List<(DateOnly Day, long Bytes)>();
        await using (var cmd = new NpgsqlCommand("SELECT day, bytes FROM mirror_mission_partitions(@m)", conn))
        {
            cmd.Parameters.AddWithValue("m", m.Id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) parts.Add((r.GetFieldValue<DateOnly>(0), r.GetInt64(1)));
        }

        var cutoff = today.AddDays(-Math.Max(1, m.RetentionDays) + 1);
        var drop = parts.Where(p => p.Day < cutoff).Select(p => p.Day).ToList();

        if (m.MaxStorageMb is { } maxMb)
        {
            var limit = (long)maxMb * 1024 * 1024;
            var total = parts.Where(p => !drop.Contains(p.Day)).Sum(p => p.Bytes);
            foreach (var p in parts.Where(p => !drop.Contains(p.Day) && p.Day < today).OrderBy(p => p.Day))
            {
                if (total <= limit) break;
                drop.Add(p.Day);
                total -= p.Bytes;
            }
        }

        foreach (var day in drop)
        {
            await using var cmd = new NpgsqlCommand("SELECT mirror_drop_day_partition(@m, @d)", conn);
            cmd.Parameters.AddWithValue("m", m.Id);
            cmd.Parameters.AddWithValue("d", day);
            await cmd.ExecuteNonQueryAsync(ct);
            log.LogInformation("Dropped partition {Day} of mission {Slug} (retention {Days} d, limit {Mb} MB)", day, m.Slug, m.RetentionDays, m.MaxStorageMb);
        }
        if (drop.Count > 0)
        {
            await using var cmd = new NpgsqlCommand("DELETE FROM traffic_stats_minute WHERE mission_id = @m AND bucket < @c", conn);
            cmd.Parameters.AddWithValue("m", m.Id);
            cmd.Parameters.AddWithValue("c", drop.Max().AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }
}
