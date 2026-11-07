using System.Text.Json.Nodes;
using Mirror.Mcp.Protocol;

namespace Mirror.Mcp.Features;

/// <summary>
/// Read-only views as MCP resources: kittyparty://missions, kittyparty://missions/{mission}, …/rules, …/causes,
/// …/requests/{request_id}. They reuse the tool implementations so text is identical.
/// </summary>
public sealed class ResourceProvider(KittyApi api, IReadOnlyList<ToolDef> tools)
{
    private const string Scheme = "kittyparty://missions";

    private static readonly (string Template, string Name, string Title, string Description)[] TemplateDefs =
    [
        ($"{Scheme}/{{mission}}", "mission", "Mission", "Configuration and rules of one mission"),
        ($"{Scheme}/{{mission}}/rules", "mission-rules", "Mission rules", "The mission's comparison rules"),
        ($"{Scheme}/{{mission}}/causes", "mission-causes", "Why Main and Dev differ", "Differing requests of the last 24 h grouped by cause"),
        ($"{Scheme}/{{mission}}/stats", "mission-stats", "Mission statistics", "Verdict counts and latency of the last 24 h"),
        ($"{Scheme}/{{mission}}/requests/{{request_id}}", "request-diff", "Request diff", "Differences between Main and Dev for one request"),
    ];

    public async Task<IReadOnlyList<string>> MissionSlugsAsync(CancellationToken ct)
    {
        try { return (await api.MissionsAsync(ct)).OfType<JsonObject>().Select(m => (string)m["slug"]!).ToList(); }
        catch (ApiException) { return []; }
    }

    public async Task<JsonObject> ListAsync(CancellationToken ct)
    {
        var list = new JsonArray
        {
            new JsonObject { ["uri"] = Scheme, ["name"] = "missions", ["title"] = "Missions", ["description"] = "All missions with 24 h volume and diff rate", ["mimeType"] = "text/plain" },
        };
        foreach (var m in (await api.MissionsAsync(ct)).OfType<JsonObject>())
            list.Add(new JsonObject
            {
                ["uri"] = $"{Scheme}/{m["slug"]}", ["name"] = (string?)m["slug"], ["title"] = (string?)m["name"],
                ["description"] = $"{m["mainUrl"]} vs {m["devUrl"]}", ["mimeType"] = "text/plain",
            });
        return new JsonObject { ["resources"] = list };
    }

    public JsonObject Templates() => new()
    {
        ["resourceTemplates"] = new JsonArray(TemplateDefs.Select(t => (JsonNode)new JsonObject
        {
            ["uriTemplate"] = t.Template, ["name"] = t.Name, ["title"] = t.Title, ["description"] = t.Description, ["mimeType"] = "text/plain",
        }).ToArray()),
    };

    public async Task<JsonObject> ReadAsync(string uri, CancellationToken ct)
    {
        if (!uri.StartsWith(Scheme, StringComparison.Ordinal)) throw NotFound(uri);
        var parts = uri[Scheme.Length..].Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var (tool, args) = parts switch
        {
            [] => ("list_missions", new JsonObject()),
            [var m] => ("get_mission", new JsonObject { ["mission"] = m }),
            [var m, "rules"] => ("get_rules", new JsonObject { ["mission"] = m }),
            [var m, "causes"] => ("list_causes", new JsonObject { ["mission"] = m }),
            [var m, "stats"] => ("mission_stats", new JsonObject { ["mission"] = m }),
            [var m, "requests", var id] => ("get_request_diff", new JsonObject { ["mission"] = m, ["request_id"] = id }),
            _ => throw NotFound(uri),
        };
        ToolResult r;
        try
        {
            r = await tools.First(t => t.Name == tool).Handler(new Args(args), new McpSession(), ct);
        }
        catch (Exception ex) when (ex is ToolException or ApiException)
        {
            throw new RpcException(RpcError.ResourceNotFound, ex.Message, new JsonObject { ["uri"] = uri });
        }
        return new JsonObject { ["contents"] = new JsonArray(new JsonObject { ["uri"] = uri, ["mimeType"] = "text/plain", ["text"] = r.Text }) };
    }

    private static RpcException NotFound(string uri) => new(RpcError.ResourceNotFound, "resource not found", new JsonObject { ["uri"] = uri });
}

/// <summary>Ready-made task prompts that walk a (local) model through the tools in a sensible order.</summary>
public sealed class PromptProvider
{
    private static readonly (string Name, string Title, string Description, (string Name, string Description, bool Required)[] Args)[] Defs =
    [
        ("triage_mission", "Triage a mission", "Find out why Dev differs from Main and which differences matter.",
            [("mission", "Mission name or slug", true)]),
        ("tune_rules", "Tune comparison rules", "Propose and verify rules that remove expected noise without hiding regressions.",
            [("mission", "Mission name or slug", true)]),
        ("release_check", "Is Dev ready?", "Decide whether Dev can replace Main, with evidence.",
            [("mission", "Mission name or slug", true), ("hours", "Time window in hours (default 24)", false)]),
    ];

    public JsonObject List() => new()
    {
        ["prompts"] = new JsonArray(Defs.Select(d => (JsonNode)new JsonObject
        {
            ["name"] = d.Name, ["title"] = d.Title, ["description"] = d.Description,
            ["arguments"] = new JsonArray(d.Args.Select(a => (JsonNode)new JsonObject { ["name"] = a.Name, ["description"] = a.Description, ["required"] = a.Required }).ToArray()),
        }).ToArray()),
    };

    public JsonObject Get(string name, JsonObject args)
    {
        var def = Defs.FirstOrDefault(d => d.Name == name);
        if (def.Name is null) throw new RpcException(RpcError.InvalidParams, $"unknown prompt '{name}'");
        foreach (var a in def.Args.Where(a => a.Required))
            if (string.IsNullOrWhiteSpace((string?)args[a.Name])) throw new RpcException(RpcError.InvalidParams, $"argument '{a.Name}' is required");
        var mission = (string?)args["mission"];
        var hours = (string?)args["hours"] is { Length: > 0 } h ? h : "24";
        var text = name switch
        {
            "triage_mission" => $"""
                Investigate the KittyParty mission "{mission}", which mirrors production traffic (Main) to a candidate (Dev).
                1. Call mission_stats for "{mission}" to see how many requests differ, error or time out.
                2. Call list_causes for "{mission}". For the two biggest causes, call get_request_diff on the example request_id.
                3. If there are dev_error or dev_timeout requests, call search_traffic with that verdict and look at one with get_request_diff.
                Then answer in short bullet points: what differs, how often, whether each cause looks like expected noise or a real regression, and what to do next.
                Do not change any rules.
                """,
            "tune_rules" => $"""
                Help reduce noise in the KittyParty mission "{mission}" without hiding real regressions.
                1. Call get_rules and list_causes for "{mission}".
                2. For each cause that is clearly expected (request ids, timestamps, host names, field order, new optional fields, number formatting), pick a rule. list_rule_types shows the configs.
                3. Call preview_rule for each candidate and keep it only if it lowers the number of differing requests and newly differing is 0.
                4. Ask me before calling add_rule. Never ignore a field just because it differs: say why the difference is harmless.
                """,
            "release_check" => $"""
                Decide whether Dev is ready to replace Main for the KittyParty mission "{mission}", based on the last {hours} hours.
                1. Call mission_stats for "{mission}" with hours={hours}.
                2. Call list_causes with hours={hours} and inspect the top causes with get_request_diff.
                3. Call search_traffic with verdict=dev_error and with verdict=dev_timeout (hours={hours}).
                Reply with READY, READY WITH CAVEATS or NOT READY, then the evidence: match rate, error and timeout counts, latency of Dev versus Main, and every remaining cause that is not explained by the mission's rules.
                """,
            _ => throw new RpcException(RpcError.InvalidParams, $"unknown prompt '{name}'"),
        };
        return new JsonObject
        {
            ["description"] = def.Description,
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = new JsonObject { ["type"] = "text", ["text"] = text } }),
        };
    }
}
