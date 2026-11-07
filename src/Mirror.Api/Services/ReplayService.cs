using System.Diagnostics;
using System.Net;
using Mirror.Core.Compression;
using Mirror.Core.Diffing;
using Mirror.Core.Http;
using Mirror.Data;

namespace Mirror.Api.Services;

public sealed class ApiOptions
{
    public int ReplayMainTimeoutMs { get; set; } = 30_000;
    public int MaxCapturedResponseBytes { get; set; } = 8 * 1024 * 1024;
    public string ExportDir { get; set; } = "/data/exports";
    public int MaxConcurrentJobs { get; set; } = 4;
    public int MaintenanceIntervalMinutes { get; set; } = 10;
    public int PartitionsAhead { get; set; } = 3;
    public bool MigrateOnStartup { get; set; } = true;
    public string[] CorsOrigins { get; set; } = [];
    /// <summary>Public base URL of the proxy, shown in the UI as the mission's proxy endpoint.</summary>
    public string ProxyPublicUrl { get; set; } = "http://localhost:8080";
}

/// <summary>Sends one request to both Main and Dev (in parallel) for replays and fuzzing, and records the diff.</summary>
public sealed class ReplayService
{
    private readonly HttpClient _main;
    private readonly HttpClient _dev;
    private readonly ApiOptions _o;

    public ReplayService(Microsoft.Extensions.Options.IOptions<ApiOptions> options)
    {
        _o = options.Value;
        _main = Create(TimeSpan.FromMilliseconds(_o.ReplayMainTimeoutMs));
        _dev = Create(Timeout.InfiniteTimeSpan);
    }

    private static HttpClient Create(TimeSpan timeout) => new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxConnectionsPerServer = 128,
        ConnectTimeout = TimeSpan.FromSeconds(5),
    }) { Timeout = timeout };

    public async Task<(LogRecord Record, DiffResult? Diff)> SendBothAsync(
        MissionEntity mission, Mirror.Core.Rules.CompiledRuleSet rules, string method, string path, string? query,
        IReadOnlyList<KeyValuePair<string, string>> headers, byte[]? body, RecordOptions options, CancellationToken ct)
    {
        var policy = MissionMapping.Headers(mission);
        var strip = new HashSet<string>(policy.Strip ?? [], StringComparer.OrdinalIgnoreCase);
        var set = new Dictionary<string, string>(policy.Set ?? [], StringComparer.OrdinalIgnoreCase);
        var tag = options.Source == LogSource.Fuzz ? "X-Mirror-Fuzz" : "X-Mirror-Replay";

        var mainTask = SendAsync(_main, OutboundRequest.Build(method, mission.MainUrl, path, query, headers, body, extra: [new(tag, "1")]), null, ct);
        var devTask = SendAsync(_dev, OutboundRequest.Build(method, mission.DevUrl, path, query, headers, body, strip, set, [new(tag, "1")]),
            TimeSpan.FromMilliseconds(mission.DevTimeoutMs), ct);
        await Task.WhenAll(mainTask, devTask);

        var (main, mainOutcome, mainError, mainMs) = mainTask.Result;
        var (dev, outcome, devError, devMs) = devTask.Result;

        var mainSide = main is null
            ? new ExchangeSide(0, new Dictionary<string, string>(), [], mainMs)
            : new ExchangeSide(main.Status, main.Headers, main.Body, mainMs);
        var devSide = dev is null ? null : new ExchangeSide(dev.Status, dev.Headers, dev.Body, devMs);
        var opts = mainOutcome == DevOutcome.Ok ? options : options with { Note = $"Main failed: {mainError}" };
        var req = new ExchangeRequest(mission.Id, method, path, query, headers, body);
        var result = ExchangeRecorder.Record(req, mainSide, devSide, outcome, devError, rules, MissionMapping.DiffSettings(mission), opts);
        return result;
    }

    private async Task<(CapturedResponse? Resp, DevOutcome Outcome, string? Error, double Ms)> SendAsync(
        HttpClient client, HttpRequestMessage req, TimeSpan? timeout, CancellationToken ct)
    {
        using var _ = req;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        var sw = Stopwatch.StartNew();
        try
        {
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var captured = await OutboundRequest.ReadAsync(resp, _o.MaxCapturedResponseBytes, cts.Token);
            return (captured, DevOutcome.Ok, captured.Overflow ? "response exceeds capture limit" : null, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, DevOutcome.Timeout, $"no answer within {(timeout ?? client.Timeout).TotalMilliseconds:0} ms", sw.Elapsed.TotalMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            return (null, DevOutcome.Error, ex.Message, sw.Elapsed.TotalMilliseconds);
        }
    }

    /// <summary>Decompresses a stored request so it can be re-sent.</summary>
    public static (List<KeyValuePair<string, string>> Headers, byte[]? Body) StoredRequest(BodyRow? body)
    {
        var headers = LogRecord.UnpackHeaders(body?.RequestHeaders).ToList();
        var bytes = body?.RequestBody is { } b ? BodyCodec.Decompress(b) : null;
        return (headers, bytes);
    }
}
