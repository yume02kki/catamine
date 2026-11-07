using Mirror.Core.Diffing;

namespace Mirror.Core.Rules;

/// <summary>An ordered, immutable, ready-to-run set of rules for one mission and rules version.</summary>
public sealed class CompiledRuleSet
{
    public static readonly CompiledRuleSet Empty = new(0, [], []);

    public int Version { get; }
    public IReadOnlyList<IDiffRule> Rules { get; }
    public IReadOnlyList<(RuleDefinition Rule, string Error)> Errors { get; }
    public CompareOptions Options { get; }

    private readonly IDiffRule[] _textRules;
    private readonly IDiffRule[] _documentRules;

    private CompiledRuleSet(int version, IReadOnlyList<IDiffRule> rules, IReadOnlyList<(RuleDefinition, string)> errors)
    {
        Version = version;
        Rules = rules;
        Errors = errors;
        var b = new CompareOptionsBuilder();
        foreach (var r in rules) r.Configure(b);
        Options = b.Build();
        _textRules = rules.Where(r => r.HasTextStage).ToArray();
        _documentRules = rules.Where(r => r.HasDocumentStage).ToArray();
    }

    /// <summary>Builds the set. A rule with a bad config is skipped and reported, never thrown, so a single bad rule cannot stop mirroring.</summary>
    public static CompiledRuleSet Build(int version, IEnumerable<RuleDefinition> definitions)
    {
        var rules = new List<IDiffRule>();
        var errors = new List<(RuleDefinition, string)>();
        foreach (var def in definitions.Where(d => d.Enabled).OrderBy(d => d.Order))
        {
            try { rules.Add(RuleRegistry.Create(def.Type, def.Config)); }
            catch (Exception ex) when (ex is RuleConfigException or FormatException or ArgumentException)
            {
                errors.Add((def, ex.Message));
            }
        }
        return new CompiledRuleSet(version, rules, errors);
    }

    public static CompiledRuleSet FromRules(params IDiffRule[] rules) => new(0, rules, []);

    public bool HasTextStage => _textRules.Length > 0;
    public bool HasDocumentStage => _documentRules.Length > 0;

    public string ApplyText(string text, Side side)
    {
        foreach (var r in _textRules) text = r.TransformText(text, side);
        return text;
    }

    public System.Text.Json.Nodes.JsonNode? ApplyDocument(System.Text.Json.Nodes.JsonNode? root, Side side)
    {
        foreach (var r in _documentRules) root = r.TransformDocument(root, side);
        return root;
    }
}
