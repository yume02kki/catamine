using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mirror.Mcp.Features;

namespace Mirror.Mcp.Protocol;

/// <summary>JSON-RPC 2.0 error codes used by MCP.</summary>
public static class RpcError
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
    /// <summary>MCP: resource not found.</summary>
    public const int ResourceNotFound = -32002;
}

public sealed class RpcException(int code, string message, JsonNode? data = null) : Exception(message)
{
    public int Code { get; } = code;
    public JsonNode? ErrorData { get; } = data;
}

/// <summary>State of one MCP connection (one stdio process, one HTTP session or one SSE stream).</summary>
public sealed class McpSession
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public bool Initialized { get; set; }
    public string ProtocolVersion { get; set; } = McpServer.LatestProtocolVersion;
    public JsonObject? ClientInfo { get; set; }
    public string LogLevel { get; set; } = "info";
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Sends a server→client notification, when the transport has a channel for it.</summary>
    public Func<JsonObject, Task>? Notify { get; set; }
    internal ConcurrentDictionary<string, CancellationTokenSource> InFlight { get; } = new();

    public bool SupportsStructuredContent => string.CompareOrdinal(ProtocolVersion, "2025-06-18") >= 0;
}

/// <summary>
/// Transport-independent MCP server: takes parsed JSON-RPC messages, returns the response(s).
/// Implements lifecycle (initialize/ping), tools, resources (+templates), prompts, completion, logging and cancellation.
/// </summary>
public sealed class McpServer(ToolRegistry tools, ResourceProvider resources, PromptProvider prompts, McpOptions options, ILogger<McpServer> log)
{
    public const string LatestProtocolVersion = "2025-06-18";
    public static readonly string[] SupportedProtocolVersions = ["2025-06-18", "2025-03-26", "2024-11-05"];
    private static readonly string[] LogLevels = ["debug", "info", "notice", "warning", "error", "critical", "alert", "emergency"];

    public const string Instructions = """
        KittyParty mirrors real traffic from a production API ("Main") to a candidate ("Dev") and records every difference.
        Work like this:
        1. list_missions to see what is being compared. Refer to a mission by its name or slug.
        2. mission_stats and list_causes to understand how often and why Main and Dev differ.
        3. search_traffic and get_request_diff to look at concrete requests.
        4. preview_rule before add_rule: rules hide expected noise (ids, timestamps, hostnames); never hide a real regression.
        Results are short summaries; ask for more detail with the specific tools.
        """;

    /// <summary>Handles one message or a batch. Returns null when nothing must be sent back (notifications, responses).</summary>
    public async Task<JsonNode?> HandleAsync(JsonNode? message, McpSession session, CancellationToken ct)
    {
        session.LastSeen = DateTimeOffset.UtcNow;
        if (message is JsonArray batch)
        {
            if (batch.Count == 0) return Error(null, RpcError.InvalidRequest, "empty batch");
            var results = await Task.WhenAll(batch.Select(m => HandleOneAsync(m, session, ct)));
            var responses = new JsonArray();
            foreach (var r in results) if (r is not null) responses.Add(r);
            return responses.Count == 0 ? null : responses;
        }
        return await HandleOneAsync(message, session, ct);
    }

    public static bool IsRequest(JsonNode? message) => message is JsonObject o && o.ContainsKey("method") && o.ContainsKey("id");

    public static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data = null)
    {
        var err = new JsonObject { ["code"] = code, ["message"] = message };
        if (data is not null) err["data"] = data;
        return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = err };
    }

    private static JsonObject Result(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result };

    private async Task<JsonObject?> HandleOneAsync(JsonNode? node, McpSession session, CancellationToken ct)
    {
        if (node is not JsonObject msg) return Error(null, RpcError.InvalidRequest, "message must be a JSON object");
        if (msg["jsonrpc"]?.GetValueKind() != JsonValueKind.String || (string?)msg["jsonrpc"] != "2.0")
            return Error(msg["id"], RpcError.InvalidRequest, "jsonrpc must be \"2.0\"");

        var hasId = msg.TryGetPropertyValue("id", out var id);
        if (!msg.TryGetPropertyValue("method", out var methodNode))
            return null; // a response to a server→client request; this server sends none, so ignore it
        if (methodNode?.GetValueKind() != JsonValueKind.String) return Error(id, RpcError.InvalidRequest, "method must be a string");
        var method = (string)methodNode!;
        var @params = msg["params"] as JsonObject ?? [];

        if (!hasId)
        {
            await HandleNotificationAsync(method, @params, session);
            return null;
        }
        if (id is null || (id.GetValueKind() != JsonValueKind.String && id.GetValueKind() != JsonValueKind.Number))
            return Error(null, RpcError.InvalidRequest, "id must be a string or a number");

        var key = id.ToJsonString();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        session.InFlight[key] = cts;
        try
        {
            var result = await DispatchAsync(method, @params, session, cts.Token);
            return Result(id, result);
        }
        catch (RpcException ex)
        {
            return Error(id, ex.Code, ex.Message, ex.ErrorData);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            return null; // cancelled by the client: the spec says not to respond
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unhandled error in {Method}", method);
            return Error(id, RpcError.InternalError, "internal error: " + ex.Message);
        }
        finally
        {
            session.InFlight.TryRemove(key, out _);
        }
    }

    private Task HandleNotificationAsync(string method, JsonObject p, McpSession session)
    {
        switch (method)
        {
            case "notifications/initialized":
                session.Initialized = true;
                break;
            case "notifications/cancelled":
                var rid = p["requestId"]?.ToJsonString();
                if (rid is not null && session.InFlight.TryGetValue(rid, out var cts)) cts.Cancel();
                break;
            default:
                log.LogDebug("Ignoring notification {Method}", method);
                break;
        }
        return Task.CompletedTask;
    }

    private async Task<JsonNode> DispatchAsync(string method, JsonObject p, McpSession s, CancellationToken ct)
    {
        switch (method)
        {
            case "initialize": return Initialize(p, s);
            case "ping": return new JsonObject();
            case "tools/list": return tools.List(s, options.ReadOnly);
            case "tools/call":
            {
                var name = Str(p, "name");
                var args = p["arguments"] switch
                {
                    JsonObject o => o,
                    null => [],
                    var other when other.GetValueKind() == JsonValueKind.String => TryParseObject((string)other!) ?? throw new RpcException(RpcError.InvalidParams, "arguments must be an object"),
                    _ => throw new RpcException(RpcError.InvalidParams, "arguments must be an object"),
                };
                return await tools.CallAsync(name, args, s, options.ReadOnly, ct);
            }
            case "resources/list": return await resources.ListAsync(ct);
            case "resources/templates/list": return resources.Templates();
            case "resources/read": return await resources.ReadAsync(Str(p, "uri"), ct);
            case "prompts/list": return prompts.List();
            case "prompts/get": return prompts.Get(Str(p, "name"), p["arguments"] as JsonObject ?? []);
            case "completion/complete": return await CompleteAsync(p, ct);
            case "logging/setLevel":
            {
                var level = Str(p, "level");
                if (!LogLevels.Contains(level)) throw new RpcException(RpcError.InvalidParams, $"unknown level '{level}'");
                s.LogLevel = level;
                return new JsonObject();
            }
            default:
                throw new RpcException(RpcError.MethodNotFound, $"method not found: {method}");
        }
    }

    private JsonObject Initialize(JsonObject p, McpSession s)
    {
        var requested = (string?)p["protocolVersion"];
        s.ProtocolVersion = requested is not null && SupportedProtocolVersions.Contains(requested) ? requested : LatestProtocolVersion;
        s.ClientInfo = p["clientInfo"]?.DeepClone() as JsonObject;
        s.Initialized = true; // lenient: some clients send requests before notifications/initialized
        log.LogInformation("Client {Client} initialized (protocol {Version})", (string?)s.ClientInfo?["name"] ?? "unknown", s.ProtocolVersion);
        return new JsonObject
        {
            ["protocolVersion"] = s.ProtocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
                ["resources"] = new JsonObject { ["subscribe"] = false, ["listChanged"] = false },
                ["prompts"] = new JsonObject { ["listChanged"] = false },
                ["completions"] = new JsonObject(),
                ["logging"] = new JsonObject(),
            },
            ["serverInfo"] = new JsonObject { ["name"] = "kittyparty-mcp", ["title"] = "KittyParty API Diff Mirror", ["version"] = "2.0.0" },
            ["instructions"] = Instructions,
        };
    }

    private async Task<JsonNode> CompleteAsync(JsonObject p, CancellationToken ct)
    {
        var argName = (string?)p["argument"]?["name"] ?? "";
        var value = (string?)p["argument"]?["value"] ?? "";
        var values = new List<string>();
        if (argName == "mission")
        {
            foreach (var slug in await resources.MissionSlugsAsync(ct))
                if (slug.StartsWith(value, StringComparison.OrdinalIgnoreCase)) values.Add(slug);
        }
        else if (argName is "type" or "rule_type")
        {
            values.AddRange(ToolRegistry.RuleTypes.Where(t => t.StartsWith(value, StringComparison.OrdinalIgnoreCase)));
        }
        return new JsonObject
        {
            ["completion"] = new JsonObject
            {
                ["values"] = new JsonArray(values.Take(100).Select(v => (JsonNode)v).ToArray()),
                ["total"] = values.Count,
                ["hasMore"] = values.Count > 100,
            },
        };
    }

    /// <summary>Sends a log notification if the session has a channel and the level passes its threshold.</summary>
    public static async Task LogAsync(McpSession s, string level, string message)
    {
        if (s.Notify is null || Array.IndexOf(LogLevels, level) < Array.IndexOf(LogLevels, s.LogLevel)) return;
        await s.Notify(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/message",
            ["params"] = new JsonObject { ["level"] = level, ["logger"] = "kittyparty", ["data"] = message },
        });
    }

    private static string Str(JsonObject p, string name) =>
        p[name] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? (string)v! : throw new RpcException(RpcError.InvalidParams, $"'{name}' is required");

    internal static JsonObject? TryParseObject(string s)
    {
        try { return JsonNode.Parse(s) as JsonObject; }
        catch (JsonException) { return null; }
    }
}

public sealed class McpOptions
{
    public string ApiUrl { get; set; } = "http://localhost:8081";
    public bool ReadOnly { get; set; }
    /// <summary>Upper bound on text returned by one tool call; small local models have small context windows.</summary>
    public int MaxChars { get; set; } = 8000;
    public string[] AllowedOrigins { get; set; } = [];
}
