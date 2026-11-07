namespace Mirror.Core.Paths;

/// <summary>Set of partially matched patterns at a node of the tree walk.</summary>
public sealed class MatchState
{
    public static readonly MatchState Empty = new([], []);

    internal readonly int[] Active;   // (patternIndex << 8) | tokenPosition
    internal readonly int[] Matched;  // pattern indices that fully match this node

    internal MatchState(int[] active, int[] matched)
    {
        Active = active;
        Matched = matched;
    }

    public bool IsEmpty => Active.Length == 0 && Matched.Length == 0;
    public bool HasMatches => Matched.Length > 0;
}

/// <summary>
/// Matches many JSON path patterns at once while a tree is walked top-down.
/// Each pattern is a tiny NFA; <see cref="Step"/> advances all of them by one segment,
/// so cost per node is proportional to the number of live patterns, not to depth.
/// </summary>
public sealed class PathMatcher<T>
{
    private readonly JsonPathPattern[] _patterns;
    private readonly T[] _payloads;

    public MatchState Root { get; }
    public bool IsEmpty => _patterns.Length == 0;

    public PathMatcher(IEnumerable<(JsonPathPattern Pattern, T Payload)> items)
    {
        var list = items.ToList();
        if (list.Any(x => x.Pattern.Length > 255)) throw new ArgumentException("JSON path too long (max 255 segments)");
        _patterns = list.Select(x => x.Pattern).ToArray();
        _payloads = list.Select(x => x.Payload).ToArray();

        var active = new List<int>();
        var matched = new List<int>();
        for (var p = 0; p < _patterns.Length; p++)
        {
            if (_patterns[p].Length == 0) matched.Add(p);
            else active.Add(p << 8);
        }
        Root = active.Count == 0 && matched.Count == 0 ? MatchState.Empty : new MatchState(active.ToArray(), matched.ToArray());
    }

    public MatchState Step(MatchState state, in PathSegment segment)
    {
        if (state.Active.Length == 0) return MatchState.Empty;
        List<int>? next = null;
        List<int>? matched = null;
        foreach (var code in state.Active)
        {
            var p = code >> 8;
            var pos = code & 0xFF;
            var tokens = _patterns[p].Tokens;
            var tok = tokens[pos];
            if (tok.Descendant) AddUnique(ref next, code);
            if (tok.Matches(segment))
            {
                var np = pos + 1;
                if (np == tokens.Length) AddUnique(ref matched, p);
                else AddUnique(ref next, (p << 8) | np);
            }
        }
        if (next is null && matched is null) return MatchState.Empty;
        return new MatchState(next?.ToArray() ?? [], matched?.ToArray() ?? []);
    }

    public IEnumerable<T> MatchesAt(MatchState state)
    {
        foreach (var p in state.Matched) yield return _payloads[p];
    }

    public bool AnyMatch(MatchState state, Func<T, bool> predicate)
    {
        foreach (var p in state.Matched)
            if (predicate(_payloads[p])) return true;
        return false;
    }

    private static void AddUnique(ref List<int>? list, int value)
    {
        list ??= new List<int>(4);
        if (!list.Contains(value)) list.Add(value);
    }
}
