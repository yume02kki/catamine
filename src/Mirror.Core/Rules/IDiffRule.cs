using System.Text.Json;
using System.Text.Json.Nodes;
using Mirror.Core.Diffing;

namespace Mirror.Core.Rules;

public enum Side { Main, Dev }

/// <summary>
/// A comparison rule. Each rule type is one class; it implements only the stages it needs:
/// <list type="number">
/// <item><see cref="TransformText"/> — on the decoded body text, before parsing.</item>
/// <item><see cref="TransformDocument"/> — on the parsed JSON tree.</item>
/// <item><see cref="Configure"/> — contributes directives to the comparer (keys, tolerances, ignores…).</item>
/// </list>
/// Rules run in the mission's configured order within each stage.
/// </summary>
public interface IDiffRule
{
    string Type { get; }

    bool HasTextStage => false;
    string TransformText(string text, Side side) => text;

    bool HasDocumentStage => false;
    JsonNode? TransformDocument(JsonNode? root, Side side) => root;

    void Configure(CompareOptionsBuilder options) { }
}

/// <summary>Persisted rule definition (one row of <c>mission_rules</c>).</summary>
public sealed record RuleDefinition(Guid Id, string Type, bool Enabled, int Order, JsonElement Config, string? Name = null);

public sealed class RuleConfigException(string message) : Exception(message);
