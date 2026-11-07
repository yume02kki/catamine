using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mirror.Mcp.Features;
using Mirror.Mcp.Protocol;

namespace Mirror.Mcp.Tests;

/// <summary>A small stand-in for the KittyParty api with canned data; records what the MCP server sends.</summary>
public sealed class FakeApi : IAsyncDisposable
{
    public const string EchoId = "11111111-0000-0000-0000-000000000001";
    public const string PokeId = "11111111-0000-0000-0000-000000000002";
    public const string LogId = "01a1b728-d8c6-72c5-81c4-3fe2c070e4c6";

    private readonly WebApplication _app;
    public string Url { get; }
    public ConcurrentQueue<(string Method, string Path, JsonNode? Body)> Calls { get; } = new();
    public JsonArray Rules { get; } = new(new JsonObject { ["id"] = "r1", ["order"] = 0, ["type"] = "IgnoreField", ["name"] = null, ["enabled"] = true, ["config"] = new JsonObject { ["paths"] = new JsonArray("$..requestId") } });
    public TimeSpan Delay { get; set; }

    private FakeApi(WebApplication app, string url) { _app = app; Url = url; }

    public static async Task<FakeApi> StartAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        var app = b.Build();
        FakeApi? self = null;

        JsonObject Mission(string id, string slug, string name, string main, string dev, string[] methods) => new()
        {
            ["id"] = id, ["slug"] = slug, ["name"] = name, ["description"] = null, ["mainUrl"] = main, ["devUrl"] = dev, ["proxyPath"] = $"/m/{slug}",
            ["enabled"] = true, ["mirrorEnabled"] = true, ["sampleRate"] = 1.0, ["mirrorMethods"] = new JsonArray(methods.Select(x => (JsonNode)x).ToArray()),
            ["maxMirrorBodyBytes"] = 262144, ["devTimeoutMs"] = 2000, ["devMaxConcurrency"] = 64,
            ["headerPolicy"] = new JsonObject { ["strip"] = new JsonArray("authorization"), ["set"] = new JsonObject() },
            ["comparedHeaders"] = new JsonArray("content-type"), ["rulesVersion"] = 3, ["retentionDays"] = 14, ["maxStorageMb"] = null,
            ["createdAt"] = "2026-11-07T00:00:00Z", ["updatedAt"] = "2026-11-07T00:00:00Z",
            ["warnings"] = methods.Contains("POST") ? new JsonArray("Mirroring POST sends real write requests to Dev.") : new JsonArray(), ["ruleCount"] = 1,
        };
        var missions = new JsonArray(
            Mission(EchoId, "echo-apis", "Echo APIs", "https://httpbin.org", "https://postman-echo.com", ["GET", "POST"]),
            Mission(PokeId, "pok-api", "PokéAPI", "https://pokeapi.co/api/v2", "https://pokeapi.co/api/v2", ["GET"]));

        app.Use(async (ctx, next) =>
        {
            JsonNode? body = null;
            if (ctx.Request.ContentLength > 0) body = await JsonNode.ParseAsync(ctx.Request.Body);
            self!.Calls.Enqueue((ctx.Request.Method, ctx.Request.Path + ctx.Request.QueryString, body));
            ctx.Items["body"] = body;
            if (self.Delay > TimeSpan.Zero) await Task.Delay(self.Delay, ctx.RequestAborted);
            await next();
        });
        IResult J(JsonNode n) => Results.Content(n.ToJsonString(), "application/json");

        app.MapGet("/api/config", () => J(new JsonObject { ["proxyPublicUrl"] = "http://proxy.test", ["version"] = "2.0.0" }));
        app.MapGet("/api/missions", () => J(missions));
        app.MapGet("/api/missions/overview", () => J(new JsonArray(new JsonObject
        {
            ["missionId"] = EchoId, ["total"] = 14, ["match"] = 11, ["mismatch"] = 3, ["devError"] = 0, ["devTimeout"] = 0, ["notMirrored"] = 0, ["diffRate"] = 3 / 14.0,
        })));
        app.MapGet("/api/missions/{id}", (string id) => missions.FirstOrDefault(m => (string)m!["id"]! == id) is { } m ? J(m) : Results.NotFound());
        app.MapGet("/api/missions/{id}/rules", () => J(self!.Rules));
        app.MapPut("/api/missions/{id}/rules", (HttpContext ctx) =>
        {
            var body = (JsonObject)ctx.Items["body"]!;
            return J(new JsonObject { ["rulesVersion"] = 4, ["rules"] = body["rules"]!.DeepClone(), ["job"] = (bool)body["rediff"]! ? new JsonObject { ["id"] = "22222222-0000-0000-0000-000000000009" } : null });
        });
        app.MapPost("/api/rules/validate", (HttpContext ctx) =>
        {
            var body = (JsonObject)ctx.Items["body"]!;
            var ok = (string?)body["type"] != "IgnoreField" || body["config"]?["paths"] is JsonArray { Count: > 0 };
            return J(new JsonObject { ["valid"] = ok, ["errors"] = ok ? new JsonArray() : new JsonArray("at least one path is required") });
        });
        app.MapPost("/api/missions/{id}/rules/preview", () => J(new JsonObject
        {
            ["sampled"] = 14, ["mismatchesBefore"] = 14, ["mismatchesAfter"] = 3, ["nowMatching"] = 11, ["nowMismatching"] = 0, ["ruleErrors"] = new JsonArray(), ["examples"] = new JsonArray(),
        }));
        app.MapGet("/api/missions/{id}/stats", () => J(new JsonObject
        {
            ["total"] = 14, ["matchRate"] = 11 / 14.0,
            ["verdicts"] = new JsonObject
            {
                ["match"] = new JsonObject { ["count"] = 11, ["mainMsAvg"] = 100.0, ["devMsAvg"] = 150.0 },
                ["mismatch"] = new JsonObject { ["count"] = 3, ["mainMsAvg"] = 100.0, ["devMsAvg"] = 200.0 },
            },
        }));
        app.MapGet("/api/missions/{id}/clusters", () => J(new JsonArray(new JsonObject
        {
            ["signature"] = "42", ["count"] = 3, ["example"] = new JsonObject { ["id"] = LogId, ["method"] = "GET", ["path"] = "/get" },
            ["summary"] = new JsonObject { ["top"] = new JsonArray(new JsonObject { ["k"] = "removed", ["p"] = "$.method", ["m"] = "\"GET\"", ["d"] = null }) },
        })));
        app.MapGet("/api/missions/{id}/logs", () => J(new JsonObject
        {
            ["items"] = new JsonArray(new JsonObject
            {
                ["id"] = LogId, ["ts"] = "2026-11-07T18:00:00.000Z", ["method"] = "GET", ["path"] = "/get", ["query"] = "q=1", ["mainStatus"] = 200, ["devStatus"] = 200,
                ["mainMs"] = 120.0, ["devMs"] = 180.0, ["verdict"] = "mismatch", ["diffKinds"] = new JsonArray("removed", "status"), ["diffCount"] = 1, ["note"] = null,
                ["summary"] = new JsonObject { ["changed"] = 0, ["added"] = 0, ["removed"] = 1, ["typeChanged"] = 0 },
            }),
            ["nextCursor"] = "abc",
        }));
        app.MapGet("/api/missions/{id}/logs/{logId}/detail", () => J(new JsonObject
        {
            ["log"] = new JsonObject { ["id"] = LogId, ["ts"] = "2026-11-07T18:00:00Z", ["method"] = "GET", ["path"] = "/get", ["query"] = null, ["mainStatus"] = 200, ["devStatus"] = 200, ["mainMs"] = 120.0, ["devMs"] = 180.0, ["verdict"] = "mismatch", ["diffCount"] = 1, ["rulesVersion"] = 3, ["source"] = "live", ["note"] = null },
            ["main"] = new JsonObject { ["bodyBytes"] = 300 }, ["dev"] = new JsonObject { ["bodyBytes"] = 280 },
            ["differences"] = new JsonArray(new JsonObject { ["k"] = "removed", ["p"] = "$.method", ["m"] = "\"GET\"", ["d"] = null }),
        }));
        app.MapGet("/api/missions/{id}/logs/{logId}/body/{side}", (string side) => Results.Text(side == "main" ? "{\"method\":\"GET\",\"args\":{}}" : "{\"args\":{}}", "application/json"));
        app.MapPost("/api/missions/{id}/jobs/{type}", (string type) => J(new JsonObject { ["id"] = "33333333-0000-0000-0000-000000000003", ["type"] = type, ["status"] = "queued" }));
        app.MapDelete("/api/missions/{id}", () => Results.NoContent());
        app.MapPost("/api/missions", () => J(Mission("44444444-0000-0000-0000-000000000004", "new-one", "New one", "https://a.test", "https://b.test", ["GET"])));

        await app.StartAsync();
        self = new FakeApi(app, app.Urls.First());
        return self;
    }

    public McpServer CreateServer(bool readOnly = false)
    {
        var options = new McpOptions { ApiUrl = Url, ReadOnly = readOnly };
        var api = new KittyApi(new HttpClient(), options);
        var tools = MirrorTools.Create(api).ToList();
        return new McpServer(new ToolRegistry(tools, options), new ResourceProvider(api, tools), new PromptProvider(), options, NullLogger<McpServer>.Instance);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
