using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mirror.Core.Rules;

public sealed record RuleTypeInfo(string Type, string Description, Type ConfigType, JsonElement ExampleConfig);

/// <summary>
/// Registry of rule types. To add a rule type: write the class and its config record, then add one
/// <see cref="Register{TConfig}"/> line in the static constructor (or call it at startup from another assembly).
/// </summary>
public static class RuleRegistry
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private sealed record Entry(RuleTypeInfo Info, Func<JsonElement, IDiffRule> Create, Func<JsonElement, IReadOnlyList<string>> Validate);

    private static readonly Dictionary<string, Entry> Entries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    static RuleRegistry()
    {
        Register<PrimaryKeyConfig>("PrimaryKey",
            "Match array items by a key field (JSON path relative to the item) instead of by index. Works for nested arrays.",
            c => new PrimaryKeyRule(c), PrimaryKeyRule.Validate,
            new PrimaryKeyConfig("$.items", "id", null));
        Register<IgnoreFieldConfig>("IgnoreField",
            "Exclude fields from comparison. Wildcards ([*], .*, ..name) allowed.",
            c => new IgnoreFieldRule(c), IgnoreFieldRule.Validate,
            new IgnoreFieldConfig(null, ["$..requestId"]));
        Register<SearchAndReplaceConfig>("SearchAndReplace",
            "Replace text in one field or across the whole response, as plain text or regex.",
            c => new SearchAndReplaceRule(c), SearchAndReplaceRule.Validate,
            new SearchAndReplaceConfig(SearchScope.Body, null, "dev.example.com", "prod.example.com", false, false));
        Register<IgnoreOrderConfig>("IgnoreOrder",
            "Compare arrays at the path as sets (order ignored).",
            c => new IgnoreOrderRule(c), IgnoreOrderRule.Validate,
            new IgnoreOrderConfig("$.tags"));
        Register<NumericToleranceConfig>("NumericTolerance",
            "Treat numbers within ± tolerance as equal (absolute, or relative as a fraction e.g. 0.01 = 1%).",
            c => new NumericToleranceRule(c), NumericToleranceRule.Validate,
            new NumericToleranceConfig("$..price", 0.01, ToleranceMode.Absolute));
        Register<IgnoreHeaderConfig>("IgnoreHeader",
            "Do not compare these response headers.",
            c => new IgnoreHeaderRule(c), IgnoreHeaderRule.Validate,
            new IgnoreHeaderConfig(["date", "x-request-id"]));
        Register<TypeCoercionConfig>("TypeCoercion",
            "Treat strings and numbers/booleans with the same value as equal (\"1\" == 1). Path optional (default: everywhere).",
            c => new TypeCoercionRule(c), TypeCoercionRule.Validate,
            new TypeCoercionConfig("$.total"));
    }

    public static void Register<TConfig>(string type, string description, Func<TConfig, IDiffRule> factory,
        Func<TConfig, IEnumerable<string>>? validate, TConfig example)
    {
        TConfig Deserialize(JsonElement e)
        {
            try
            {
                return e.Deserialize<TConfig>(JsonOptions) ?? throw new RuleConfigException("config is null");
            }
            catch (JsonException ex)
            {
                throw new RuleConfigException($"invalid config for {type}: {ex.Message}");
            }
        }

        var entry = new Entry(
            new RuleTypeInfo(type, description, typeof(TConfig), JsonSerializer.SerializeToElement(example, JsonOptions)),
            e =>
            {
                var cfg = Deserialize(e);
                var errors = validate?.Invoke(cfg).ToList() ?? [];
                if (errors.Count > 0) throw new RuleConfigException($"{type}: {string.Join("; ", errors)}");
                return factory(cfg);
            },
            e =>
            {
                try
                {
                    var cfg = Deserialize(e);
                    return validate?.Invoke(cfg).ToList() ?? [];
                }
                catch (RuleConfigException ex) { return [ex.Message]; }
            });
        lock (Gate) Entries[type] = entry;
    }

    public static IReadOnlyCollection<RuleTypeInfo> Types
    {
        get { lock (Gate) return Entries.Values.Select(e => e.Info).ToList(); }
    }

    public static bool IsKnown(string type)
    {
        lock (Gate) return Entries.ContainsKey(type);
    }

    public static IDiffRule Create(string type, JsonElement config)
    {
        Entry? e;
        lock (Gate) Entries.TryGetValue(type, out e);
        if (e is null) throw new RuleConfigException($"unknown rule type '{type}'");
        return e.Create(config);
    }

    public static IReadOnlyList<string> Validate(string type, JsonElement config)
    {
        Entry? e;
        lock (Gate) Entries.TryGetValue(type, out e);
        return e is null ? [$"unknown rule type '{type}'"] : e.Validate(config);
    }

    public static string CanonicalName(string type)
    {
        lock (Gate) return Entries.TryGetValue(type, out var e) ? e.Info.Type : type;
    }
}
