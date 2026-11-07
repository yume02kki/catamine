using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using Mirror.Core.Diffing;
using Mirror.Core.Http;
using Mirror.Data;
using Mirror.Proxy.Capture;
using Mirror.Proxy.Metrics;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;

namespace Mirror.Proxy.Mirroring;

/// <summary>Pool of workers that send captured requests to Dev, diff the responses and hand records to the log writer.</summary>
public sealed class MirrorWorkerService(
    MirrorQueue queue,
    LogQueue logQueue,
    DevPipelines pipelines,
    [FromKeyedServices("dev")] HttpClient devClient,
    IOptions<ProxyOptions> options,
    ILogger<MirrorWorkerService> log) : BackgroundService
{
    private readonly ProxyOptions _o = options.Value;
    private readonly SemaphoreSlim _diffGate = new(Math.Max(1, options.Value.DiffConcurrency));

    protected override Task ExecuteAsync(CancellationToken ct) =>
        Task.WhenAll(Enumerable.Range(0, Math.Max(1, _o.Workers)).Select(_ => Task.Run(() => RunWorkerAsync(ct), ct)));

    private async Task RunWorkerAsync(CancellationToken ct)
    {
        var reader = queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                while (reader.TryRead(out var job))
                {
                    using (job)
                    {
                        try { await ProcessAsync(job, ct); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                        catch (Exception ex)
                        {
                            ProxyMetrics.Dropped.WithLabels("worker_error").Inc();
                            log.LogWarning(ex, "Mirror job {Id} failed", job.Id);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal async Task ProcessAsync(MirrorJob job, CancellationToken ct)
    {
        var m = job.Mission;
        var slug = m.Config.Slug;
        var requestBody = job.RequestBody?.ToArray();

        var outcome = DevOutcome.Ok;
        string? devError = null;
        CapturedResponse? dev = null;
        var started = Stopwatch.GetTimestamp();
        using (var req = OutboundRequest.Build(job.Method, m.Config.DevUrl, job.Path, job.Query, job.RequestHeaders, requestBody,
                   m.StripHeaders, m.SetHeaders,
                   [new("X-Mirror-Request", "1"), new("X-Mirror-Id", job.Id.ToString())]))
        {
            try
            {
                dev = await pipelines.Get(m).ExecuteAsync(async token =>
                {
                    using var resp = await devClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
                    return await OutboundRequest.ReadAsync(resp, _o.MaxCapturedResponseBytes, token);
                }, ct);
            }
            catch (BrokenCircuitException)
            {
                ProxyMetrics.Dropped.WithLabels("circuit_open").Inc();
                return;
            }
            catch (RateLimiterRejectedException)
            {
                ProxyMetrics.Dropped.WithLabels("dev_concurrency").Inc();
                return;
            }
            catch (TimeoutRejectedException)
            {
                outcome = DevOutcome.Timeout;
                devError = $"Dev did not answer within {m.Config.DevTimeoutMs} ms";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                outcome = DevOutcome.Error;
                devError = "Dev request failed: " + (ex.InnerException?.Message ?? ex.Message);
            }
        }
        var devMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        ProxyMetrics.DevDuration.WithLabels(slug).Observe(devMs / 1000);
        ProxyMetrics.DevRequests.WithLabels(slug, outcome switch { DevOutcome.Ok => "ok", DevOutcome.Timeout => "timeout", _ => "error" }).Inc();

        if (dev is { Overflow: true })
        {
            devError = $"Dev response exceeds {_o.MaxCapturedResponseBytes / 1024} KB capture limit; body not compared";
        }

        var request = new ExchangeRequest(m.Config.Id, job.Method, job.Path, job.Query, job.RequestHeaders, requestBody);
        var main = new ExchangeSide(job.MainStatus, ToDict(job.MainHeaders), job.MainBody.ToArray(), job.MainMs);
        var devSide = dev is null ? null : new ExchangeSide(dev.Status, dev.Headers, dev.Body, devMs);

        LogRecord record;
        await _diffGate.WaitAsync(ct);
        var diffStart = Stopwatch.GetTimestamp();
        try
        {
            (record, _) = ExchangeRecorder.Record(request, main, devSide, outcome, devError, m.Rules, m.DiffSettings,
                new RecordOptions(_o.StoreBodies, LogSource.Live), (job.Id, job.Ts));
        }
        finally
        {
            _diffGate.Release();
            ProxyMetrics.DiffDuration.Observe(Stopwatch.GetElapsedTime(diffStart).TotalSeconds);
        }

        ProxyMetrics.Verdicts.WithLabels(slug, record.Verdict.ToWire()).Inc();
        logQueue.TryEnqueue(record);
    }

    internal static Dictionary<string, string> ToDict(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headers) d[k] = d.TryGetValue(k, out var e) ? e + ", " + v : v;
        return d;
    }
}
