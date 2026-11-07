using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mirror.Mcp.Protocol;

namespace Mirror.Mcp.Features;

/// <summary>Error the model can read and act on (returned as a tool result with isError=true, not as a protocol error).</summary>
public sealed class ToolException(string message) : Exception(message);

public sealed record ToolResult(string Text, JsonNode? Structured = null, bool IsError = false);

public sealed record ToolDef(
    string Name,
    string Title,
    string Description,
    JsonObject InputSchema,
    bool ReadOnly,
    bool Destructive,
    bool Idempotent,
    bool OpenWorld,
    Func<Args, McpSession, CancellationToken, Task<ToolResult>> Handler);

public sealed class ToolRegistry(IEnumerable<ToolDef> tools, McpOptions options)
{
    public static readonly string[] RuleTypes = ["IgnoreField", "PrimaryKey", "SearchAndReplace", "IgnoreOrder", "NumericTolerance", "IgnoreHeader", "TypeCoercion"];

    private readonly Dictionary<string, ToolDef> _tools = tools.ToDictionary(t => t.Name);

    public IReadOnlyCollection<ToolDef> All => _tools.Values;

    public JsonObject List(McpSession session, bool readOnlyMode)
    {
        var arr = new JsonArray();
        foreach (var t in _tools.Values.Where(t => t.ReadOnly || !readOnlyMode))
        {
            var o = new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["inputSchema"] = t.InputSchema.DeepClone(),
            };
            if (string.CompareOrdinal(session.ProtocolVersion, "2025-03-26") >= 0)
            {
                o["annotations"] = new JsonObject
                {
                    ["title"] = t.Title,
                    ["readOnlyHint"] = t.ReadOnly,
                    ["destructiveHint"] = t.Destructive,
                    ["idempotentHint"] = t.Idempotent,
                    ["openWorldHint"] = t.OpenWorld,
                };
            }
            if (session.SupportsStructuredContent) o["title"] = t.Title;
            arr.Add(o);
        }
        return new JsonObject { ["tools"] = arr };
    }

    public async Task<JsonObject> CallAsync(string name, JsonObject arguments, McpSession session, bool readOnlyMode, CancellationToken ct)
    {
        if (!_tools.TryGetValue(name, out var tool))
            throw new RpcException(RpcError.InvalidParams, $"unknown tool '{name}'", new JsonObject { ["available"] = new JsonArray(_tools.Keys.Select(k => (JsonNode)k).ToArray()) });

        ToolResult result;
        if (readOnlyMode && !tool.ReadOnly)
        {
            result = new ToolResult($"'{name}' changes data and this server runs in read-only mode (KITTYPARTY_MCP_READONLY).", IsError: true);
        }
        else
        {
            try
            {
                result = await tool.Handler(new Args(arguments), session, ct);
            }
            catch (ToolException ex)
            {
                result = new ToolResult("Error: " + ex.Message, IsError: true);
            }
            catch (ApiException ex)
            {
                result = new ToolResult("Error from KittyParty api: " + ex.Message, IsError: true);
            }
            if (result.IsError) await McpServer.LogAsync(session, "warning", $"{name}: {result.Text}");
        }

        var text = result.Text.Length > options.MaxChars
            ? result.Text[..options.MaxChars] + $"\n… (truncated at {options.MaxChars} characters; narrow the request for more)"
            : result.Text;
        var response = new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = result.IsError,
        };
        if (result.Structured is not null && session.SupportsStructuredContent)
            response["structuredContent"] = result.Structured is JsonObject ? result.Structured : new JsonObject { ["result"] = result.Structured };
        return response;
    }
}

/// <summary>
/// Forgiving argument reader. Small local models often send numbers and booleans as strings, objects as JSON strings,
/// or a single value where a list is expected; all of that is accepted. Problems become <see cref="ToolException"/>s
/// with a message the model can use to correct itself.
/// </summary>
public sealed class Args(JsonObject raw)
{
    public JsonObject Raw => raw;

    private JsonNode? Get(string name)
    {
        if (raw.TryGetPropertyValue(name, out var v)) return v;
        foreach (var (k, val) in raw)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase) || string.Equals(k.Replace("_", ""), name.Replace("_", ""), StringComparison.OrdinalIgnoreCase))
                return val;
        return null;
    }

    public bool Has(string name) => Get(name) is not null && !(Get(name) is JsonValue v && v.GetValueKind() == JsonValueKind.String && string.IsNullOrWhiteSpace((string?)v));

    public string? Str(string name, bool required = false)
    {
        var v = Get(name);
        string? s = v switch
        {
            null => null,
            JsonValue j when j.GetValueKind() == JsonValueKind.String => (string?)j,
            JsonValue j => j.ToJsonString(),
            _ => v.ToJsonString(),
        };
        if (string.IsNullOrWhiteSpace(s)) s = null;
        if (required && s is null) throw new ToolException($"'{name}' is required");
        return s?.Trim();
    }

    public string Req(string name) => Str(name, required: true)!;

    public int Int(string name, int @default, int min, int max)
    {
        var v = Get(name);
        if (v is null) return @default;
        int n;
        if (v is JsonValue j && j.GetValueKind() == JsonValueKind.Number) n = (int)Math.Round(j.GetValue<double>());
        else if (!int.TryParse(Str(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
            throw new ToolException($"'{name}' must be a whole number");
        return Math.Clamp(n, min, max);
    }

    public double? Dbl(string name)
    {
        var v = Get(name);
        if (v is null) return null;
        if (v is JsonValue j && j.GetValueKind() == JsonValueKind.Number) return j.GetValue<double>();
        var s = Str(name)?.TrimEnd('%');
        if (s is null) return null;
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) throw new ToolException($"'{name}' must be a number");
        return Str(name)!.EndsWith('%') ? d / 100 : d;
    }

    public bool Bool(string name, bool @default = false)
    {
        var v = Get(name);
        if (v is null) return @default;
        if (v is JsonValue j && j.GetValueKind() is JsonValueKind.True or JsonValueKind.False) return j.GetValue<bool>();
        return Str(name)?.ToLowerInvariant() switch
        {
            "true" or "yes" or "1" or "y" => true,
            "false" or "no" or "0" or "n" => false,
            _ => throw new ToolException($"'{name}' must be true or false"),
        };
    }

    public bool? OptBool(string name) => Get(name) is null ? null : Bool(name);

    public string? Enum(string name, string[] allowed, string? @default = null)
    {
        var s = Str(name);
        if (s is null) return @default;
        var hit = allowed.FirstOrDefault(a => string.Equals(a, s, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Replace("_", ""), s.Replace("_", "").Replace(" ", "").Replace("-", ""), StringComparison.OrdinalIgnoreCase));
        return hit ?? throw new ToolException($"'{name}' must be one of: {string.Join(", ", allowed)}");
    }

    public string[]? List(string name)
    {
        var v = Get(name);
        return v switch
        {
            null => null,
            JsonArray a => a.Select(x => x is JsonValue j && j.GetValueKind() == JsonValueKind.String ? (string)j! : x?.ToJsonString() ?? "").Where(x => x.Length > 0).ToArray(),
            _ when Str(name) is { } s && s.StartsWith('[') => (JsonNode.Parse(s) as JsonArray)?.Select(x => x?.GetValueKind() == JsonValueKind.String ? (string)x! : x?.ToJsonString() ?? "").ToArray(),
            _ => Str(name)?.Split([',', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
        };
    }

    public JsonObject? Obj(string name)
    {
        var v = Get(name);
        return v switch
        {
            null => null,
            JsonObject o => o,
            JsonValue j when j.GetValueKind() == JsonValueKind.String =>
                McpServer.TryParseObject((string)j!) ?? throw new ToolException($"'{name}' must be a JSON object, e.g. {{\"path\": \"$.id\"}}"),
            _ => throw new ToolException($"'{name}' must be a JSON object"),
        };
    }
}

/// <summary>Tiny JSON Schema builder for tool input schemas.</summary>
public static class Schema
{
    public static JsonObject Object(params (string Name, JsonObject Schema, bool Required)[] props)
    {
        var properties = new JsonObject();
        foreach (var (n, s, _) in props) properties[n] = s;
        var o = new JsonObject { ["type"] = "object", ["properties"] = properties };
        var req = props.Where(p => p.Required).Select(p => (JsonNode)p.Name).ToArray();
        if (req.Length > 0) o["required"] = new JsonArray(req);
        return o;
    }

    public static JsonObject String(string description, string[]? @enum = null, string? @default = null)
    {
        var o = new JsonObject { ["type"] = "string", ["description"] = description };
        if (@enum is not null) o["enum"] = new JsonArray(@enum.Select(e => (JsonNode)e).ToArray());
        if (@default is not null) o["default"] = @default;
        return o;
    }

    public static JsonObject Integer(string description, int? @default = null, int? min = null, int? max = null)
    {
        var o = new JsonObject { ["type"] = "integer", ["description"] = description };
        if (@default is not null) o["default"] = @default;
        if (min is not null) o["minimum"] = min;
        if (max is not null) o["maximum"] = max;
        return o;
    }

    public static JsonObject Number(string description) => new() { ["type"] = "number", ["description"] = description };

    public static JsonObject Boolean(string description, bool? @default = null)
    {
        var o = new JsonObject { ["type"] = "boolean", ["description"] = description };
        if (@default is not null) o["default"] = @default;
        return o;
    }

    public static JsonObject StringArray(string description) =>
        new() { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = description };

    public static JsonObject AnyObject(string description) =>
        new() { ["type"] = "object", ["description"] = description, ["additionalProperties"] = true };
}
