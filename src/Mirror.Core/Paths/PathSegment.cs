using System.Text;

namespace Mirror.Core.Paths;

public enum SegmentKind : byte
{
    Property,
    Index,
    /// <summary>An array element matched by primary key. <see cref="PathSegment.Name"/> is the key field label.</summary>
    Key,
}

/// <summary>One step of a concrete JSON path.</summary>
public readonly record struct PathSegment(SegmentKind Kind, string? Name, int Index, string? KeyValue)
{
    public static PathSegment Prop(string name) => new(SegmentKind.Property, name, -1, null);
    public static PathSegment Idx(int index) => new(SegmentKind.Index, null, index, null);
    public static PathSegment Keyed(string keyLabel, string keyValue, int index) => new(SegmentKind.Key, keyLabel, index, keyValue);

    internal void Append(StringBuilder sb, bool normalized)
    {
        switch (Kind)
        {
            case SegmentKind.Property:
                if (IsIdentifier(Name!)) sb.Append('.').Append(Name);
                else sb.Append("['").Append(Name!.Replace("\\", "\\\\").Replace("'", "\\'")).Append("']");
                break;
            case SegmentKind.Index:
                if (normalized) sb.Append("[*]"); else sb.Append('[').Append(Index).Append(']');
                break;
            case SegmentKind.Key:
                if (normalized) sb.Append("[*]");
                else sb.Append('[').Append(Name).Append('=').Append(KeyValue!.Replace("]", "\\]")).Append(']');
                break;
        }
    }

    internal static bool IsIdentifier(string s)
    {
        if (s.Length == 0) return false;
        if (!(char.IsLetter(s[0]) || s[0] == '_' || s[0] == '$')) return false;
        foreach (var c in s)
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '-')) return false;
        return true;
    }
}

/// <summary>Immutable linked path (child → parent) so walking a large tree does not allocate strings.</summary>
public sealed class PathNode
{
    public static readonly PathNode Root = new(null, default);

    public PathNode? Parent { get; }
    public PathSegment Segment { get; }
    public int Depth { get; }

    private PathNode(PathNode? parent, PathSegment segment)
    {
        Parent = parent;
        Segment = segment;
        Depth = parent is null ? 0 : parent.Depth + 1;
    }

    public PathNode Child(in PathSegment segment) => new(this, segment);

    public override string ToString() => Format(normalized: false);

    /// <summary>Path with indices and keys replaced by <c>[*]</c>; used to group similar differences.</summary>
    public string Normalized() => Format(normalized: true);

    private string Format(bool normalized)
    {
        if (Parent is null) return "$";
        var stack = new PathNode[Depth];
        var n = this;
        for (var i = Depth - 1; i >= 0; i--) { stack[i] = n!; n = n!.Parent; }
        var sb = new StringBuilder("$", Depth * 8);
        foreach (var p in stack) p.Segment.Append(sb, normalized);
        return sb.ToString();
    }
}
