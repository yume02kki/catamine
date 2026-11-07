using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mirror.Core.Diffing;
using Mirror.Core.Paths;

namespace Mirror.Core.Rules;

internal static class RuleValidation
{
    public static IEnumerable<string> Path(string? path, string field)
    {
        if (string.IsNullOrWhiteSpace(path)) { yield return $"{field} is required"; yield break; }
        if (!JsonPathPattern.TryParse(path, out _, out var err)) yield return $"{field}: {err}";
    }
}

// ───────────────────────────── PrimaryKey ─────────────────────────────

/// <param name="Path">Path of the array(s), e.g. <c>$.items</c> or <c>$.orders[*].lines</c>.</param>
/// <param name="Key">Key field relative to each item, e.g. <c>id</c> or <c>meta.id</c>.</param>
/// <param name="Keys">Composite key (alternative to <paramref name="Key"/>).</param>
public sealed record PrimaryKeyConfig(string Path, string? Key, string[]? Keys);

public sealed class PrimaryKeyRule : IDiffRule
{
    private readonly JsonPathPattern _path;
    private readonly PrimaryKeyDirective _directive;

    public PrimaryKeyRule(PrimaryKeyConfig c)
    {
        _path = JsonPathPattern.Parse(c.Path);
        var keys = (c.Keys is { Length: > 0 } ? c.Keys : [c.Key!]).Select(JsonPathPattern.Parse).ToList();
        _directive = new PrimaryKeyDirective(keys);
    }

    public string Type => "PrimaryKey";
    public void Configure(CompareOptionsBuilder options) => options.Add(_path, _directive);

    public static IEnumerable<string> Validate(PrimaryKeyConfig c)
    {
        foreach (var e in RuleValidation.Path(c.Path, "path")) yield return e;
        var keys = c.Keys is { Length: > 0 } ? c.Keys : c.Key is null ? [] : [c.Key];
        if (keys.Length == 0) yield return "key is required";
        foreach (var k in keys)
        {
            if (!JsonPathPattern.TryParse(k, out var p, out var err)) { yield return $"key '{k}': {err}"; continue; }
            if (!p.IsSimplePropertyChain || p.Length == 0) yield return $"key '{k}' must be a plain field path like 'id' or 'meta.id'";
        }
    }
}

// ───────────────────────────── IgnoreField ─────────────────────────────

public sealed record IgnoreFieldConfig(string? Path, string[]? Paths);

public sealed class IgnoreFieldRule : IDiffRule
{
    private readonly List<JsonPathPattern> _paths;

    public IgnoreFieldRule(IgnoreFieldConfig c) => _paths = AllPaths(c).Select(JsonPathPattern.Parse).ToList();

    public string Type => "IgnoreField";

    public void Configure(CompareOptionsBuilder options)
    {
        var d = new IgnoreDirective();
        foreach (var p in _paths) options.Add(p, d);
    }

    private static IEnumerable<string> AllPaths(IgnoreFieldConfig c) =>
        (c.Paths ?? []).Concat(c.Path is null ? [] : [c.Path]).Where(p => !string.IsNullOrWhiteSpace(p));

    public static IEnumerable<string> Validate(IgnoreFieldConfig c)
    {
        var all = AllPaths(c).ToList();
        if (all.Count == 0) yield return "at least one path is required";
        foreach (var p in all)
            foreach (var e in RuleValidation.Path(p, $"path '{p}'")) yield return e;
    }
}

// ───────────────────────────── SearchAndReplace ─────────────────────────────

public enum SearchScope { Body, Field }

/// <param name="Scope"><c>body</c>: whole response text before parsing. <c>field</c>: string values at <paramref name="Path"/> (and below).</param>
public sealed record SearchAndReplaceConfig(SearchScope Scope, string? Path, string Search, string? Replace, bool Regex, bool IgnoreCase);

public sealed class SearchAndReplaceRule : IDiffRule
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private readonly SearchAndReplaceConfig _c;
    private readonly Regex? _regex;
    private readonly PathMatcher<bool>? _matcher;

    public SearchAndReplaceRule(SearchAndReplaceConfig c)
    {
        _c = c;
        if (c.Regex)
            _regex = new Regex(c.Search, RegexOptions.CultureInvariant | (c.IgnoreCase ? RegexOptions.IgnoreCase : 0), RegexTimeout);
        if (c.Scope == SearchScope.Field)
            _matcher = new PathMatcher<bool>([(JsonPathPattern.Parse(c.Path!), true)]);
    }

    public string Type => "SearchAndReplace";
    public bool HasTextStage => _c.Scope == SearchScope.Body;
    public bool HasDocumentStage => _c.Scope == SearchScope.Field;

    public string TransformText(string text, Side side) => _c.Scope == SearchScope.Body ? Replace(text) : text;

    public JsonNode? TransformDocument(JsonNode? root, Side side)
    {
        if (_matcher is null || root is null) return root;
        if (_matcher.Root.HasMatches) return ReplaceIn(root) ?? root;
        Walk(root, _matcher.Root);
        return root;
    }

    private void Walk(JsonNode node, MatchState st)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    var cs = _matcher!.Step(st, PathSegment.Prop(key));
                    if (cs.IsEmpty) continue;
                    var child = o[key];
                    if (cs.HasMatches) { if (child is not null && ReplaceIn(child) is { } r) o[key] = r; }
                    else if (child is not null) Walk(child, cs);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    var cs = _matcher!.Step(st, PathSegment.Idx(i));
                    if (cs.IsEmpty) continue;
                    var child = a[i];
                    if (cs.HasMatches) { if (child is not null && ReplaceIn(child) is { } r) a[i] = r; }
                    else if (child is not null) Walk(child, cs);
                }
                break;
        }
    }

    /// <summary>Replaces in every string value of the subtree. Containers are edited in place (returns null);
    /// for a changed string value the replacement node is returned.</summary>
    private JsonNode? ReplaceIn(JsonNode node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(p => p.Key).ToList())
                    if (o[key] is { } v && ReplaceIn(v) is { } r) o[key] = r;
                return null;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    if (a[i] is { } v && ReplaceIn(v) is { } r) a[i] = r;
                return null;
            default:
                if (node.GetValueKind() != JsonValueKind.String) return null;
                var s = node.GetValue<string>();
                var replaced = Replace(s);
                return string.Equals(replaced, s, StringComparison.Ordinal) ? null : JsonValue.Create(replaced);
        }
    }

    private string Replace(string input)
    {
        try
        {
            if (_regex is not null) return _regex.Replace(input, _c.Replace ?? "");
            return input.Replace(_c.Search, _c.Replace ?? "", _c.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (RegexMatchTimeoutException)
        {
            return input; // a pathological pattern must never stall the worker
        }
    }

    public static IEnumerable<string> Validate(SearchAndReplaceConfig c)
    {
        if (string.IsNullOrEmpty(c.Search)) yield return "search is required";
        if (c.Scope == SearchScope.Field)
            foreach (var e in RuleValidation.Path(c.Path, "path")) yield return e;
        if (c.Regex && !string.IsNullOrEmpty(c.Search))
        {
            string? err = null;
            try { _ = new Regex(c.Search, RegexOptions.None, RegexTimeout); }
            catch (ArgumentException ex) { err = ex.Message; }
            if (err is not null) yield return $"invalid regex: {err}";
        }
    }
}

// ───────────────────────────── IgnoreOrder ─────────────────────────────

public sealed record IgnoreOrderConfig(string Path);

public sealed class IgnoreOrderRule(IgnoreOrderConfig c) : IDiffRule
{
    private readonly JsonPathPattern _path = JsonPathPattern.Parse(c.Path);
    public string Type => "IgnoreOrder";
    public void Configure(CompareOptionsBuilder options) => options.Add(_path, new IgnoreOrderDirective());
    public static IEnumerable<string> Validate(IgnoreOrderConfig c) => RuleValidation.Path(c.Path, "path");
}

// ───────────────────────────── NumericTolerance ─────────────────────────────

public enum ToleranceMode { Absolute, Relative }

public sealed record NumericToleranceConfig(string Path, double Tolerance, ToleranceMode Mode);

public sealed class NumericToleranceRule(NumericToleranceConfig c) : IDiffRule
{
    private readonly JsonPathPattern _path = JsonPathPattern.Parse(c.Path);
    public string Type => "NumericTolerance";
    public void Configure(CompareOptionsBuilder options) =>
        options.Add(_path, new ToleranceDirective(c.Tolerance, c.Mode == ToleranceMode.Relative));

    public static IEnumerable<string> Validate(NumericToleranceConfig c)
    {
        foreach (var e in RuleValidation.Path(c.Path, "path")) yield return e;
        if (c.Tolerance < 0 || double.IsNaN(c.Tolerance) || double.IsInfinity(c.Tolerance)) yield return "tolerance must be a finite number ≥ 0";
    }
}

// ───────────────────────────── IgnoreHeader ─────────────────────────────

public sealed record IgnoreHeaderConfig(string[] Headers);

public sealed class IgnoreHeaderRule(IgnoreHeaderConfig c) : IDiffRule
{
    public string Type => "IgnoreHeader";
    public void Configure(CompareOptionsBuilder options)
    {
        foreach (var h in c.Headers) options.IgnoreHeader(h);
    }

    public static IEnumerable<string> Validate(IgnoreHeaderConfig c)
    {
        if (c.Headers is not { Length: > 0 }) yield return "at least one header is required";
        else if (c.Headers.Any(string.IsNullOrWhiteSpace)) yield return "header names must not be empty";
    }
}

// ───────────────────────────── TypeCoercion ─────────────────────────────

public sealed record TypeCoercionConfig(string? Path);

public sealed class TypeCoercionRule(TypeCoercionConfig c) : IDiffRule
{
    private readonly JsonPathPattern[] _paths = string.IsNullOrWhiteSpace(c.Path)
        ? [JsonPathPattern.Parse("$"), JsonPathPattern.Parse("$..*")]
        : [JsonPathPattern.Parse(c.Path)];

    public string Type => "TypeCoercion";

    public void Configure(CompareOptionsBuilder options)
    {
        var d = new TypeCoercionDirective();
        foreach (var p in _paths) options.Add(p, d);
    }

    public static IEnumerable<string> Validate(TypeCoercionConfig c) =>
        string.IsNullOrWhiteSpace(c.Path) ? [] : RuleValidation.Path(c.Path, "path");
}
