// Deterministic JSON upstream for demos, tests and load tests.
// VARIANT=main serves the reference behaviour; VARIANT=dev injects realistic differences.
// Env: VARIANT, DELAY_MS (base latency), JITTER_MS, ERROR_RATE (0..1, dev only), SLOW_RATE (0..1), SLOW_MS.
using System.Text.Json.Nodes;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var app = builder.Build();

var variant = Environment.GetEnvironmentVariable("VARIANT") ?? "main";
var isDev = variant.Equals("dev", StringComparison.OrdinalIgnoreCase);
int Env(string n, int d) => int.TryParse(Environment.GetEnvironmentVariable(n), out var v) ? v : d;
double EnvD(string n, double d) => double.TryParse(Environment.GetEnvironmentVariable(n), System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : d;
var delayMs = Env("DELAY_MS", 0);
var jitterMs = Env("JITTER_MS", 0);
var errorRate = EnvD("ERROR_RATE", 0);
var slowRate = EnvD("SLOW_RATE", 0);
var slowMs = Env("SLOW_MS", 3000);

async Task Latency()
{
    var d = delayMs + (jitterMs > 0 ? Random.Shared.Next(jitterMs) : 0);
    if (slowRate > 0 && Random.Shared.NextDouble() < slowRate) d += slowMs;
    if (d > 0) await Task.Delay(d);
}

static int Hash(string s) { unchecked { var h = 17; foreach (var c in s) h = h * 31 + c; return h & 0x7fffffff; } }

app.MapGet("/health", () => Results.Ok(new { status = "ok", variant }));

app.MapGet("/v1/search", async (string? q, int? page, string? sort) =>
{
    await Latency();
    if (isDev && errorRate > 0 && Random.Shared.NextDouble() < errorRate)
        return Results.Content("<html><body>502 Bad Gateway</body></html>", "text/html", statusCode: 502);

    q ??= "all";
    var p = Math.Max(1, page ?? 1);
    var seed = Hash(q) + p;
    var items = new JsonArray();
    for (var i = 0; i < 20; i++)
    {
        var n = seed + i;
        var sku = $"SKU-{n % 100000:D5}";
        var price = Math.Round(5 + n % 200 + (n % 100) / 100.0, 2);
        if (isDev && i == 3) price = Math.Round(price, 1);            // rounding difference
        if (isDev && i == 7 && p % 2 == 0) continue;                    // item missing on even pages
        var item = new JsonObject
        {
            ["sku"] = sku,
            ["title"] = $"{q} item {i}",
            ["price"] = new JsonObject { ["amount"] = price, ["currency"] = "EUR" },
            ["stock"] = n % 37,
            ["tags"] = new JsonArray("new", n % 2 == 0 ? "sale" : "regular"),
        };
        if (isDev) item["badge"] = n % 5 == 0 ? "hot" : null;              // new field on Dev
        items.Add(item);
    }
    if (isDev && sort == "price")
    {
        var list = items.Select(x => x!.DeepClone()).Reverse().ToList();   // order difference
        items = new JsonArray(list.ToArray());
    }
    var total = 1000 + seed % 1000;
    var body = new JsonObject
    {
        ["requestId"] = Guid.NewGuid().ToString(),
        ["took_ms"] = Random.Shared.Next(1, 40),
        ["query"] = q,
        ["page"] = p,
        ["total"] = isDev && p == 1 ? JsonValue.Create(total.ToString()) : JsonValue.Create(total), // type change on page 1
        ["items"] = items,
        ["host"] = isDev ? "search-dev.internal" : "search.internal",
    };
    return Results.Content(body.ToJsonString(), "application/json");
});

app.MapGet("/v1/items/{id}", async (string id) =>
{
    await Latency();
    var n = Hash(id);
    return Results.Json(new { id, name = $"Item {id}", price = n % 100 + 0.99, updatedAt = DateTime.UtcNow });
});

app.MapGet("/v1/big", async (int? kb) =>
{
    await Latency();
    var target = Math.Clamp(kb ?? 512, 1, 64 * 1024) * 1024;
    var arr = new JsonArray();
    var size = 0;
    for (var i = 0; size < target; i++)
    {
        var o = new JsonObject { ["id"] = i, ["name"] = $"row {i}", ["value"] = isDev && i % 1000 == 999 ? i + 1 : i };
        size += 40;
        arr.Add(o);
    }
    return Results.Content(new JsonObject { ["rows"] = arr }.ToJsonString(), "application/json");
});

app.MapPost("/v1/echo", async (HttpRequest req) =>
{
    await Latency();
    using var reader = new StreamReader(req.Body);
    var text = await reader.ReadToEndAsync();
    JsonNode? body;
    try { body = JsonNode.Parse(text); } catch { body = text; }
    return Results.Json(new { received = body, length = text.Length, variant = isDev ? "dev" : "main" });
});

app.Run();
