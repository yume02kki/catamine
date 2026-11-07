using System.Text.Json.Serialization;

namespace Mirror.Core.Diffing;

[Flags]
public enum DiffKinds
{
    None = 0,
    Status = 1,
    Changed = 2,
    Added = 4,
    Removed = 8,
    TypeChanged = 16,
    ArrayLength = 32,
    Order = 64,
    Header = 128,
    /// <summary>Bodies differ but at least one is not JSON (compared as text).</summary>
    Body = 256,
}

public enum DiffKind
{
    Status,
    Changed,
    Added,
    Removed,
    TypeChanged,
    ArrayLength,
    Order,
    Header,
    Body,
}

public enum Verdict : short
{
    Match = 0,
    Mismatch = 1,
    DevError = 2,
    DevTimeout = 3,
    NotMirrored = 4,
}

public static class DiffKindExtensions
{
    public static DiffKinds ToFlag(this DiffKind k) => (DiffKinds)(1 << (int)k);

    public static string ToWire(this DiffKind k) => k switch
    {
        DiffKind.Status => "status",
        DiffKind.Changed => "changed",
        DiffKind.Added => "added",
        DiffKind.Removed => "removed",
        DiffKind.TypeChanged => "type_changed",
        DiffKind.ArrayLength => "array_length",
        DiffKind.Order => "order",
        DiffKind.Header => "header",
        DiffKind.Body => "body",
        _ => k.ToString().ToLowerInvariant(),
    };

    public static bool TryParseWire(string s, out DiffKind kind)
    {
        foreach (var k in Enum.GetValues<DiffKind>())
            if (string.Equals(k.ToWire(), s, StringComparison.OrdinalIgnoreCase)) { kind = k; return true; }
        kind = default;
        return false;
    }

    public static string ToWire(this Verdict v) => v switch
    {
        Verdict.Match => "match",
        Verdict.Mismatch => "mismatch",
        Verdict.DevError => "dev_error",
        Verdict.DevTimeout => "dev_timeout",
        Verdict.NotMirrored => "not_mirrored",
        _ => v.ToString(),
    };

    public static bool TryParseVerdict(string s, out Verdict v)
    {
        foreach (var x in Enum.GetValues<Verdict>())
            if (string.Equals(x.ToWire(), s, StringComparison.OrdinalIgnoreCase)) { v = x; return true; }
        v = default;
        return false;
    }
}

/// <summary>One difference between Main and Dev. Values are compact JSON, truncated.</summary>
public sealed record Difference(
    [property: JsonPropertyName("k")] string Kind,
    [property: JsonPropertyName("p")] string Path,
    [property: JsonPropertyName("m")] string? Main,
    [property: JsonPropertyName("d")] string? Dev)
{
    [JsonIgnore] public DiffKind KindEnum { get; init; }
    [JsonIgnore] public string NormalizedPath { get; init; } = Path;
}

public sealed record ArrayLengthChange(
    [property: JsonPropertyName("p")] string Path,
    [property: JsonPropertyName("m")] int Main,
    [property: JsonPropertyName("d")] int Dev);

/// <summary>Short, index-friendly summary stored on every log row.</summary>
public sealed class DiffSummary
{
    [JsonPropertyName("changed")] public int Changed { get; set; }
    [JsonPropertyName("added")] public int Added { get; set; }
    [JsonPropertyName("removed")] public int Removed { get; set; }
    [JsonPropertyName("typeChanged")] public int TypeChanged { get; set; }
    [JsonPropertyName("order")] public int Order { get; set; }
    [JsonPropertyName("headers")] public List<string> Headers { get; set; } = [];
    [JsonPropertyName("arrays")] public List<ArrayLengthChange> Arrays { get; set; } = [];
    [JsonPropertyName("top")] public List<Difference> Top { get; set; } = [];
    [JsonPropertyName("truncated")] public bool Truncated { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
}

public sealed class DiffResult
{
    public required List<Difference> Differences { get; init; }
    public required DiffKinds Kinds { get; init; }
    /// <summary>Total number of differences found, which can exceed <see cref="Differences"/>.Count when truncated.</summary>
    public required int TotalCount { get; init; }
    public required bool Truncated { get; init; }
    public required DiffSummary Summary { get; init; }
    /// <summary>Stable hash of the normalised (kind, path) set; groups mismatches by cause.</summary>
    public required long Signature { get; init; }
    public string? Note { get; init; }

    public bool IsEqual => TotalCount == 0;
}
