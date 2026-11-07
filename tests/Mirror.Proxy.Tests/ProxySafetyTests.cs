using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mirror.Core.Models;
using Mirror.Proxy;
using Mirror.Proxy.Capture;
using Mirror.Proxy.Config;
using Mirror.Proxy.Metrics;
using Mirror.Proxy.Mirroring;

namespace Mirror.Proxy.Tests;

/// <summary>A real Kestrel upstream on a random port.</summary>
internal sealed class Upstream : IAsyncDisposable
{
    private readonly WebApplication _app;
    public string Url { get; }
    public int Hits;

    private Upstream(WebApplication app, string url) { _app = app; Url = url; }

    public static async Task<Upstream> StartAsync(Func<HttpContext, Task> handler)
    {
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        var app = b.Build();
        Upstream? self = null;
        app.Run(async ctx => { Interlocked.Increment(ref self!.Hits); await handler(ctx); });
        await app.StartAsync();
        var url = app.Urls.First();
        self = new Upstream(app, url);
        return self;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}

public sealed class ProxyFactory(string snapshotFile, Dictionary<string, string?> extra) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Postgres points at a closed port: every DB call fails fast.
        builder.UseSetting("ConnectionStrings:Postgres", "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1");
        builder.UseSetting("Mirror:SnapshotFile", snapshotFile);
        foreach (var (k, v) in extra) builder.UseSetting(k, v);
    }
}

public class ProxySafetyTests
{
    private static string WriteSnapshot(params MissionConfig[] missions)
    {
        var path = Path.Combine(Path.GetTempPath(), $"mirror-snapshot-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(missions, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return path;
    }

    private static MissionConfig Mission(string main, string dev, Action<MissionConfigBuilder>? configure = null)
    {
        var b = new MissionConfigBuilder { Main = main, Dev = dev };
        configure?.Invoke(b);
        return new MissionConfig
        {
            Id = Guid.NewGuid(), Slug = "orders", Name = "Orders", MainUrl = main, DevUrl = dev,
            SampleRate = 1, MirrorMethods = b.Methods, MaxMirrorBodyBytes = b.MaxBody, DevTimeoutMs = b.DevTimeoutMs,
        };
    }

    public sealed class MissionConfigBuilder
    {
        public string Main = "", Dev = "";
        public string[] Methods = ["GET", "HEAD", "POST"];
        public int MaxBody = 1024;
        public int DevTimeoutMs = 2000;
    }

    private static async Task<HttpClient> ReadyClient(ProxyFactory f)
    {
        var c = f.CreateClient();
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            var r = await c.GetAsync("/readyz");
            if (r.StatusCode == HttpStatusCode.OK) return c;
            await Task.Delay(100);
        }
        throw new TimeoutException("proxy never became ready");
    }

    [Fact]
    public async Task Forwards_unchanged_when_dev_and_postgres_are_down()
    {
        await using var main = await Upstream.StartAsync(async ctx =>
        {
            ctx.Response.StatusCode = 201;
            ctx.Response.Headers["X-Main"] = "yes";
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($$"""{"path":"{{ctx.Request.Path}}","query":"{{ctx.Request.QueryString}}"}""");
        });
        var snapshot = WriteSnapshot(Mission(main.Url, "http://127.0.0.1:1"));
        await using var f = new ProxyFactory(snapshot, []);
        var client = await ReadyClient(f);

        for (var i = 0; i < 50; i++)
        {
            var resp = await client.GetAsync("/m/orders/v1/orders/42?x=1");
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            Assert.Equal("yes", resp.Headers.GetValues("X-Main").Single());
            Assert.Equal("""{"path":"/v1/orders/42","query":"?x=1"}""", await resp.Content.ReadAsStringAsync());
        }
        Assert.Equal(50, main.Hits);

        // Mirroring was attempted (Dev refused) and logs were dropped, yet every main request succeeded.
        await WaitUntil(() => ProxyMetrics.DevRequests.WithLabels("orders", "error").Value >= 1);
    }

    [Fact]
    public async Task Slow_dev_does_not_slow_main()
    {
        await using var main = await Upstream.StartAsync(ctx => ctx.Response.WriteAsync("{}"));
        await using var dev = await Upstream.StartAsync(async ctx => { await Task.Delay(1500); await ctx.Response.WriteAsync("{}"); });
        var snapshot = WriteSnapshot(Mission(main.Url, dev.Url, b => b.DevTimeoutMs = 500));
        await using var f = new ProxyFactory(snapshot, []);
        var client = await ReadyClient(f);

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) Assert.True((await client.GetAsync("/m/orders/x")).IsSuccessStatusCode);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 1500, $"20 requests took {sw.ElapsedMilliseconds} ms although Dev takes 1.5 s each");
    }

    [Fact]
    public async Task Request_bodies_over_the_limit_are_forwarded_in_full_and_not_mirrored()
    {
        var received = 0;
        await using var main = await Upstream.StartAsync(async ctx =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            Interlocked.Exchange(ref received, (int)ms.Length);
            await ctx.Response.WriteAsync("{}");
        });
        await using var dev = await Upstream.StartAsync(ctx => ctx.Response.WriteAsync("{}"));
        var snapshot = WriteSnapshot(Mission(main.Url, dev.Url, b => b.MaxBody = 1000));
        await using var f = new ProxyFactory(snapshot, []);
        var client = await ReadyClient(f);

        var before = ProxyMetrics.NotMirrored.WithLabels("request_too_large").Value;
        var body = new string('x', 50_000);
        var resp = await client.PostAsync("/m/orders/upload", new StringContent(body, Encoding.UTF8, "text/plain"));
        Assert.True(resp.IsSuccessStatusCode);
        Assert.Equal(50_000, received);
        Assert.True(ProxyMetrics.NotMirrored.WithLabels("request_too_large").Value > before);

        // Chunked (unknown length) bodies over the limit are detected while streaming.
        var chunked = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(body)));
        resp = await client.PostAsync("/m/orders/upload", chunked);
        Assert.True(resp.IsSuccessStatusCode);
        Assert.Equal(50_000, received);
        await Task.Delay(200);
        Assert.Equal(0, dev.Hits);
    }

    [Fact]
    public async Task Small_post_bodies_are_mirrored_with_headers_stripped_and_overridden()
    {
        string? devAuth = "unset", devBody = null, devEnv = null;
        await using var main = await Upstream.StartAsync(ctx => ctx.Response.WriteAsync("{}"));
        await using var dev = await Upstream.StartAsync(async ctx =>
        {
            devAuth = ctx.Request.Headers.Authorization.FirstOrDefault();
            devEnv = ctx.Request.Headers["X-Env"].FirstOrDefault();
            devBody = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            await ctx.Response.WriteAsync("{}");
        });
        var m = Mission(main.Url, dev.Url) with { Headers = new HeaderPolicy { Strip = ["authorization"], Set = new() { ["X-Env"] = "dev" } } };
        var snapshot = WriteSnapshot(m);
        await using var f = new ProxyFactory(snapshot, []);
        var client = await ReadyClient(f);

        var req = new HttpRequestMessage(HttpMethod.Post, "/m/orders/v1/echo") { Content = new StringContent("""{"a":1}""", Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "prod-secret");
        Assert.True((await client.SendAsync(req)).IsSuccessStatusCode);
        await WaitUntil(() => devBody is not null);
        Assert.Equal("""{"a":1}""", devBody);
        Assert.Null(devAuth);
        Assert.Equal("dev", devEnv);
    }

    [Fact]
    public async Task Methods_not_enabled_are_never_mirrored()
    {
        await using var main = await Upstream.StartAsync(ctx => ctx.Response.WriteAsync("{}"));
        await using var dev = await Upstream.StartAsync(ctx => ctx.Response.WriteAsync("{}"));
        var snapshot = WriteSnapshot(Mission(main.Url, dev.Url, b => b.Methods = ["GET"]));
        await using var f = new ProxyFactory(snapshot, []);
        var client = await ReadyClient(f);
        Assert.True((await client.DeleteAsync("/m/orders/v1/orders/1")).IsSuccessStatusCode);
        Assert.True((await client.PutAsync("/m/orders/v1/orders/1", new StringContent("{}"))).IsSuccessStatusCode);
        await Task.Delay(300);
        Assert.Equal(0, dev.Hits);
        Assert.Equal(2, main.Hits);
    }

    [Fact]
    public async Task Unknown_mission_is_404()
    {
        var snapshot = WriteSnapshot();
        await using var f = new ProxyFactory(snapshot, []);
        var client = await ReadyClient(f);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/m/missing/x")).StatusCode);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException();
            await Task.Delay(50);
        }
    }
}

public class QueueAndCaptureTests
{
    private static IOptions<ProxyOptions> Opts(int capacity) => Options.Create(new ProxyOptions { QueueCapacity = capacity });

    private static MirrorJob Job(CaptureBudget budget) => new()
    {
        Mission = new MissionRuntime(new MissionConfig { Id = Guid.NewGuid(), Slug = "s", Name = "n", MainUrl = "http://a", DevUrl = "http://b" },
            Mirror.Core.Rules.CompiledRuleSet.Empty, 1024),
        Id = Guid.NewGuid(), Ts = DateTime.UtcNow, Method = "GET", Path = "/", RequestHeaders = [], MainStatus = 200, MainHeaders = [],
        MainBody = new CaptureBuffer(budget, 1 << 20), MainMs = 1,
    };

    [Fact]
    public void Full_queue_drops_instead_of_blocking_and_releases_buffers()
    {
        var budget = new CaptureBudget(1 << 30);
        var q = new MirrorQueue(Opts(2));
        var sw = Stopwatch.StartNew();
        var accepted = 0;
        for (var i = 0; i < 10_000; i++)
        {
            var job = Job(budget);
            job.MainBody.Append(new byte[100]);
            if (q.TryEnqueue(job)) accepted++;
        }
        sw.Stop();
        Assert.Equal(2, accepted);
        Assert.Equal(2, q.Count);
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Assert.Equal(2 * 16 * 1024, budget.Used); // only the two queued jobs still hold a segment
    }

    [Fact]
    public void Capture_stops_at_limit_and_releases_memory()
    {
        var budget = new CaptureBudget(1 << 20);
        using var c = new CaptureBuffer(budget, 100);
        c.Append(new byte[60]);
        Assert.Equal(CaptureState.Capturing, c.State);
        c.Append(new byte[60]);
        Assert.Equal(CaptureState.Overflow, c.State);
        Assert.Equal(0, budget.Used);
        c.Append(new byte[10]); // ignored
        Assert.Empty(c.ToArray());
    }

    [Fact]
    public void Capture_respects_global_budget()
    {
        var budget = new CaptureBudget(32 * 1024);
        using var a = new CaptureBuffer(budget, 1 << 20);
        using var b = new CaptureBuffer(budget, 1 << 20);
        a.Append(new byte[20_000]);
        Assert.Equal(CaptureState.Capturing, a.State);
        b.Append(new byte[20_000]);
        Assert.Equal(CaptureState.BudgetExceeded, b.State);
        Assert.Equal(32 * 1024, budget.Used);
        a.Dispose();
        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public void Capture_reassembles_segments()
    {
        var budget = new CaptureBudget(1 << 20);
        using var c = new CaptureBuffer(budget, 1 << 20);
        var data = Enumerable.Range(0, 50_000).Select(i => (byte)i).ToArray();
        foreach (var chunk in data.Chunk(777)) c.Append(chunk);
        Assert.Equal(data, c.ToArray());
    }
}
