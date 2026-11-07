using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mirror.Core.Paths;

namespace Mirror.Core.Diffing;

/// <summary>Accumulates differences with a hard cap so a pathological response cannot exhaust memory.</summary>
public sealed class DiffCollector
{
    private readonly DiffSettings _settings;
    private readonly HashSet<string> _signatureParts = new(StringComparer.Ordinal);

    public List<Difference> Differences { get; } = [];
    public DiffSummary Summary { get; } = new();
    public DiffKinds Kinds { get; private set; }
    public int Total { get; private set; }
    public bool Truncated => Total > Differences.Count;

    public DiffCollector(DiffSettings settings) => _settings = settings;

    public void Add(DiffKind kind, PathNode path, JsonNode? main, JsonNode? dev, bool mainPresent = true, bool devPresent = true)
    {
        if (Differences.Count >= _settings.MaxDifferences) { Count(kind, null, path); return; }
        Add(kind, path.ToString(), path.Normalized(),
            mainPresent ? Snippet(main) : null,
            devPresent ? Snippet(dev) : null);
    }

    public void Add(DiffKind kind, string path, string normalizedPath, string? main, string? dev)
    {
        var d = new Difference(kind.ToWire(), path, main, dev) { KindEnum = kind, NormalizedPath = normalizedPath };
        if (Differences.Count < _settings.MaxDifferences) Differences.Add(d);
        Count(kind, d, null);
    }

    public void ArrayLength(PathNode path, int main, int dev)
    {
        var p = path.ToString();
        if (Summary.Arrays.Count < 10) Summary.Arrays.Add(new ArrayLengthChange(p, main, dev));
        Add(DiffKind.ArrayLength, p, path.Normalized(), main.ToString(CultureInfo.InvariantCulture), dev.ToString(CultureInfo.InvariantCulture));
    }

    private void Count(DiffKind kind, Difference? d, PathNode? path)
    {
        Total++;
        Kinds |= kind.ToFlag();
        switch (kind)
        {
            case DiffKind.Changed: Summary.Changed++; break;
            case DiffKind.Added: Summary.Added++; break;
            case DiffKind.Removed: Summary.Removed++; break;
            case DiffKind.TypeChanged: Summary.TypeChanged++; break;
            case DiffKind.Order: Summary.Order++; break;
            case DiffKind.Header when d is not null: Summary.Headers.Add(d.Path["header:".Length..]); break;
        }
        if (_signatureParts.Count < 256)
            _signatureParts.Add(kind.ToWire() + ":" + (d?.NormalizedPath ?? path?.Normalized() ?? ""));
        if (d is not null && Summary.Top.Count < _settings.SummaryTopCount && kind != DiffKind.ArrayLength)
            Summary.Top.Add(d with { Main = Shorten(d.Main, 80), Dev = Shorten(d.Dev, 80) });
    }

    public long ComputeSignature()
    {
        if (_signatureParts.Count == 0) return 0;
        // FNV-1a 64 over the sorted set: stable across processes and versions.
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        var hash = offset;
        foreach (var part in _signatureParts.Order(StringComparer.Ordinal))
        {
            foreach (var b in Encoding.UTF8.GetBytes(part)) { hash ^= b; hash *= prime; }
            hash ^= 0x1F; hash *= prime;
        }
        return unchecked((long)hash);
    }

    private string Snippet(JsonNode? node)
    {
        if (node is null) return "null";
        var s = node.ToJsonString();
        return Shorten(s, _settings.ValueSnippetLength)!;
    }

    internal static string? Shorten(string? s, int max) =>
        s is null || s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "…");
}

/// <summary>Structural JSON comparison driven by <see cref="CompareOptions"/>.</summary>
public static class JsonComparer
{
    private struct NodeDirectives
    {
        public bool Ignore;
        public bool IgnoreOrder;
        public bool Coerce;
        public PrimaryKeyDirective? Key;
        public ToleranceDirective? Tolerance;
    }

    public static void Compare(JsonNode? main, JsonNode? dev, CompareOptions options, DiffCollector collector)
    {
        var m = options.Matcher;
        Compare(main, dev, PathNode.Root, m.Root, m, collector);
    }

    private static NodeDirectives Resolve(PathMatcher<CompareDirective> m, MatchState st)
    {
        var d = new NodeDirectives();
        if (!st.HasMatches) return d;
        foreach (var x in m.MatchesAt(st))
        {
            switch (x)
            {
                case IgnoreDirective: d.Ignore = true; break;
                case IgnoreOrderDirective: d.IgnoreOrder = true; break;
                case TypeCoercionDirective: d.Coerce = true; break;
                case PrimaryKeyDirective k: d.Key ??= k; break;
                case ToleranceDirective t: d.Tolerance ??= t; break;
            }
        }
        return d;
    }

    private static bool IsIgnored(PathMatcher<CompareDirective> m, MatchState st) =>
        st.HasMatches && m.AnyMatch(st, x => x is IgnoreDirective);

    private static JsonValueKind KindOf(JsonNode? n) => n is null ? JsonValueKind.Null : n.GetValueKind();

    private static void Compare(JsonNode? a, JsonNode? b, PathNode path, MatchState st, PathMatcher<CompareDirective> m, DiffCollector c)
    {
        var d = Resolve(m, st);
        if (d.Ignore) return;

        var ka = KindOf(a);
        var kb = KindOf(b);

        if (ka == JsonValueKind.Object && kb == JsonValueKind.Object)
        {
            CompareObjects(a!.AsObject(), b!.AsObject(), path, st, m, c);
            return;
        }
        if (ka == JsonValueKind.Array && kb == JsonValueKind.Array)
        {
            CompareArrays(a!.AsArray(), b!.AsArray(), path, st, m, c, d);
            return;
        }
        if (IsScalar(ka) && IsScalar(kb))
        {
            if (ScalarEquals(a, b, ka, kb, d)) return;
            c.Add(SameFamily(ka, kb) ? DiffKind.Changed : DiffKind.TypeChanged, path, a, b);
            return;
        }
        c.Add(DiffKind.TypeChanged, path, a, b);
    }

    private static void CompareObjects(JsonObject a, JsonObject b, PathNode path, MatchState st, PathMatcher<CompareDirective> m, DiffCollector c)
    {
        foreach (var (key, av) in a)
        {
            var seg = PathSegment.Prop(key);
            var cs = m.Step(st, seg);
            if (b.TryGetPropertyValue(key, out var bv))
                Compare(av, bv, path.Child(seg), cs, m, c);
            else if (!IsIgnored(m, cs))
                c.Add(DiffKind.Removed, path.Child(seg), av, null, devPresent: false);
        }
        foreach (var (key, bv) in b)
        {
            if (a.ContainsKey(key)) continue;
            var seg = PathSegment.Prop(key);
            var cs = m.Step(st, seg);
            if (!IsIgnored(m, cs))
                c.Add(DiffKind.Added, path.Child(seg), null, bv, mainPresent: false);
        }
    }

    private static void CompareArrays(JsonArray a, JsonArray b, PathNode path, MatchState st, PathMatcher<CompareDirective> m, DiffCollector c, NodeDirectives d)
    {
        if (a.Count != b.Count) c.ArrayLength(path, a.Count, b.Count);

        if (d.Key is not null) CompareKeyed(a, b, path, st, m, c, d.Key, d.IgnoreOrder);
        else if (d.IgnoreOrder) CompareAsSet(a, b, path, st, m, c);
        else CompareByIndex(a, b, path, st, m, c);
    }

    private static void CompareByIndex(JsonArray a, JsonArray b, PathNode path, MatchState st, PathMatcher<CompareDirective> m, DiffCollector c)
    {
        var common = Math.Min(a.Count, b.Count);
        for (var i = 0; i < common; i++)
        {
            var seg = PathSegment.Idx(i);
            Compare(a[i], b[i], path.Child(seg), m.Step(st, seg), m, c);
        }
        for (var i = common; i < a.Count; i++) AddElement(DiffKind.Removed, a[i], PathSegment.Idx(i), path, st, m, c);
        for (var i = common; i < b.Count; i++) AddElement(DiffKind.Added, b[i], PathSegment.Idx(i), path, st, m, c);
    }

    private static void AddElement(DiffKind kind, JsonNode? node, PathSegment seg, PathNode path, MatchState st, PathMatcher<CompareDirective> m, DiffCollector c)
    {
        if (IsIgnored(m, m.Step(st, seg))) return;
        if (kind == DiffKind.Removed) c.Add(kind, path.Child(seg), node, null, devPresent: false);
        else c.Add(kind, path.Child(seg), null, node, mainPresent: false);
    }

    private static void CompareKeyed(JsonArray a, JsonArray b, PathNode path, MatchState st, PathMatcher<CompareDirective> m, DiffCollector c, PrimaryKeyDirective key, bool ignoreOrder)
    {
        var (aMap, aOrder, aUnkeyed) = Index(a, key);
        var (bMap, bOrder, bUnkeyed) = Index(b, key);

        var commonA = new List<string>();
        foreach (var k in aOrder)
        {
            var i = aMap[k];
            var seg = PathSegment.Keyed(key.Label, k, i);
            if (bMap.TryGetValue(k, out var j))
            {
                commonA.Add(k);
                Compare(a[i], b[j], path.Child(seg), m.Step(st, seg), m, c);
            }
            else AddElement(DiffKind.Removed, a[i], seg, path, st, m, c);
        }
        foreach (var k in bOrder)
        {
            if (aMap.ContainsKey(k)) continue;
            AddElement(DiffKind.Added, b[bMap[k]], PathSegment.Keyed(key.Label, k, bMap[k]), path, st, m, c);
        }

        if (!ignoreOrder && commonA.Count > 1)
        {
            var commonB = bOrder.Where(aMap.ContainsKey).ToList();
            var moved = 0;
            for (var i = 0; i < commonA.Count; i++) if (!string.Equals(commonA[i], commonB[i], StringComparison.Ordinal)) moved++;
            if (moved > 0)
                c.Add(DiffKind.Order, path.ToString(), path.Normalized(),
                    $"{moved} of {commonA.Count} items in a different position", string.Join(",", commonB.Take(5)) + (commonB.Count > 5 ? ",…" : ""));
        }

        // Elements without a usable key (missing or duplicate) fall back to positional comparison.
        var n = Math.Min(aUnkeyed.Count, bUnkeyed.Count);
        for (var x = 0; x < n; x++)
        {
            var seg = PathSegment.Idx(aUnkeyed[x]);
            Compare(a[aUnkeyed[x]], b[bUnkeyed[x]], path.Child(seg), m.Step(st, seg), m, c);
        }
        for (var x = n; x < aUnkeyed.Count; x++) AddElement(DiffKind.Removed, a[aUnkeyed[x]], PathSegment.Idx(aUnkeyed[x]), path, st, m, c);
        for (var x = n; x < bUnkeyed.Count; x++) AddElement(DiffKind.Added, b[bUnkeyed[x]], PathSegment.Idx(bUnkeyed[x]), path, st, m, c);
    }

    private static (Dictionary<string, int> map, List<string> order, List<int> unkeyed) Index(JsonArray arr, PrimaryKeyDirective key)
    {
        var map = new Dictionary<string, int>(arr.Count, StringComparer.Ordinal);
        var order = new List<string>(arr.Count);
        var unkeyed = new List<int>();
        var dupes = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < arr.Count; i++)
        {
            var k = KeyOf(arr[i], key.KeyPaths);
            if (k is null) { unkeyed.Add(i); continue; }
            if (!map.TryAdd(k, i)) dupes.Add(k);
            else order.Add(k);
        }
        if (dupes.Count > 0)
        {
            // Keys that are not unique cannot identify items: compare all of them positionally.
            for (var i = 0; i < arr.Count; i++)
            {
                var k = KeyOf(arr[i], key.KeyPaths);
                if (k is not null && dupes.Contains(k)) unkeyed.Add(i);
            }
            foreach (var k in dupes) map.Remove(k);
            order.RemoveAll(dupes.Contains);
            unkeyed.Sort();
        }
        return (map, order, unkeyed);
    }

    internal static string? KeyOf(JsonNode? element, IReadOnlyList<JsonPathPattern> keyPaths)
    {
        if (element is not JsonObject) return null;
        string? result = null;
        foreach (var kp in keyPaths)
        {
            JsonNode? cur = element;
            foreach (var name in kp.PropertyChain)
            {
                if (cur is JsonObject o && o.TryGetPropertyValue(name, out var next)) cur = next;
                else return null;
            }
            if (cur is null) return null;
            var part = cur.GetValueKind() == JsonValueKind.String ? cur.GetValue<string>() : cur.ToJsonString();
            result = result is null ? part : result + "|" + part;
        }
        return result;
    }

    private static void CompareAsSet(JsonArray a, JsonArray b, PathNode path, MatchState st, PathMatcher<CompareDirective> m, DiffCollector c)
    {
        var pool = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (var j = 0; j < b.Count; j++)
        {
            var key = Canonical(b[j], m.Step(st, PathSegment.Idx(j)), m);
            if (!pool.TryGetValue(key, out var q)) pool[key] = q = new Queue<int>();
            q.Enqueue(j);
        }
        var leftA = new List<int>();
        for (var i = 0; i < a.Count; i++)
        {
            var key = Canonical(a[i], m.Step(st, PathSegment.Idx(i)), m);
            if (pool.TryGetValue(key, out var q) && q.Count > 0) q.Dequeue();
            else leftA.Add(i);
        }
        var leftB = pool.Values.SelectMany(q => q).Order().ToList();

        var n = Math.Min(leftA.Count, leftB.Count);
        for (var x = 0; x < n; x++)
        {
            var seg = PathSegment.Idx(leftA[x]);
            Compare(a[leftA[x]], b[leftB[x]], path.Child(seg), m.Step(st, seg), m, c);
        }
        for (var x = n; x < leftA.Count; x++) AddElement(DiffKind.Removed, a[leftA[x]], PathSegment.Idx(leftA[x]), path, st, m, c);
        for (var x = n; x < leftB.Count; x++) AddElement(DiffKind.Added, b[leftB[x]], PathSegment.Idx(leftB[x]), path, st, m, c);
    }

    /// <summary>Order-insensitive canonical text of a subtree, skipping ignored fields.</summary>
    private static string Canonical(JsonNode? node, MatchState st, PathMatcher<CompareDirective> m)
    {
        var sb = new StringBuilder();
        WriteCanonical(node, st, m, sb);
        return sb.ToString();
    }

    private static void WriteCanonical(JsonNode? node, MatchState st, PathMatcher<CompareDirective> m, StringBuilder sb)
    {
        switch (KindOf(node))
        {
            case JsonValueKind.Object:
                sb.Append('{');
                foreach (var (k, v) in node!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    var cs = m.Step(st, PathSegment.Prop(k));
                    if (IsIgnored(m, cs)) continue;
                    sb.Append(JsonSerializer.Serialize(k)).Append(':');
                    WriteCanonical(v, cs, m, sb);
                    sb.Append(',');
                }
                sb.Append('}');
                break;
            case JsonValueKind.Array:
                sb.Append('[');
                var arr = node!.AsArray();
                for (var i = 0; i < arr.Count; i++)
                {
                    WriteCanonical(arr[i], m.Step(st, PathSegment.Idx(i)), m, sb);
                    sb.Append(',');
                }
                sb.Append(']');
                break;
            case JsonValueKind.Number:
                sb.Append(TryNumber(node!, out var dec, out var dbl) ? (dec?.ToString(CultureInfo.InvariantCulture) ?? dbl.ToString("R", CultureInfo.InvariantCulture)) : node!.ToJsonString());
                break;
            default:
                sb.Append(node?.ToJsonString() ?? "null");
                break;
        }
    }

    private static bool IsScalar(JsonValueKind k) => k is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;

    private static bool SameFamily(JsonValueKind a, JsonValueKind b) =>
        a == b || (a is JsonValueKind.True or JsonValueKind.False && b is JsonValueKind.True or JsonValueKind.False);

    private static bool ScalarEquals(JsonNode? a, JsonNode? b, JsonValueKind ka, JsonValueKind kb, NodeDirectives d)
    {
        if (ka == JsonValueKind.Null || kb == JsonValueKind.Null) return ka == kb;
        if (ka == JsonValueKind.Number && kb == JsonValueKind.Number) return NumbersEqual(a!, b!, d.Tolerance);
        if (ka == JsonValueKind.String && kb == JsonValueKind.String)
            return string.Equals(a!.GetValue<string>(), b!.GetValue<string>(), StringComparison.Ordinal);
        if (ka is JsonValueKind.True or JsonValueKind.False && kb is JsonValueKind.True or JsonValueKind.False) return ka == kb;

        if (!d.Coerce) return false;
        // Type coercion: "1" == 1, "true" == true.
        if (ka == JsonValueKind.String && kb == JsonValueKind.Number) return StringEqualsNumber(a!.GetValue<string>(), b!, d.Tolerance);
        if (ka == JsonValueKind.Number && kb == JsonValueKind.String) return StringEqualsNumber(b!.GetValue<string>(), a!, d.Tolerance);
        if (ka == JsonValueKind.String && kb is JsonValueKind.True or JsonValueKind.False)
            return bool.TryParse(a!.GetValue<string>().Trim(), out var x) && x == (kb == JsonValueKind.True);
        if (kb == JsonValueKind.String && ka is JsonValueKind.True or JsonValueKind.False)
            return bool.TryParse(b!.GetValue<string>().Trim(), out var y) && y == (ka == JsonValueKind.True);
        return false;
    }

    private static bool StringEqualsNumber(string s, JsonNode number, ToleranceDirective? tol)
    {
        if (!decimal.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sd))
            return double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sdd) &&
                   TryNumber(number, out _, out var nd) && DoubleEquals(sdd, nd, tol);
        return TryNumber(number, out var dec, out var dbl) && (dec is { } dv ? DecimalEquals(sd, dv, tol) : DoubleEquals((double)sd, dbl, tol));
    }

    private static bool NumbersEqual(JsonNode a, JsonNode b, ToleranceDirective? tol)
    {
        TryNumber(a, out var da, out var fa);
        TryNumber(b, out var db, out var fb);
        if (da is { } x && db is { } y) return DecimalEquals(x, y, tol);
        return DoubleEquals(da is { } xa ? (double)xa : fa, db is { } yb ? (double)yb : fb, tol);
    }

    private static bool DecimalEquals(decimal x, decimal y, ToleranceDirective? tol)
    {
        if (x == y) return true;
        if (tol is null) return false;
        var diff = Math.Abs(x - y);
        if (!tol.Relative) return (double)diff <= tol.Tolerance + 1e-12;
        var scale = Math.Max(Math.Abs(x), Math.Abs(y));
        return (double)diff <= tol.Tolerance * (double)scale + 1e-12;
    }

    private static bool DoubleEquals(double x, double y, ToleranceDirective? tol)
    {
        if (x.Equals(y)) return true;
        if (tol is null) return false;
        var diff = Math.Abs(x - y);
        return tol.Relative ? diff <= tol.Tolerance * Math.Max(Math.Abs(x), Math.Abs(y)) : diff <= tol.Tolerance + 1e-12;
    }

    internal static bool TryNumber(JsonNode n, out decimal? dec, out double dbl)
    {
        dec = null;
        dbl = double.NaN;
        if (n is not JsonValue v) return false;
        if (v.TryGetValue<decimal>(out var d)) { dec = d; dbl = (double)d; return true; }
        if (v.TryGetValue<double>(out var f)) { dbl = f; return true; }
        if (v.TryGetValue<JsonElement>(out var el) && el.ValueKind == JsonValueKind.Number)
        {
            if (el.TryGetDecimal(out d)) { dec = d; dbl = (double)d; return true; }
            if (el.TryGetDouble(out f)) { dbl = f; return true; }
        }
        return double.TryParse(n.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out dbl);
    }
}
