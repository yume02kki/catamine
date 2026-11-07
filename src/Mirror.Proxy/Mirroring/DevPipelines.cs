using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Options;
using Mirror.Proxy.Config;
using Mirror.Proxy.Metrics;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Mirror.Proxy.Mirroring;

/// <summary>Per-mission resilience pipeline for Dev: concurrency limiter → circuit breaker → timeout.</summary>
public sealed class DevPipelines(IOptions<ProxyOptions> options)
{
    private sealed record Entry(string Key, ResiliencePipeline Pipeline, CircuitBreakerStateProvider State);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();
    private readonly ProxyOptions _o = options.Value;

    public ResiliencePipeline Get(MissionRuntime mission)
    {
        var key = mission.Config.PipelineKey;
        if (_entries.TryGetValue(mission.Config.Id, out var e) && e.Key == key) return e.Pipeline;
        var created = Create(mission, key);
        _entries[mission.Config.Id] = created;
        return created.Pipeline;
    }

    private Entry Create(MissionRuntime mission, string key)
    {
        var state = new CircuitBreakerStateProvider();
        var pipeline = new ResiliencePipelineBuilder()
            .AddConcurrencyLimiter(Math.Max(1, mission.Config.DevMaxConcurrency), queueLimit: 0)
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = _o.DevBreakerFailureRatio,
                MinimumThroughput = Math.Max(2, _o.DevBreakerMinimumThroughput),
                SamplingDuration = TimeSpan.FromSeconds(Math.Max(1, _o.DevBreakerSamplingSeconds)),
                BreakDuration = TimeSpan.FromSeconds(Math.Max(1, _o.DevBreakerBreakSeconds)),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<TimeoutRejectedException>()
                    .Handle<IOException>(),
                StateProvider = state,
            })
            .AddTimeout(TimeSpan.FromMilliseconds(Math.Max(10, mission.Config.DevTimeoutMs)))
            .Build();
        return new Entry(key, pipeline, state);
    }

    public void PublishMetrics(IEnumerable<MissionRuntime> missions)
    {
        foreach (var m in missions)
            if (_entries.TryGetValue(m.Config.Id, out var e))
                ProxyMetrics.CircuitState.WithLabels(m.Config.Slug).Set((int)e.State.CircuitState);
    }

    public IReadOnlyDictionary<string, string> CircuitStates(IEnumerable<MissionRuntime> missions) =>
        missions.Where(m => _entries.ContainsKey(m.Config.Id))
            .ToDictionary(m => m.Config.Slug, m => _entries[m.Config.Id].State.CircuitState.ToString().ToLowerInvariant());

    public static HttpClient CreateDevClient(ProxyOptions o) => new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        MaxConnectionsPerServer = Math.Max(1, o.DevMaxConnectionsPerServer),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        ConnectTimeout = TimeSpan.FromSeconds(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan, // the Polly timeout governs
        DefaultRequestVersion = HttpVersion.Version11,
    };
}
