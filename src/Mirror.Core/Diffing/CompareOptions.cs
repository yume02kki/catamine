using Mirror.Core.Paths;

namespace Mirror.Core.Diffing;

/// <summary>Instruction attached to a path pattern; produced by rules in the compare stage.</summary>
public abstract record CompareDirective;

public sealed record IgnoreDirective : CompareDirective;

public sealed record PrimaryKeyDirective(IReadOnlyList<JsonPathPattern> KeyPaths) : CompareDirective
{
    public string Label { get; } = string.Join("+", KeyPaths.Select(k => k.Text.TrimStart('$', '.')));
}

public sealed record IgnoreOrderDirective : CompareDirective;

public sealed record ToleranceDirective(double Tolerance, bool Relative) : CompareDirective;

public sealed record TypeCoercionDirective : CompareDirective;

/// <summary>Collects directives from rules, then freezes into an immutable <see cref="CompareOptions"/>.</summary>
public sealed class CompareOptionsBuilder
{
    private readonly List<(JsonPathPattern, CompareDirective)> _directives = [];
    private readonly HashSet<string> _ignoredHeaders = new(StringComparer.OrdinalIgnoreCase);

    public CompareOptionsBuilder Add(JsonPathPattern path, CompareDirective directive)
    {
        _directives.Add((path, directive));
        return this;
    }

    public CompareOptionsBuilder IgnoreHeader(string name)
    {
        _ignoredHeaders.Add(name.Trim());
        return this;
    }

    public CompareOptions Build() => new(new PathMatcher<CompareDirective>(_directives), _ignoredHeaders.ToHashSet(StringComparer.OrdinalIgnoreCase));
}

public sealed class CompareOptions
{
    public static readonly CompareOptions Default = new CompareOptionsBuilder().Build();

    internal PathMatcher<CompareDirective> Matcher { get; }
    public IReadOnlySet<string> IgnoredHeaders { get; }

    internal CompareOptions(PathMatcher<CompareDirective> matcher, IReadOnlySet<string> ignoredHeaders)
    {
        Matcher = matcher;
        IgnoredHeaders = ignoredHeaders;
    }
}

public sealed class DiffSettings
{
    public static readonly DiffSettings Default = new();

    /// <summary>Response headers compared between Main and Dev (case-insensitive).</summary>
    public IReadOnlyList<string> ComparedHeaders { get; init; } = ["content-type"];
    /// <summary>Differences kept in detail; the rest are only counted.</summary>
    public int MaxDifferences { get; init; } = 5000;
    /// <summary>Bodies bigger than this (decoded) skip field-level diffing.</summary>
    public int MaxDiffBodyBytes { get; init; } = 16 * 1024 * 1024;
    public int ValueSnippetLength { get; init; } = 300;
    public int SummaryTopCount { get; init; } = 5;
}
