using System.Diagnostics;
using Microsoft.Extensions.Options;
using Mirror.Data;
using Mirror.Proxy.Metrics;
using Mirror.Proxy.Mirroring;
using Npgsql;

namespace Mirror.Proxy.Logging;

/// <summary>
/// Drains the log queue in batches (size or interval) and writes them with binary COPY. If Postgres is unavailable
/// the batch is dropped and counted, and writes back off exponentially — producers are never blocked.
/// </summary>
public sealed class LogWriterService(
    LogQueue queue,
    NpgsqlDataSource ds,
    IOptions<ProxyOptions> options,
    ILogger<LogWriterService> log) : BackgroundService
{
    private readonly ProxyOptions _o = options.Value;
    private DateTime _backoffUntil = DateTime.MinValue;
    private TimeSpan _backoff = TimeSpan.Zero;
    private DateTime _lastErrorLog = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var batch = new List<LogRecord>(_o.LogBatchSize);
        var reader = queue.Reader;
        var interval = TimeSpan.FromMilliseconds(Math.Max(10, _o.LogFlushIntervalMs));
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                var deadline = Stopwatch.GetTimestamp() + (long)(interval.TotalSeconds * Stopwatch.Frequency);
                // Collect until the batch is full or the flush interval has passed.
                while (batch.Count < _o.LogBatchSize)
                {
                    if (reader.TryRead(out var r))
                    {
                        queue.OnDequeued(r);
                        batch.Add(r);
                        continue;
                    }
                    var remaining = deadline - Stopwatch.GetTimestamp();
                    if (remaining <= 0) break;
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency));
                    try
                    {
                        if (!await reader.WaitToReadAsync(wait.Token)) break;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }
                }
                await FlushAsync(batch, ct);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Best effort final flush on shutdown.
            while (reader.TryRead(out var r)) { queue.OnDequeued(r); batch.Add(r); }
            using var final = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await FlushAsync(batch, final.Token);
        }
    }

    private async Task FlushAsync(List<LogRecord> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        if (DateTime.UtcNow < _backoffUntil)
        {
            ProxyMetrics.LogDropped.WithLabels("db_unavailable").Inc(batch.Count);
            return;
        }
        var sw = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await LogCopyWriter.WriteAsync(ds, batch, updateStats: true, timeout.Token);
            ProxyMetrics.LogWritten.Inc(batch.Count);
            ProxyMetrics.LogBatchDuration.Observe(sw.Elapsed.TotalSeconds);
            _backoff = TimeSpan.Zero;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested || ex is not OperationCanceledException)
        {
            ProxyMetrics.LogDropped.WithLabels("write_failed").Inc(batch.Count);
            _backoff = _backoff == TimeSpan.Zero ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(Math.Min(30, _backoff.TotalSeconds * 2));
            _backoffUntil = DateTime.UtcNow + _backoff;
            if (DateTime.UtcNow - _lastErrorLog > TimeSpan.FromSeconds(30))
            {
                _lastErrorLog = DateTime.UtcNow;
                log.LogWarning("Dropping {Count} log records, Postgres write failed (backing off {Backoff}s): {Message}",
                    batch.Count, _backoff.TotalSeconds, ex.Message);
            }
        }
    }
}
