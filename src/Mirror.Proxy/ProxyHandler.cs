using System.Diagnostics;
using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Mirror.Core.Diffing;
using Mirror.Data;
using Mirror.Proxy.Capture;
using Mirror.Proxy.Config;
using Mirror.Proxy.Metrics;
using Mirror.Proxy.Mirroring;
using Yarp.ReverseProxy.Forwarder;

namespace Mirror.Proxy;

/// <summary>
/// The critical path. Forwards <c>/m/{slug}/{**path}</c> to the mission's Main URL with YARP and streams the response back.
/// Mirroring adds only: a sampling decision, two tee stream wrappers, and a non-blocking enqueue after the response completed.
/// </summary>
public sealed class ProxyHandler
{
    private readonly IHttpForwarder _forwarder;
    private readonly MissionConfigStore _store;
    private readonly MirrorQueue _queue;
    private readonly LogQueue _logQueue;
    private readonly CaptureBudget _budget;
    private readonly HttpMessageInvoker _main;
    private readonly ProxyOptions _o;
    private readonly ForwarderRequestConfig _requestConfig;

    public ProxyHandler(IHttpForwarder forwarder, MissionConfigStore store, MirrorQueue queue, LogQueue logQueue,
        CaptureBudget budget, [FromKeyedServices("main")] HttpMessageInvoker main, IOptions<ProxyOptions> options)
    {
        _forwarder = forwarder;
        _store = store;
        _queue = queue;
        _logQueue = logQueue;
        _budget = budget;
        _main = main;
        _o = options.Value;
        _requestConfig = new ForwarderRequestConfig
        {
            ActivityTimeout = TimeSpan.FromSeconds(_o.MainActivityTimeoutSeconds),
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        // Under app.Map("/m"): Path = "/{slug}/rest..."
        var fullPath = ctx.Request.Path.Value ?? "/";
        var slash = fullPath.IndexOf('/', 1);
        var slug = slash < 0 ? fullPath[1..] : fullPath[1..slash];
        if (slug.Length == 0 || !_store.TryGet(slug, out var mission) || !mission.Config.Enabled)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("""{"error":"unknown or disabled mission"}""");
            return;
        }

        ctx.Request.PathBase = ctx.Request.PathBase.Add("/" + slug);
        ctx.Request.Path = slash < 0 ? "/" : new PathString(fullPath[slash..]);

        var m = mission.Config;
        var method = ctx.Request.Method;
        var sampled = m.MirrorEnabled && m.SampleRate > 0 && mission.MirrorMethods.Contains(method)
                      && (m.SampleRate >= 1 || Random.Shared.NextDouble() < m.SampleRate)
                      && !ctx.WebSockets.IsWebSocketRequest;

        CaptureBuffer? reqCap = null, respCap = null;
        Stream? origReq = null, origResp = null;
        string? skipReason = null;
        var contentLength = ctx.Request.ContentLength;
        var arrived = DateTimeOffset.UtcNow;

        if (sampled)
        {
            if (_budget.Exhausted)
            {
                ProxyMetrics.Dropped.WithLabels("memory").Inc();
                sampled = false;
            }
            else if (contentLength > m.MaxMirrorBodyBytes)
            {
                skipReason = "request_too_large";
                sampled = false;
            }
            else
            {
                if (ctx.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
                {
                    reqCap = new CaptureBuffer(_budget, m.MaxMirrorBodyBytes);
                    origReq = ctx.Request.Body;
                    ctx.Request.Body = new TeeReadStream(origReq, reqCap);
                }
                respCap = new CaptureBuffer(_budget, _o.MaxCapturedResponseBytes);
                origResp = ctx.Response.Body;
                ctx.Response.Body = new TeeWriteStream(origResp, respCap);
            }
        }

        var start = Stopwatch.GetTimestamp();
        var error = await _forwarder.SendAsync(ctx, mission.MainPrefix, _main, _requestConfig, HttpTransformer.Default);
        var mainMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        (respCap is not null ? LatencyTracker.Mirrored : LatencyTracker.Unmirrored).Observe(mainMs);
        ProxyMetrics.MainDuration.WithLabels(respCap is not null ? "true" : "false").Observe(mainMs / 1000);
        ProxyMetrics.MainRequests.WithLabels(error == ForwarderError.None ? "ok" : "error").Inc();

        if (respCap is null)
        {
            if (skipReason is not null) LogNotMirrored(mission, ctx, arrived, mainMs, skipReason, $"request body exceeds {m.MaxMirrorBodyBytes / 1024} KB mirror limit");
            return;
        }

        ctx.Response.Body = origResp!;
        if (origReq is not null) ctx.Request.Body = origReq;

        var reason = error != ForwarderError.None ? "main_error"
            : reqCap is { State: CaptureState.BudgetExceeded } || respCap.State == CaptureState.BudgetExceeded ? "memory"
            : reqCap is { State: CaptureState.Overflow } ? "request_too_large"
            : reqCap is not null && !(reqCap.Completed || (contentLength is { } cl && reqCap.Length == cl)) ? "request_incomplete"
            : respCap.State == CaptureState.Overflow ? "response_too_large"
            : null;

        if (reason is not null)
        {
            reqCap?.Dispose();
            respCap.Dispose();
            switch (reason)
            {
                case "memory": ProxyMetrics.Dropped.WithLabels("memory").Inc(); break;
                case "main_error": ProxyMetrics.NotMirrored.WithLabels("main_error").Inc(); break;
                default:
                    LogNotMirrored(mission, ctx, arrived, mainMs, reason, reason switch
                    {
                        "request_too_large" => $"request body exceeds {m.MaxMirrorBodyBytes / 1024} KB mirror limit",
                        "response_too_large" => $"Main response exceeds {_o.MaxCapturedResponseBytes / 1024} KB capture limit",
                        _ => "request body was not fully read by Main",
                    });
                    break;
            }
            return;
        }

        var (id, ts) = LogRecord.NewId(arrived);
        var job = new MirrorJob
        {
            Mission = mission,
            Id = id,
            Ts = ts,
            Method = method,
            Path = ctx.Request.Path.ToUriComponent(),
            Query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value![1..] : null,
            RequestHeaders = Snapshot(ctx.Request.Headers),
            RequestBody = reqCap,
            MainStatus = ctx.Response.StatusCode,
            MainHeaders = Snapshot(ctx.Response.Headers),
            MainBody = respCap,
            MainMs = mainMs,
        };

        // Finish the client's response first; only then hand the job to the workers (never waits).
        try { await ctx.Response.CompleteAsync(); }
        catch (Exception) { job.Dispose(); return; }
        _queue.TryEnqueue(job);
    }

    private void LogNotMirrored(MissionRuntime mission, HttpContext ctx, DateTimeOffset arrived, double mainMs, string reason, string note)
    {
        ProxyMetrics.NotMirrored.WithLabels(reason).Inc();
        var (id, ts) = LogRecord.NewId(arrived);
        _logQueue.TryEnqueue(new LogRecord
        {
            Id = id,
            MissionId = mission.Config.Id,
            Ts = ts,
            Method = ctx.Request.Method,
            Path = ctx.Request.Path.ToUriComponent(),
            Query = ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value![1..] : null,
            MainStatus = (short)ctx.Response.StatusCode,
            MainMs = (float)mainMs,
            Verdict = Verdict.NotMirrored,
            RulesVersion = mission.Config.RulesVersion,
            Source = LogSource.Live,
            Note = note,
        });
    }

    private static KeyValuePair<string, string>[] Snapshot(IHeaderDictionary headers)
    {
        var arr = new KeyValuePair<string, string>[headers.Count];
        var i = 0;
        foreach (var h in headers) arr[i++] = new(h.Key, h.Value.ToString());
        return arr;
    }
}
