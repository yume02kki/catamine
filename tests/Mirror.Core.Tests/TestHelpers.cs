using System.Text;
using System.Text.Json;
using Mirror.Core.Diffing;
using Mirror.Core.Rules;

namespace Mirror.Core.Tests;

internal static class T
{
    public static RuleDefinition Rule(string type, object config, int order = 0) =>
        new(Guid.NewGuid(), type, true, order, JsonSerializer.SerializeToElement(config, RuleRegistry.JsonOptions));

    public static CompiledRuleSet Rules(params (string Type, object Config)[] rules) =>
        CompiledRuleSet.Build(1, rules.Select((r, i) => Rule(r.Type, r.Config, i)));

    public static DiffResult Diff(string main, string dev, CompiledRuleSet? rules = null, DiffSettings? settings = null,
        int mainStatus = 200, int devStatus = 200,
        Dictionary<string, string>? mainHeaders = null, Dictionary<string, string>? devHeaders = null) =>
        DiffEngine.Compare(new DiffInput
        {
            MainStatus = mainStatus,
            DevStatus = devStatus,
            MainBody = Encoding.UTF8.GetBytes(main),
            DevBody = Encoding.UTF8.GetBytes(dev),
            MainHeaders = mainHeaders,
            DevHeaders = devHeaders,
        }, rules ?? CompiledRuleSet.Empty, settings);

    public static Difference Single(this DiffResult r)
    {
        Assert.Single(r.Differences);
        return r.Differences[0];
    }

    public static string[] Paths(this DiffResult r) => r.Differences.Select(d => $"{d.Kind} {d.Path}").ToArray();
}
