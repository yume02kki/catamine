using System.Text.Json.Nodes;
using Mirror.Mcp.Features;
using Mirror.Mcp.Protocol;

namespace Mirror.Mcp.Tests;

public class McpServerTests : IAsyncLifetime
{
    private FakeApi _api = null!;
    private McpServer _server = null!;

    public async Task InitializeAsync()
    {
        _api = await FakeApi.StartAsync();
        _server = _api.CreateServer();
    }

    public async Task DisposeAsync() => await _api.DisposeAsync();

    private static JsonObject Req(int id, string method, JsonObject? @params = null) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params ?? new JsonObject() };

    private async Task<McpSession> InitAsync(string version = "2025-06-18")
    {
        var s = new McpSession();
        await _server.HandleAsync(Req(0, "initialize", new JsonObject { ["protocolVersion"] = version, ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" } }), s, default);
        return s;
    }

    private async Task<JsonObject> CallAsync(McpSession s, string tool, JsonObject? args = null)
    {
        var r = (JsonObject)(await _server.HandleAsync(Req(9, "tools/call", new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() }), s, default))!;
        Assert.True(r["error"] is null, r["error"]?.ToJsonString());
        return (JsonObject)r["result"]!;
    }

    private static string Text(JsonObject result) => (string)result["content"]![0]!["text"]!;

    [Theory]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2025-03-26", "2025-03-26")]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("1999-01-01", "2025-06-18")]
    public async Task Initialize_negotiates_the_protocol_version(string requested, string expected)
    {
        var r = (JsonObject)(await _server.HandleAsync(Req(1, "initialize", new JsonObject { ["protocolVersion"] = requested, ["capabilities"] = new JsonObject() }), new McpSession(), default))!;
        Assert.Equal(expected, (string?)r["result"]!["protocolVersion"]);
        var caps = r["result"]!["capabilities"]!.AsObject();
        Assert.True(caps.ContainsKey("tools") && caps.ContainsKey("resources") && caps.ContainsKey("prompts") && caps.ContainsKey("completions") && caps.ContainsKey("logging"));
        Assert.Equal("kittyparty-mcp", (string?)r["result"]!["serverInfo"]!["name"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)r["result"]!["instructions"]));
    }

    [Fact]
    public async Task Json_rpc_errors_follow_the_spec()
    {
        var s = await InitAsync();
        var notFound = (JsonObject)(await _server.HandleAsync(Req(1, "nope/nope"), s, default))!;
        Assert.Equal(RpcError.MethodNotFound, (int)notFound["error"]!["code"]!);
        Assert.Equal(1, (int)notFound["id"]!);

        var badVersion = (JsonObject)(await _server.HandleAsync(new JsonObject { ["jsonrpc"] = "1.0", ["id"] = 2, ["method"] = "ping" }, s, default))!;
        Assert.Equal(RpcError.InvalidRequest, (int)badVersion["error"]!["code"]!);

        var unknownTool = (JsonObject)(await _server.HandleAsync(Req(3, "tools/call", new JsonObject { ["name"] = "nope" }), s, default))!;
        Assert.Equal(RpcError.InvalidParams, (int)unknownTool["error"]!["code"]!);

        Assert.Null(await _server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, s, default));
        Assert.Null(await _server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 5, ["result"] = new JsonObject() }, s, default));
        var ping = (JsonObject)(await _server.HandleAsync(Req(6, "ping"), s, default))!;
        Assert.Empty(ping["result"]!.AsObject());
    }

    [Fact]
    public async Task Batches_return_only_responses_for_requests()
    {
        var s = await InitAsync();
        var batch = new JsonArray(Req(1, "ping"), new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, Req(2, "tools/list"));
        var r = (JsonArray)(await _server.HandleAsync(batch, s, default))!;
        Assert.Equal(2, r.Count);
        Assert.Equal(RpcError.InvalidRequest, (int)(await _server.HandleAsync(new JsonArray(), s, default))!["error"]!["code"]!);
    }

    [Fact]
    public async Task Tools_have_schemas_and_annotations_depending_on_version()
    {
        var modern = (JsonArray)(await _server.HandleAsync(Req(1, "tools/list"), await InitAsync(), default))!["result"]!["tools"]!;
        Assert.True(modern.Count >= 20);
        foreach (var t in modern)
        {
            Assert.Equal("object", (string?)t!["inputSchema"]!["type"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)t["description"]));
            Assert.NotNull(t["annotations"]!["readOnlyHint"]);
        }
        Assert.True((bool)modern.First(t => (string)t!["name"]! == "list_missions")!["annotations"]!["readOnlyHint"]!);
        Assert.True((bool)modern.First(t => (string)t!["name"]! == "delete_mission")!["annotations"]!["destructiveHint"]!);

        var legacy = (JsonArray)(await _server.HandleAsync(Req(1, "tools/list"), await InitAsync("2024-11-05"), default))!["result"]!["tools"]!;
        Assert.All(legacy, t => Assert.Null(t!["annotations"]));
    }

    [Fact]
    public async Task List_missions_returns_text_and_structured_content()
    {
        var r = await CallAsync(await InitAsync(), "list_missions");
        Assert.False((bool)r["isError"]!);
        Assert.Contains("echo-apis", Text(r));
        Assert.Contains("21.4% differ", Text(r));
        Assert.Contains("WARNING mirrors write methods", Text(r));
        Assert.Equal(2, r["structuredContent"]!["missions"]!.AsArray().Count);

        var legacy = await CallAsync(await InitAsync("2025-03-26"), "list_missions");
        Assert.Null(legacy["structuredContent"]);
    }

    [Theory]
    [InlineData("echo-apis")]
    [InlineData("Echo APIs")]
    [InlineData("echo apis")]
    [InlineData(FakeApi.EchoId)]
    [InlineData("/m/echo-apis/")]
    public async Task Missions_resolve_by_slug_name_or_id(string reference)
    {
        var r = await CallAsync(await InitAsync(), "get_mission", new JsonObject { ["mission"] = reference });
        Assert.False((bool)r["isError"]!, Text(r));
        Assert.Contains("Echo APIs", Text(r));
    }

    [Fact]
    public async Task Mission_lookup_folds_accents_and_near_misses_and_explains_failures()
    {
        var s = await InitAsync();
        Assert.Contains("PokéAPI", Text(await CallAsync(s, "get_mission", new JsonObject { ["mission"] = "pokeapi" })));
        Assert.Contains("PokéAPI", Text(await CallAsync(s, "get_mission", new JsonObject { ["mission"] = "pokemon" })));
        var miss = await CallAsync(s, "get_mission", new JsonObject { ["mission"] = "zzz" });
        Assert.True((bool)miss["isError"]!);
        Assert.Contains("Available: echo-apis, pok-api", Text(miss));
        var missing = await CallAsync(s, "get_mission");
        Assert.Contains("'mission' is required", Text(missing));
    }

    [Fact]
    public async Task Arguments_are_accepted_in_the_shapes_small_models_produce()
    {
        var s = await InitAsync();
        // limit as a string, config as a JSON string, "path" instead of "paths", lower-case rule type
        var traffic = await CallAsync(s, "search_traffic", new JsonObject { ["mission"] = "echo", ["Limit"] = "5", ["verdict"] = "Mismatch" });
        Assert.False((bool)traffic["isError"]!, Text(traffic));
        Assert.Contains("verdict=mismatch", _api.Calls.Last(c => c.Path.Contains("/logs?")).Path);
        Assert.Contains("limit=5", _api.Calls.Last(c => c.Path.Contains("/logs?")).Path);
        Assert.Contains("cursor=\"abc\"", Text(traffic));
        Assert.Contains("1 differences (1 removed, status)", Text(traffic));

        var add = await CallAsync(s, "add_rule", new JsonObject { ["mission"] = "echo-apis", ["type"] = "ignorefield", ["config"] = "{\"path\": \"$.method\"}" });
        Assert.False((bool)add["isError"]!, Text(add));
        var put = _api.Calls.Last(c => c.Method == "PUT").Body!;
        var rules = put["rules"]!.AsArray();
        Assert.Equal(2, rules.Count);
        Assert.Equal("r1", (string?)rules[0]!["id"]);
        Assert.Equal("$.method", (string?)rules[1]!["config"]!["paths"]![0]);
        Assert.True((bool)put["rediff"]!);
        Assert.Contains("Re-diff job", Text(add));
    }

    [Fact]
    public async Task Invalid_rules_come_back_as_readable_tool_errors()
    {
        var r = await CallAsync(await InitAsync(), "preview_rule", new JsonObject { ["mission"] = "echo-apis", ["type"] = "IgnoreField", ["config"] = new JsonObject { ["paths"] = new JsonArray() } });
        Assert.True((bool)r["isError"]!);
        Assert.Contains("at least one path is required", Text(r));
        var badType = await CallAsync(await InitAsync(), "preview_rule", new JsonObject { ["mission"] = "echo-apis", ["type"] = "Magic", ["config"] = new JsonObject() });
        Assert.Contains("must be one of", Text(badType));
    }

    [Fact]
    public async Task Preview_and_causes_summarise_effects_and_suggest_rules()
    {
        var s = await InitAsync();
        var p = await CallAsync(s, "preview_rule", new JsonObject { ["mission"] = "echo-apis", ["type"] = "IgnoreField", ["config"] = new JsonObject { ["paths"] = new JsonArray("$.method") } });
        Assert.Contains("14 differ now, 3 would differ", Text(p));
        var c = await CallAsync(s, "list_causes", new JsonObject { ["mission"] = "echo-apis" });
        Assert.Contains("missing on Dev $.method", Text(c));
        Assert.Contains("suggested rule: IgnoreField {\"paths\":[\"$.method\"]}", Text(c));
        Assert.Contains(FakeApi.LogId, Text(c));
    }

    [Fact]
    public async Task Request_tools_show_diff_and_bodies()
    {
        var s = await InitAsync();
        var d = await CallAsync(s, "get_request_diff", new JsonObject { ["mission"] = "echo-apis", ["request_id"] = FakeApi.LogId });
        Assert.Contains("verdict: mismatch", Text(d));
        Assert.Contains("missing on Dev $.method", Text(d));
        var body = await CallAsync(s, "get_response_body", new JsonObject { ["mission"] = "echo-apis", ["request_id"] = FakeApi.LogId, ["side"] = "MAIN" });
        Assert.Contains("\"method\": \"GET\"", Text(body));
        var bad = await CallAsync(s, "get_request_diff", new JsonObject { ["mission"] = "echo-apis", ["request_id"] = "01a1b728" });
        Assert.Contains("must be a full id", Text(bad));
    }

    [Fact]
    public async Task Read_only_mode_hides_and_refuses_mutating_tools()
    {
        var ro = _api.CreateServer(readOnly: true);
        var s = new McpSession();
        var tools = (JsonArray)(await ro.HandleAsync(Req(1, "tools/list"), s, default))!["result"]!["tools"]!;
        Assert.DoesNotContain(tools, t => (string)t!["name"]! is "add_rule" or "delete_mission" or "start_fuzz");
        Assert.Contains(tools, t => (string)t!["name"]! == "list_missions");
        var r = (JsonObject)(await ro.HandleAsync(Req(2, "tools/call", new JsonObject { ["name"] = "add_rule", ["arguments"] = new JsonObject() }), s, default))!["result"]!;
        Assert.True((bool)r["isError"]!);
        Assert.Contains("read-only", Text(r));
        Assert.DoesNotContain(_api.Calls, c => c.Method == "PUT");
    }

    [Fact]
    public async Task Write_replays_and_fuzzing_are_refused_without_allow_writes()
    {
        var s = await InitAsync();
        var replay = await CallAsync(s, "replay_request", new JsonObject { ["mission"] = "echo-apis", ["request_id"] = FakeApi.PostLogId });
        Assert.True((bool)replay["isError"]!);
        Assert.Contains("allow_writes=true", Text(replay));
        Assert.DoesNotContain("allowUnsafe", Text(replay));

        var ok = await CallAsync(s, "replay_request", new JsonObject { ["mission"] = "echo-apis", ["request_id"] = FakeApi.PostLogId, ["allow_writes"] = true });
        Assert.False((bool)ok["isError"]!, Text(ok));
        Assert.Contains("Now: match", Text(ok));

        var calls = _api.Calls.Count(c => c.Path.Contains("/jobs/fuzz"));
        var fuzz = await CallAsync(s, "start_fuzz", new JsonObject { ["mission"] = "echo-apis", ["request_id"] = FakeApi.PostLogId });
        Assert.True((bool)fuzz["isError"]!);
        Assert.Contains("allow_writes=true", Text(fuzz));
        var fuzz2 = await CallAsync(s, "start_fuzz", new JsonObject { ["mission"] = "echo-apis", ["method"] = "delete", ["path"] = "/items/1" });
        Assert.True((bool)fuzz2["isError"]!);
        Assert.Equal(calls, _api.Calls.Count(c => c.Path.Contains("/jobs/fuzz"))); // nothing queued

        var get = await CallAsync(s, "start_fuzz", new JsonObject { ["mission"] = "echo-apis", ["request_id"] = FakeApi.LogId, ["volume"] = 5 });
        Assert.False((bool)get["isError"]!, Text(get));
    }

    [Fact]
    public async Task Delete_requires_the_slug_as_confirmation()
    {
        var s = await InitAsync();
        var refused = await CallAsync(s, "delete_mission", new JsonObject { ["mission"] = "echo-apis", ["confirm"] = "yes" });
        Assert.True((bool)refused["isError"]!);
        Assert.DoesNotContain(_api.Calls, c => c.Method == "DELETE");
        var ok = await CallAsync(s, "delete_mission", new JsonObject { ["mission"] = "echo-apis", ["confirm"] = "echo-apis" });
        Assert.False((bool)ok["isError"]!);
        Assert.Contains(_api.Calls, c => c.Method == "DELETE" && c.Path.Contains(FakeApi.EchoId));
    }

    [Fact]
    public async Task Create_mission_defaults_to_safe_methods_and_accepts_percentages()
    {
        var r = await CallAsync(await InitAsync(), "create_mission", new JsonObject { ["name"] = "Orders API v2", ["main_url"] = "https://a.test", ["dev_url"] = "https://b.test", ["sample_rate"] = "10%" });
        Assert.False((bool)r["isError"]!, Text(r));
        var body = _api.Calls.Last(c => c.Method == "POST" && c.Path == "/api/missions").Body!;
        Assert.Equal("orders-api-v2", (string?)body["slug"]);
        Assert.Equal(0.1, (double)body["sampleRate"]!, 6);
        Assert.Equal(["GET", "HEAD"], body["mirrorMethods"]!.AsArray().Select(x => (string)x!));
        Assert.False((bool)body["acknowledgeUnsafeMethods"]!);
    }

    [Fact]
    public async Task Api_outages_become_tool_errors_not_crashes()
    {
        var options = new McpOptions { ApiUrl = "http://127.0.0.1:1" };
        var api = new KittyApi(new HttpClient(), options);
        var tools = MirrorTools.Create(api).ToList();
        var server = new McpServer(new ToolRegistry(tools, options), new ResourceProvider(api, tools), new PromptProvider(), options, Microsoft.Extensions.Logging.Abstractions.NullLogger<McpServer>.Instance);
        var r = (JsonObject)(await server.HandleAsync(Req(1, "tools/call", new JsonObject { ["name"] = "list_missions" }), new McpSession(), default))!["result"]!;
        Assert.True((bool)r["isError"]!);
        Assert.Contains("not reachable", Text(r));
    }

    [Fact]
    public async Task Long_outputs_are_truncated()
    {
        var options = new McpOptions { ApiUrl = _api.Url, MaxChars = 500 };
        var api = new KittyApi(new HttpClient(), options);
        var tools = MirrorTools.Create(api).ToList();
        var server = new McpServer(new ToolRegistry(tools, options), new ResourceProvider(api, tools), new PromptProvider(), options, Microsoft.Extensions.Logging.Abstractions.NullLogger<McpServer>.Instance);
        var r = (JsonObject)(await server.HandleAsync(Req(1, "tools/call", new JsonObject { ["name"] = "list_rule_types" }), new McpSession(), default))!;
        Assert.NotNull(r["error"] ?? r["result"]);
    }

    [Fact]
    public async Task Resources_list_templates_and_read()
    {
        var s = await InitAsync();
        var list = (JsonArray)(await _server.HandleAsync(Req(1, "resources/list"), s, default))!["result"]!["resources"]!;
        Assert.Contains(list, r => (string)r!["uri"]! == "kittyparty://missions/echo-apis");
        var templates = (JsonArray)(await _server.HandleAsync(Req(2, "resources/templates/list"), s, default))!["result"]!["resourceTemplates"]!;
        Assert.Contains(templates, t => (string)t!["uriTemplate"]! == "kittyparty://missions/{mission}/requests/{request_id}");
        var read = (JsonObject)(await _server.HandleAsync(Req(3, "resources/read", new JsonObject { ["uri"] = "kittyparty://missions/echo-apis/rules" }), s, default))!;
        var content = read["result"]!["contents"]![0]!;
        Assert.Equal("kittyparty://missions/echo-apis/rules", (string?)content["uri"]);
        Assert.Contains("IgnoreField", (string?)content["text"]);
        var missing = (JsonObject)(await _server.HandleAsync(Req(4, "resources/read", new JsonObject { ["uri"] = "kittyparty://missions/nope" }), s, default))!;
        Assert.Equal(RpcError.ResourceNotFound, (int)missing["error"]!["code"]!);
    }

    [Fact]
    public async Task Prompts_list_and_render_with_arguments()
    {
        var s = await InitAsync();
        var list = (JsonArray)(await _server.HandleAsync(Req(1, "prompts/list"), s, default))!["result"]!["prompts"]!;
        Assert.Equal(["triage_mission", "tune_rules", "release_check"], list.Select(p => (string)p!["name"]!));
        var get = (JsonObject)(await _server.HandleAsync(Req(2, "prompts/get", new JsonObject { ["name"] = "release_check", ["arguments"] = new JsonObject { ["mission"] = "echo-apis", ["hours"] = "6" } }), s, default))!;
        var text = (string)get["result"]!["messages"]![0]!["content"]!["text"]!;
        Assert.Contains("\"echo-apis\"", text);
        Assert.Contains("hours=6", text);
        var missing = (JsonObject)(await _server.HandleAsync(Req(3, "prompts/get", new JsonObject { ["name"] = "triage_mission" }), s, default))!;
        Assert.Equal(RpcError.InvalidParams, (int)missing["error"]!["code"]!);
    }

    [Fact]
    public async Task Completion_suggests_mission_slugs_and_rule_types()
    {
        var s = await InitAsync();
        var r = (JsonObject)(await _server.HandleAsync(Req(1, "completion/complete", new JsonObject
        {
            ["ref"] = new JsonObject { ["type"] = "ref/prompt", ["name"] = "triage_mission" },
            ["argument"] = new JsonObject { ["name"] = "mission", ["value"] = "ec" },
        }), s, default))!;
        Assert.Equal(["echo-apis"], r["result"]!["completion"]!["values"]!.AsArray().Select(v => (string)v!));
        var t = (JsonObject)(await _server.HandleAsync(Req(2, "completion/complete", new JsonObject { ["ref"] = new JsonObject { ["type"] = "ref/prompt", ["name"] = "x" }, ["argument"] = new JsonObject { ["name"] = "type", ["value"] = "Ignore" } }), s, default))!;
        Assert.Equal(["IgnoreField", "IgnoreOrder", "IgnoreHeader"], t["result"]!["completion"]!["values"]!.AsArray().Select(v => (string)v!));
    }

    [Fact]
    public async Task Cancelled_requests_get_no_response()
    {
        var s = await InitAsync();
        _api.Delay = TimeSpan.FromSeconds(5);
        var call = _server.HandleAsync(Req(77, "tools/call", new JsonObject { ["name"] = "list_missions" }), s, default);
        await Task.Delay(200);
        await _server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = 77, ["reason"] = "user" } }, s, default);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(await call);
        Assert.True(sw.ElapsedMilliseconds < 3000);
        _api.Delay = TimeSpan.Zero;
    }

    [Fact]
    public async Task Logging_level_is_validated_and_tool_errors_are_logged_to_the_client()
    {
        var s = await InitAsync();
        var logged = new List<JsonObject>();
        s.Notify = m => { logged.Add(m); return Task.CompletedTask; };
        Assert.Equal(RpcError.InvalidParams, (int)(await _server.HandleAsync(Req(1, "logging/setLevel", new JsonObject { ["level"] = "loud" }), s, default))!["error"]!["code"]!);
        await _server.HandleAsync(Req(2, "logging/setLevel", new JsonObject { ["level"] = "warning" }), s, default);
        await CallAsync(s, "get_mission", new JsonObject { ["mission"] = "zzz" });
        var log = Assert.Single(logged);
        Assert.Equal("notifications/message", (string?)log["method"]);
        Assert.Equal("warning", (string?)log["params"]!["level"]);
        await _server.HandleAsync(Req(3, "logging/setLevel", new JsonObject { ["level"] = "error" }), s, default);
        await CallAsync(s, "get_mission", new JsonObject { ["mission"] = "zzz" });
        Assert.Single(logged);
    }

    [Theory]
    [InlineData("type_changed", "$.items[3].total", "1", "\"1\"", "TypeCoercion", "$.items[*].total")]
    [InlineData("header", "header:cache-control", "a", "b", "IgnoreHeader", null)]
    [InlineData("order", "$.tags", null, null, "IgnoreOrder", "$.tags")]
    [InlineData("changed", "$.items[sku=A].price", "49.99", "49.9", "NumericTolerance", "$.items[*].price")]
    [InlineData("added", "$.trace", null, "{}", "IgnoreField", null)]
    public void Suggestions_match_the_kind_of_difference(string kind, string path, string? main, string? dev, string type, string? wildPath)
    {
        var s = MirrorTools.Suggest(kind, path, main, dev)!;
        Assert.Equal(type, (string?)s["type"]);
        if (wildPath is not null) Assert.Equal(wildPath, (string?)s["config"]!["path"]);
        Assert.Null(MirrorTools.Suggest("removed", "$.items[sku=A]", "{}", null));
    }
}
