using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Mirror.Proxy.Capture;
using Mirror.Proxy.Config;
using Mirror.Proxy.Mirroring;
using Npgsql;

namespace Mirror.Proxy.Metrics;

/// <summary>Publishes gauges every second and writes a metrics snapshot to <c>proxy_instances</c> for the UI. Failures are ignored.</summary>
public sealed class HeartbeatService(
    MirrorQueue queue,
    LogQueue logQueue,
    CaptureBudget budget,
    MissionConfigStore store,
    DevPipelines pipelines,
    NpgsqlDataSource ds,
    IOptions<ProxyOptions> options,
    ILogger<HeartbeatService> log) : BackgroundService
{
    private readonly ProxyOptions _o = options.Value;
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var tick = 0;
        while (await timer.WaitForNextTickAsync(ct))
        {
            ProxyMetrics.QueueDepth.Set(queue.Count);
            ProxyMetrics.LogQueueDepth.Set(logQueue.Count);
            ProxyMetrics.CaptureBytes.Set(budget.Used);
            pipelines.PublishMetrics(store.All);
            if (++tick % Math.Max(1, _o.HeartbeatSeconds) == 0) await WriteHeartbeatAsync(ct);
        }
    }

    public object Snapshot() => new
    {
        instanceId = _o.InstanceId,
        startedAt = _started,
        queueDepth = queue.Count,
        queueCapacity = _o.QueueCapacity,
        logQueueDepth = logQueue.Count,
        captureBytes = budget.Used,
        enqueued = ProxyMetrics.Enqueued.Value,
        dropped = Labels(ProxyMetrics.Dropped),
        notMirrored = Labels(ProxyMetrics.NotMirrored),
        logWritten = ProxyMetrics.LogWritten.Value,
        logDropped = Labels(ProxyMetrics.LogDropped),
        mainRequests = Labels(ProxyMetrics.MainRequests),
        mainLatencyMs = new
        {
            mirrored = LatencyTracker.Mirrored.Snapshot(),
            unmirrored = LatencyTracker.Unmirrored.Snapshot(),
        },
        circuits = pipelines.CircuitStates(store.All),
        configSource = store.Source,
        configLoadedAt = store.LoadedAt,
        workingSetBytes = Process.GetCurrentProcess().WorkingSet64,
    };

    private static Dictionary<string, double> Labels(Prometheus.Counter c) =>
        c.GetAllLabelValues().ToDictionary(l => string.Join(",", l), l => c.WithLabels(l).Value);

    private async Task WriteHeartbeatAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await using var cmd = ds.CreateCommand("""
                INSERT INTO proxy_instances (instance_id, last_seen, started_at, metrics) VALUES (@i, now(), @s, @m::jsonb)
                ON CONFLICT (instance_id) DO UPDATE SET last_seen = now(), metrics = excluded.metrics
                """);
            cmd.Parameters.AddWithValue("i", _o.InstanceId);
            cmd.Parameters.AddWithValue("s", _started);
            cmd.Parameters.AddWithValue("m", JsonSerializer.Serialize(Snapshot()));
            await cmd.ExecuteNonQueryAsync(timeout.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogDebug("Heartbeat write failed: {Message}", ex.Message);
        }
    }
}
