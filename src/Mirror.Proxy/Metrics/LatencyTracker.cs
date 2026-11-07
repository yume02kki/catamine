namespace Mirror.Proxy.Metrics;

/// <summary>Lock-free fixed-bucket latency histogram used for the JSON snapshot (Prometheus keeps its own).</summary>
public sealed class LatencyTracker
{
    // Upper bounds in milliseconds.
    private static readonly double[] Bounds = [0.25, 0.5, 1, 2, 3, 5, 7.5, 10, 15, 25, 50, 75, 100, 250, 500, 1000, 2500, 5000, 10000, double.PositiveInfinity];
    private readonly long[] _counts = new long[Bounds.Length];
    private long _count;
    private long _sumMicros;

    public static readonly LatencyTracker Mirrored = new();
    public static readonly LatencyTracker Unmirrored = new();

    public void Observe(double ms)
    {
        var i = 0;
        while (ms > Bounds[i]) i++;
        Interlocked.Increment(ref _counts[i]);
        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _sumMicros, (long)(ms * 1000));
    }

    public object Snapshot()
    {
        var count = Interlocked.Read(ref _count);
        if (count == 0) return new { count = 0L, avgMs = 0.0, p50Ms = 0.0, p99Ms = 0.0 };
        double Q(double q)
        {
            var target = q * count;
            long cumulative = 0;
            for (var i = 0; i < Bounds.Length; i++)
            {
                cumulative += Interlocked.Read(ref _counts[i]);
                if (cumulative >= target) return double.IsPositiveInfinity(Bounds[i]) ? Bounds[i - 1] : Bounds[i];
            }
            return Bounds[^2];
        }
        return new { count, avgMs = Interlocked.Read(ref _sumMicros) / 1000.0 / count, p50Ms = Q(0.5), p99Ms = Q(0.99) };
    }
}
