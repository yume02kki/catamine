using Prometheus;

namespace Mirror.Proxy.Metrics;

public static class ProxyMetrics
{
    private static readonly double[] LatencyBuckets =
        [0.0005, 0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10];

    public static readonly Histogram MainDuration = Prometheus.Metrics.CreateHistogram(
        "main_request_duration_seconds", "Time to forward a request to Main and stream the response back (critical path).",
        new HistogramConfiguration { Buckets = LatencyBuckets, LabelNames = ["mirrored"] });

    public static readonly Counter MainRequests = Prometheus.Metrics.CreateCounter(
        "main_requests_total", "Requests forwarded to Main.", "outcome");

    public static readonly Gauge QueueDepth = Prometheus.Metrics.CreateGauge(
        "mirror_queue_depth", "Mirror jobs waiting for a worker.");

    public static readonly Counter Enqueued = Prometheus.Metrics.CreateCounter(
        "mirror_enqueued_total", "Mirror jobs accepted into the queue.");

    public static readonly Counter Dropped = Prometheus.Metrics.CreateCounter(
        "mirror_dropped_total", "Mirror jobs dropped (never sent to Dev).", "reason");

    public static readonly Counter NotMirrored = Prometheus.Metrics.CreateCounter(
        "mirror_not_mirrored_total", "Sampled requests logged as not mirrored.", "reason");

    public static readonly Counter DevRequests = Prometheus.Metrics.CreateCounter(
        "dev_requests_total", "Dev calls by outcome (ok, error, timeout).", "mission", "outcome");

    public static readonly Histogram DevDuration = Prometheus.Metrics.CreateHistogram(
        "dev_request_duration_seconds", "Dev call duration.", new HistogramConfiguration { Buckets = LatencyBuckets, LabelNames = ["mission"] });

    public static readonly Counter Verdicts = Prometheus.Metrics.CreateCounter(
        "mirror_verdicts_total", "Diff verdicts.", "mission", "verdict");

    public static readonly Histogram DiffDuration = Prometheus.Metrics.CreateHistogram(
        "diff_duration_seconds", "Diff engine time per job.", new HistogramConfiguration { Buckets = LatencyBuckets });

    public static readonly Gauge CircuitState = Prometheus.Metrics.CreateGauge(
        "dev_circuit_state", "Dev circuit breaker state per mission (0 closed, 1 open, 2 half-open, 3 isolated).", "mission");

    public static readonly Gauge CaptureBytes = Prometheus.Metrics.CreateGauge(
        "mirror_capture_bytes", "Bytes held by in-flight request/response captures.");

    public static readonly Gauge LogQueueDepth = Prometheus.Metrics.CreateGauge(
        "log_queue_depth", "Log records waiting to be written.");

    public static readonly Counter LogWritten = Prometheus.Metrics.CreateCounter(
        "log_records_written_total", "Log records written to Postgres.");

    public static readonly Counter LogDropped = Prometheus.Metrics.CreateCounter(
        "log_records_dropped_total", "Log records dropped.", "reason");

    public static readonly Histogram LogBatchDuration = Prometheus.Metrics.CreateHistogram(
        "log_batch_write_seconds", "COPY batch write duration.", new HistogramConfiguration { Buckets = LatencyBuckets });

    public static readonly Counter ConfigRefreshFailures = Prometheus.Metrics.CreateCounter(
        "config_refresh_failures_total", "Failed config refreshes from Postgres.");

    public static readonly Gauge ConfigAge = Prometheus.Metrics.CreateGauge(
        "config_snapshot_age_seconds", "Age of the config snapshot when the last refresh failed (0 = fresh).");
}
