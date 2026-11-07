using System.Collections.Concurrent;
using System.Collections.Frozen;
using Mirror.Core.Diffing;
using Mirror.Core.Models;
using Mirror.Core.Rules;

namespace Mirror.Proxy.Config;

/// <summary>A mission config plus everything derived from it that the hot path and workers need.</summary>
public sealed class MissionRuntime
{
    public MissionConfig Config { get; }
    public FrozenSet<string> MirrorMethods { get; }
    public FrozenSet<string> StripHeaders { get; }
    public IReadOnlyDictionary<string, string> SetHeaders { get; }
    public CompiledRuleSet Rules { get; }
    public DiffSettings DiffSettings { get; }
    public Uri DevBase { get; }
    public string MainPrefix { get; }

    public MissionRuntime(MissionConfig c, CompiledRuleSet rules, int maxDiffBodyBytes)
    {
        Config = c;
        MirrorMethods = c.MirrorMethods.Select(m => m.ToUpperInvariant()).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        StripHeaders = (c.Headers.Strip ?? []).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
        SetHeaders = new Dictionary<string, string>(c.Headers.Set ?? [], StringComparer.OrdinalIgnoreCase);
        Rules = rules;
        DiffSettings = new DiffSettings { ComparedHeaders = c.ComparedHeaders, MaxDiffBodyBytes = maxDiffBodyBytes };
        DevBase = new Uri(c.DevUrl.TrimEnd('/') + "/");
        MainPrefix = c.MainUrl.TrimEnd('/');
    }
}

/// <summary>Immutable snapshot swapped atomically; the hot path does a single dictionary lookup with no locks.</summary>
public sealed class MissionConfigStore
{
    private volatile FrozenDictionary<string, MissionRuntime> _bySlug = FrozenDictionary<string, MissionRuntime>.Empty;
    private readonly ConcurrentDictionary<(Guid, int, string), CompiledRuleSet> _ruleCache = new();

    public bool HasSnapshot { get; private set; }
    public DateTimeOffset? LoadedAt { get; private set; }
    public string? Source { get; private set; }
    public IReadOnlyCollection<MissionRuntime> All => _bySlug.Values;

    public bool TryGet(string slug, out MissionRuntime mission) => _bySlug.TryGetValue(slug, out mission!);

    public void Apply(IReadOnlyList<MissionConfig> missions, int maxDiffBodyBytes, string source)
    {
        var map = new Dictionary<string, MissionRuntime>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in missions)
        {
            var rulesKey = string.Join(";", m.Rules.Select(r => $"{r.Id}:{r.Enabled}:{r.Order}:{r.Config.GetRawText()}"));
            var rules = _ruleCache.GetOrAdd((m.Id, m.RulesVersion, rulesKey), _ => CompiledRuleSet.Build(m.RulesVersion, m.Rules));
            try { map[m.Slug] = new MissionRuntime(m, rules, maxDiffBodyBytes); }
            catch (UriFormatException) { /* invalid Dev URL: skip mission rather than fail the snapshot */ }
        }
        _bySlug = map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        // Drop compiled rule sets that are no longer referenced.
        var live = map.Values.Select(v => v.Rules).ToHashSet();
        foreach (var key in _ruleCache.Keys)
            if (_ruleCache.TryGetValue(key, out var set) && !live.Contains(set)) _ruleCache.TryRemove(key, out _);
        HasSnapshot = true;
        LoadedAt = DateTimeOffset.UtcNow;
        Source = source;
    }
}
