using System.Text;

namespace Mirror.Core.Paths;

internal enum TokenKind : byte
{
    Child,     // .name  ['name']
    AnyChild,  // .*  (matches any property, index or key segment)
    Index,     // [3]
    AnyIndex,  // [*] (matches any index or keyed element)
}

internal readonly record struct PathToken(TokenKind Kind, string? Name, int Index, bool Descendant)
{
    public bool Matches(in PathSegment s) => Kind switch
    {
        TokenKind.Child => s.Kind == SegmentKind.Property && string.Equals(s.Name, Name, StringComparison.Ordinal),
        TokenKind.AnyChild => true,
        TokenKind.Index => s.Kind == SegmentKind.Index && s.Index == Index,
        TokenKind.AnyIndex => s.Kind is SegmentKind.Index or SegmentKind.Key,
        _ => false,
    };
}

/// <summary>
/// A JSONPath subset used by rules: <c>$</c>, <c>.name</c>, <c>['name']</c>, <c>[n]</c>, <c>[*]</c>, <c>.*</c>, <c>..name</c>, <c>..*</c>.
/// The leading <c>$</c> is optional (relative paths, e.g. primary key fields).
/// </summary>
public sealed class JsonPathPattern
{
    internal PathToken[] Tokens { get; }
    public string Text { get; }

    private JsonPathPattern(string text, PathToken[] tokens)
    {
        Text = text;
        Tokens = tokens;
    }

    public int Length => Tokens.Length;

    /// <summary>True when the pattern is a plain chain of property names (e.g. <c>meta.id</c>), usable for direct lookup.</summary>
    public bool IsSimplePropertyChain => Tokens.All(t => t.Kind == TokenKind.Child && !t.Descendant);

    public IEnumerable<string> PropertyChain => Tokens.Select(t => t.Name!);

    public override string ToString() => Text;

    public static bool TryParse(string? text, out JsonPathPattern pattern, out string? error)
    {
        pattern = null!;
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "path is empty"; return false; }
        var s = text.Trim();
        var tokens = new List<PathToken>();
        var i = 0;
        if (s[0] == '$') i = 1;
        else if (s[0] != '.' && s[0] != '[') s = "." + s; // relative "a.b" → ".a.b"

        while (i < s.Length)
        {
            var descendant = false;
            if (s[i] == '.')
            {
                if (i + 1 < s.Length && s[i + 1] == '.') { descendant = true; i += 2; }
                else i += 1;
                if (i >= s.Length) { error = $"unexpected end after '.' at {i}"; return false; }
                if (s[i] == '[') { if (!ParseBracket(s, ref i, descendant, tokens, out error)) return false; continue; }
                if (s[i] == '*') { tokens.Add(new PathToken(TokenKind.AnyChild, null, -1, descendant)); i++; continue; }
                var start = i;
                while (i < s.Length && s[i] != '.' && s[i] != '[') i++;
                var name = s[start..i];
                if (name.Length == 0) { error = $"empty property name at {start}"; return false; }
                tokens.Add(new PathToken(TokenKind.Child, name, -1, descendant));
            }
            else if (s[i] == '[')
            {
                if (!ParseBracket(s, ref i, false, tokens, out error)) return false;
            }
            else { error = $"unexpected '{s[i]}' at {i}"; return false; }
        }

        pattern = new JsonPathPattern(text.Trim(), tokens.ToArray());
        return true;
    }

    public static JsonPathPattern Parse(string text) =>
        TryParse(text, out var p, out var err) ? p : throw new FormatException($"Invalid JSON path '{text}': {err}");

    private static bool ParseBracket(string s, ref int i, bool descendant, List<PathToken> tokens, out string? error)
    {
        error = null;
        var close = FindClose(s, i);
        if (close < 0) { error = $"missing ']' for '[' at {i}"; return false; }
        var inner = s[(i + 1)..close].Trim();
        i = close + 1;
        if (inner == "*") { tokens.Add(new PathToken(TokenKind.AnyIndex, null, -1, descendant)); return true; }
        if (inner.Length >= 2 && (inner[0] == '\'' || inner[0] == '"') && inner[^1] == inner[0])
        {
            var name = Unescape(inner[1..^1]);
            tokens.Add(name == "*"
                ? new PathToken(TokenKind.AnyChild, null, -1, descendant)
                : new PathToken(TokenKind.Child, name, -1, descendant));
            return true;
        }
        if (int.TryParse(inner, out var idx) && idx >= 0) { tokens.Add(new PathToken(TokenKind.Index, null, idx, descendant)); return true; }
        error = $"unsupported bracket expression '[{inner}]'";
        return false;
    }

    private static int FindClose(string s, int open)
    {
        char? quote = null;
        for (var j = open + 1; j < s.Length; j++)
        {
            var c = s[j];
            if (quote is not null)
            {
                if (c == '\\') { j++; continue; }
                if (c == quote) quote = null;
            }
            else if (c is '\'' or '"') quote = c;
            else if (c == ']') return j;
        }
        return -1;
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    /// <summary>Whole-path match (used in tests and for one-off checks).</summary>
    public bool Matches(IReadOnlyList<PathSegment> path)
    {
        var m = new PathMatcher<bool>([(this, true)]);
        var st = m.Root;
        foreach (var seg in path) st = m.Step(st, seg);
        return st.HasMatches;
    }
}
