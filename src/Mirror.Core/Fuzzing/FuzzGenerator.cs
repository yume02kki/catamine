using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mirror.Core.Paths;

namespace Mirror.Core.Fuzzing;

public sealed record FuzzSeed(string Method, string Path, string? Query, Dictionary<string, string>? Headers, string? Body);

public sealed record FuzzOptions
{
    public int Volume { get; init; } = 500;
    public int HugeStringLength { get; init; } = 64 * 1024;
    public int MaxFields { get; init; } = 200;
    public int Seed { get; init; } = 42;
    /// <summary>Mutation families to use; empty = all.</summary>
    public string[] Mutations { get; init; } = [];
}

public sealed record FuzzCase(string Mutation, string Target, string Method, string Path, string? Query, Dictionary<string, string> Headers, string? Body)
{
    public string PathAndQuery => string.IsNullOrEmpty(Query) ? Path : $"{Path}?{Query.TrimStart('?')}";
}

/// <summary>
/// Generates synthetic variants of a request: every JSON field and query parameter is mutated
/// (nulls, empty/huge strings, boundary numbers, wrong types, missing and extra fields).
/// </summary>
public static class FuzzGenerator
{
    public static readonly string[] AllMutations =
        ["null", "empty", "huge_string", "boundary_number", "wrong_type", "missing", "extra_field", "empty_array", "special_chars"];

    private static readonly JsonNode[] BoundaryNumbers =
    [
        JsonValue.Create(0), JsonValue.Create(-1), JsonValue.Create(int.MaxValue), JsonValue.Create(int.MinValue),
        JsonValue.Create(long.MaxValue), JsonValue.Create(9007199254740993L), JsonValue.Create(1e308), JsonValue.Create(-1e308),
        JsonValue.Create(0.0000001),
    ];

    private static readonly string[] SpecialStrings = ["' OR '1'='1", "<script>alert(1)</script>", "../../etc/passwd", "ü💥\u0000‮", "%00%ff", "  "];

    public static IEnumerable<FuzzCase> Generate(FuzzSeed seed, FuzzOptions? options = null)
    {
        options ??= new FuzzOptions();
        var enabled = options.Mutations.Length == 0 ? AllMutations.ToHashSet() : options.Mutations.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var singles = Singles(seed, options, enabled).Take(options.Volume).ToList();
        foreach (var c in singles) yield return c;
        if (singles.Count == 0 || singles.Count >= options.Volume) yield break;

        // Not enough single mutations to reach the requested volume: combine two random ones on the body.
        var rnd = new Random(options.Seed);
        var bodyCases = singles.Where(c => c.Body is not null && c.Body != seed.Body).ToList();
        for (var produced = singles.Count; produced < options.Volume && bodyCases.Count > 1; produced++)
        {
            var a = bodyCases[rnd.Next(bodyCases.Count)];
            var b = bodyCases[rnd.Next(bodyCases.Count)];
            var combined = CombineBodies(seed.Body, a, b);
            yield return a with { Mutation = $"{a.Mutation}+{b.Mutation}", Target = $"{a.Target} & {b.Target}", Body = combined ?? a.Body };
        }
    }

    private static IEnumerable<FuzzCase> Singles(FuzzSeed seed, FuzzOptions o, HashSet<string> enabled)
    {
        var headers = seed.Headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var huge = new string('x', o.HugeStringLength);

        // Query parameters.
        var query = ParseQuery(seed.Query);
        for (var i = 0; i < query.Count; i++)
        {
            var (name, value) = query[i];
            var target = $"query.{name}";
            IEnumerable<(string, string?)> variants = new List<(string, string?)>
            {
                ("missing", null), ("empty", ""), ("huge_string", huge), ("boundary_number", "-1"), ("boundary_number", "2147483648"),
                ("wrong_type", int.TryParse(value, out _) ? "abc" : "12345"),
            }.Concat(SpecialStrings.Select(s => ("special_chars", (string?)s)));
            foreach (var (mutation, v) in variants)
            {
                if (!enabled.Contains(mutation)) continue;
                var q = query.ToList();
                if (v is null) q.RemoveAt(i); else q[i] = (name, v);
                yield return new FuzzCase(mutation, target, seed.Method, seed.Path, BuildQuery(q), headers, seed.Body);
            }
        }
        if (enabled.Contains("extra_field") && query.Count > 0)
            yield return new FuzzCase("extra_field", "query", seed.Method, seed.Path, BuildQuery([.. query, ("__fuzz", "1")]), headers, seed.Body);

        // JSON body fields.
        if (string.IsNullOrWhiteSpace(seed.Body)) yield break;
        JsonNode? root;
        try { root = JsonNode.Parse(seed.Body); }
        catch (JsonException) { yield break; }
        if (root is null) yield break;

        if (enabled.Contains("empty")) yield return Case("empty", "$", "");
        foreach (var (path, node) in Fields(root).Take(o.MaxFields))
        {
            var target = ToPathString(path);
            var kind = node is null ? JsonValueKind.Null : node.GetValueKind();
            var candidates = new List<(string Mutation, Func<JsonNode?> Value)>();
            if (path.Count > 0)
            {
                candidates.Add(("null", () => null));
                candidates.Add(("missing", () => MissingMarker));
            }
            switch (kind)
            {
                case JsonValueKind.String:
                    candidates.Add(("empty", () => JsonValue.Create("")));
                    candidates.Add(("huge_string", () => JsonValue.Create(huge)));
                    candidates.Add(("wrong_type", () => JsonValue.Create(12345)));
                    candidates.AddRange(SpecialStrings.Select(s => ("special_chars", (Func<JsonNode?>)(() => JsonValue.Create(s)))));
                    break;
                case JsonValueKind.Number:
                    candidates.AddRange(BoundaryNumbers.Select(n => ("boundary_number", (Func<JsonNode?>)(() => n.DeepClone()))));
                    candidates.Add(("wrong_type", () => JsonValue.Create(node!.ToJsonString())));
                    break;
                case JsonValueKind.True or JsonValueKind.False:
                    candidates.Add(("wrong_type", () => JsonValue.Create(kind == JsonValueKind.True ? "true" : "false")));
                    candidates.Add(("wrong_type", () => JsonValue.Create(kind == JsonValueKind.True ? 1 : 0)));
                    break;
                case JsonValueKind.Array:
                    candidates.Add(("empty_array", () => new JsonArray()));
                    candidates.Add(("wrong_type", () => new JsonObject()));
                    break;
                case JsonValueKind.Object:
                    candidates.Add(("wrong_type", () => new JsonArray()));
                    candidates.Add(("extra_field", () => Extra(node!)));
                    break;
            }
            foreach (var (mutation, make) in candidates)
            {
                if (!enabled.Contains(mutation)) continue;
                var copy = root.DeepClone();
                if (!Replace(ref copy, path, make())) continue;
                yield return Case(mutation, target, copy?.ToJsonString() ?? "null");
            }
        }

        FuzzCase Case(string mutation, string target, string body) =>
            new(mutation, target, seed.Method, seed.Path, seed.Query, headers, body);
    }

    private static readonly JsonObject MissingMarker = new();
    private static bool IsMissing(JsonNode? v) => ReferenceEquals(v, MissingMarker);

    private static JsonNode Extra(JsonNode node)
    {
        var copy = node.DeepClone().AsObject();
        copy["__fuzz_extra"] = "unexpected";
        return copy;
    }

    private static bool Replace(ref JsonNode? root, IReadOnlyList<PathSegment> path, JsonNode? value)
    {
        if (path.Count == 0) { root = IsMissing(value) ? null : value; return true; }
        var parent = root;
        for (var i = 0; i < path.Count - 1; i++)
        {
            parent = path[i].Kind == SegmentKind.Property ? parent?[path[i].Name!] : parent?[path[i].Index];
            if (parent is null) return false;
        }
        var last = path[^1];
        if (last.Kind == SegmentKind.Property && parent is JsonObject o)
        {
            if (IsMissing(value)) o.Remove(last.Name!);
            else o[last.Name!] = value;
            return true;
        }
        if (last.Kind == SegmentKind.Index && parent is JsonArray a && last.Index < a.Count)
        {
            if (IsMissing(value)) a.RemoveAt(last.Index);
            else a[last.Index] = value;
            return true;
        }
        return false;
    }

    /// <summary>Every node with its path; arrays contribute only their first element to keep the case count bounded.</summary>
    private static IEnumerable<(List<PathSegment> Path, JsonNode? Node)> Fields(JsonNode root)
    {
        var stack = new Stack<(List<PathSegment>, JsonNode?)>();
        stack.Push(([], root));
        while (stack.Count > 0)
        {
            var (path, node) = stack.Pop();
            yield return (path, node);
            switch (node)
            {
                case JsonObject o:
                    foreach (var (k, v) in o.Reverse()) stack.Push(([.. path, PathSegment.Prop(k)], v));
                    break;
                case JsonArray a when a.Count > 0:
                    stack.Push(([.. path, PathSegment.Idx(0)], a[0]));
                    break;
            }
        }
    }

    private static string? CombineBodies(string? seedBody, FuzzCase a, FuzzCase b)
    {
        // Applying two independent mutations textually is unreliable; re-parse and merge top-level keys of b into a.
        try
        {
            if (JsonNode.Parse(a.Body!) is JsonObject ao && JsonNode.Parse(b.Body!) is JsonObject bo && seedBody is not null && JsonNode.Parse(seedBody) is JsonObject so)
            {
                foreach (var (k, v) in bo.ToList())
                    if (!so.TryGetPropertyValue(k, out var sv) || !JsonNode.DeepEquals(sv, v)) ao[k] = v?.DeepClone();
                foreach (var (k, _) in so.ToList())
                    if (!bo.ContainsKey(k)) ao.Remove(k);
                return ao.ToJsonString();
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static string ToPathString(IReadOnlyList<PathSegment> path)
    {
        var n = PathNode.Root;
        foreach (var s in path) n = n.Child(s);
        return n.ToString();
    }

    internal static List<(string Name, string Value)> ParseQuery(string? query)
    {
        var list = new List<(string, string)>();
        if (string.IsNullOrEmpty(query)) return list;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            var name = Uri.UnescapeDataString(eq < 0 ? part : part[..eq]);
            var value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            list.Add((name, value));
        }
        return list;
    }

    internal static string BuildQuery(IEnumerable<(string Name, string Value)> items)
    {
        var sb = new StringBuilder();
        foreach (var (n, v) in items)
        {
            if (sb.Length > 0) sb.Append('&');
            sb.Append(Uri.EscapeDataString(n)).Append('=').Append(Uri.EscapeDataString(v));
        }
        return sb.ToString();
    }
}
