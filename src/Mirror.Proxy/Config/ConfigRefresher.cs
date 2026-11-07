using System.Text.Json;
using Microsoft.Extensions.Options;
using Mirror.Core.Models;
using Mirror.Data;
using Mirror.Proxy.Metrics;
using Npgsql;

namespace Mirror.Proxy.Config;

/// <summary>
/// Polls Postgres for mission configs (the proxy never talks to the api service). On failure the last snapshot keeps
/// serving; on a cold start with Postgres down the snapshot file (if configured) is used.
/// </summary>
public sealed class ConfigRefresher(
    NpgsqlDataSource ds,
    MissionConfigStore store,
    IOptions<ProxyOptions> options,
    ILogger<ConfigRefresher> log) : BackgroundService
{
    private readonly ProxyOptions _o = options.Value;
    private readonly HashSet<(Guid, DateOnly)> _partitionsEnsured = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!await TryRefreshAsync(ct)) TryLoadSnapshotFile();

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, _o.ConfigRefreshSeconds)));
        while (await timer.WaitForNextTickAsync(ct))
            await TryRefreshAsync(ct);
    }

    private async Task<bool> TryRefreshAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var missions = await MissionSnapshotLoader.LoadAsync(ds, timeout.Token);
            store.Apply(missions, _o.MaxDiffBodyBytes, "postgres");
            ProxyMetrics.ConfigAge.Set(0);
            WriteSnapshotFile(missions);
            try { await EnsurePartitionsAsync(missions, timeout.Token); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning("Partition pre-creation failed: {Message}", ex.Message); }
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ProxyMetrics.ConfigRefreshFailures.Inc();
            if (store.LoadedAt is { } at) ProxyMetrics.ConfigAge.Set((DateTimeOffset.UtcNow - at).TotalSeconds);
            log.LogWarning("Config refresh failed, serving last snapshot ({Source}, {Age}): {Message}",
                store.Source ?? "none", store.LoadedAt, ex.Message);
            return false;
        }
    }

    /// <summary>Creates today's and tomorrow's partitions once per mission/day, so the writer rarely hits a missing partition.</summary>
    private async Task EnsurePartitionsAsync(List<MissionConfig> missions, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var todo = missions.Where(m => !_partitionsEnsured.Contains((m.Id, today))).ToList();
        if (todo.Count == 0) return;
        await using var conn = await ds.OpenConnectionAsync(ct);
        foreach (var m in todo)
        {
            await LogCopyWriter.EnsurePartitionsAsync(conn, m.Id, today, 2, ct);
            _partitionsEnsured.Add((m.Id, today));
        }
    }

    private void WriteSnapshotFile(List<MissionConfig> missions)
    {
        if (string.IsNullOrEmpty(_o.SnapshotFile)) return;
        try
        {
            var tmp = _o.SnapshotFile + ".tmp";
            File.WriteAllBytes(tmp, JsonSerializer.SerializeToUtf8Bytes(missions, MissionSnapshotLoader.JsonOptions));
            File.Move(tmp, _o.SnapshotFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "Could not write snapshot file");
        }
    }

    private void TryLoadSnapshotFile()
    {
        if (string.IsNullOrEmpty(_o.SnapshotFile) || !File.Exists(_o.SnapshotFile)) return;
        try
        {
            var missions = JsonSerializer.Deserialize<List<MissionConfig>>(File.ReadAllBytes(_o.SnapshotFile), MissionSnapshotLoader.JsonOptions) ?? [];
            store.Apply(missions, _o.MaxDiffBodyBytes, "snapshot-file");
            log.LogWarning("Postgres unavailable at startup: serving {Count} missions from snapshot file {File}", missions.Count, _o.SnapshotFile);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            log.LogError(ex, "Snapshot file {File} unreadable", _o.SnapshotFile);
        }
    }
}
