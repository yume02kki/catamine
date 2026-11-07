namespace Mirror.Proxy;

/// <summary>Bound from the <c>Mirror</c> configuration section (env: <c>Mirror__QueueCapacity</c> etc.).</summary>
public sealed class ProxyOptions
{
    /// <summary>Bounded mirror queue size. When full, new mirror jobs are dropped (and counted).</summary>
    public int QueueCapacity { get; set; } = 10_000;
    /// <summary>Concurrent mirror workers (mostly waiting on Dev I/O).</summary>
    public int Workers { get; set; } = 64;
    /// <summary>Concurrent CPU-bound diff computations; keeps diffing from competing with the main path for cores.</summary>
    public int DiffConcurrency { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);
    /// <summary>Main responses larger than this are not mirrored (they are still forwarded normally).</summary>
    public int MaxCapturedResponseBytes { get; set; } = 8 * 1024 * 1024;
    /// <summary>Global cap on bytes held by in-flight captures. When reached, new requests are simply not sampled.</summary>
    public long MaxInFlightCaptureBytes { get; set; } = 256L * 1024 * 1024;
    /// <summary>Bodies above this size skip field-level diffing.</summary>
    public int MaxDiffBodyBytes { get; set; } = 16 * 1024 * 1024;
    public bool StoreBodies { get; set; } = true;
    /// <summary>When false (default), Authorization, Cookie, Set-Cookie and API-key headers are stored as [redacted].</summary>
    public bool StoreSensitiveHeaders { get; set; }

    public int LogQueueCapacity { get; set; } = 20_000;
    public long LogQueueMaxBytes { get; set; } = 128L * 1024 * 1024;
    public int LogBatchSize { get; set; } = 500;
    public int LogFlushIntervalMs { get; set; } = 1000;

    public int ConfigRefreshSeconds { get; set; } = 5;
    /// <summary>Optional file where the last good config snapshot is written; used on cold start if Postgres is down.</summary>
    public string? SnapshotFile { get; set; }

    public int MainConnectTimeoutMs { get; set; } = 10_000;
    public int MainActivityTimeoutSeconds { get; set; } = 100;
    public int DevMaxConnectionsPerServer { get; set; } = 512;

    /// <summary>Circuit breaker: failure ratio over the sampling window that opens the circuit.</summary>
    public double DevBreakerFailureRatio { get; set; } = 0.5;
    public int DevBreakerMinimumThroughput { get; set; } = 20;
    public int DevBreakerSamplingSeconds { get; set; } = 30;
    public int DevBreakerBreakSeconds { get; set; } = 30;

    public int HeartbeatSeconds { get; set; } = 10;
    public string InstanceId { get; set; } = Environment.GetEnvironmentVariable("HOSTNAME") ?? Environment.MachineName;
}
