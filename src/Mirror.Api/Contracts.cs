using System.Text.Json;
using System.Text.Json.Serialization;
using Mirror.Core.Diffing;
using Mirror.Core.Models;
using Mirror.Core.Rules;
using Mirror.Data;

namespace Mirror.Api;

public sealed record MissionDto(
    Guid Id, string Slug, string Name, string? Description, string MainUrl, string DevUrl, string ProxyPath,
    bool Enabled, bool MirrorEnabled, double SampleRate, string[] MirrorMethods, int MaxMirrorBodyBytes,
    int DevTimeoutMs, int DevMaxConcurrency, HeaderPolicy HeaderPolicy, string[] ComparedHeaders, int RulesVersion,
    int RetentionDays, int? MaxStorageMb, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string[] Warnings, int RuleCount);

public sealed record MissionUpsert
{
    public string? Slug { get; init; }
    public string? Name { get; init; }
    public string? Description { get; init; }
    public string? MainUrl { get; init; }
    public string? DevUrl { get; init; }
    public bool? Enabled { get; init; }
    public bool? MirrorEnabled { get; init; }
    public double? SampleRate { get; init; }
    public string[]? MirrorMethods { get; init; }
    /// <summary>Must be true to enable mirroring of POST/PUT/PATCH/DELETE (they may cause side effects on Dev).</summary>
    public bool AcknowledgeUnsafeMethods { get; init; }
    public int? MaxMirrorBodyBytes { get; init; }
    public int? DevTimeoutMs { get; init; }
    public int? DevMaxConcurrency { get; init; }
    public HeaderPolicy? HeaderPolicy { get; init; }
    public string[]? ComparedHeaders { get; init; }
    public int? RetentionDays { get; init; }
    public int? MaxStorageMb { get; init; }
}

public sealed record RuleDto(Guid Id, int Order, string Type, string? Name, bool Enabled, JsonElement Config);

public sealed record RuleInput(Guid? Id, string Type, string? Name, bool Enabled, JsonElement Config);

public sealed record RulesUpdate(List<RuleInput> Rules, bool Rediff = false);

public sealed record RuleValidationRequest(string Type, JsonElement Config);

public sealed record RulePreviewRequest(List<RuleInput> Rules, int SampleSize = 200);

public sealed record LogItemDto(
    Guid Id, DateTime Ts, string Method, string Path, string? Query, int MainStatus, int? DevStatus,
    float MainMs, float? DevMs, string Verdict, string[] DiffKinds, int DiffCount, string Signature, int RulesVersion,
    string Source, Guid? JobId, Guid? ReplayOf, string? Note, JsonElement? Summary)
{
    public static LogItemDto From(LogRow r) => new(
        r.Id, r.Ts, r.Method, r.Path, r.Query, r.MainStatus, r.DevStatus, r.MainMs, r.DevMs, r.Verdict.ToWire(),
        KindNames((DiffKinds)r.DiffKinds), r.DiffCount, r.Signature.ToString(System.Globalization.CultureInfo.InvariantCulture),
        r.RulesVersion, r.Source.ToString().ToLowerInvariant(), r.JobId, r.ReplayOf, r.Note,
        r.SummaryJson is null ? null : JsonDocument.Parse(r.SummaryJson).RootElement.Clone());

    public static string[] KindNames(DiffKinds k) =>
        Enum.GetValues<DiffKind>().Where(x => k.HasFlag(x.ToFlag())).Select(x => x.ToWire()).ToArray();
}

public sealed record ExchangePart(int? Status, Dictionary<string, string> Headers, int BodyBytes, string BodyUrl);

public sealed record LogDetailDto(LogItemDto Log, ExchangePart Request, ExchangePart Main, ExchangePart? Dev, IReadOnlyList<Difference> Differences);

public sealed record ReplayResultDto(LogItemDto Original, LogItemDto Replay, IReadOnlyList<Difference> Differences);

public sealed record JobDto(
    Guid Id, Guid? MissionId, string Type, string Status, long Done, long? Total, JsonElement? Params, JsonElement? Result,
    string? Error, bool CancelRequested, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt)
{
    public static JobDto From(JobEntity j) => new(j.Id, j.MissionId, j.Type, j.Status.ToString().ToLowerInvariant(), j.Done, j.Total,
        Parse(j.Params), Parse(j.Result), j.Error, j.CancelRequested, j.CreatedAt, j.StartedAt, j.FinishedAt);

    private static JsonElement? Parse(string? json) => string.IsNullOrEmpty(json) ? null : JsonDocument.Parse(json).RootElement.Clone();
}

/// <summary>Log filter as accepted by query strings and job parameters.</summary>
public sealed record LogFilter
{
    public string? Verdict { get; init; }
    public string? DiffKind { get; init; }
    public string? Path { get; init; }
    public string? PathMode { get; init; }
    public string? Method { get; init; }
    public int? MainStatus { get; init; }
    public int? DevStatus { get; init; }
    public string? Source { get; init; }
    public Guid? JobId { get; init; }
    public Guid? ReplayOf { get; init; }
    public string? Signature { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }

    public LogQuery ToQuery(Guid missionId, string? sort = null, string? cursor = null, int limit = 100)
    {
        Verdict[]? verdicts = null;
        if (!string.IsNullOrWhiteSpace(Verdict))
            verdicts = Verdict.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(v => DiffKindExtensions.TryParseVerdict(v, out var x) ? x : throw new BadRequestException($"unknown verdict '{v}'"))
                .ToArray();
        var kinds = DiffKinds.None;
        if (!string.IsNullOrWhiteSpace(DiffKind))
            foreach (var k in DiffKind.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                kinds |= DiffKindExtensions.TryParseWire(k, out var x) ? x.ToFlag() : throw new BadRequestException($"unknown diff kind '{k}'");
        LogSource? source = null;
        if (!string.IsNullOrWhiteSpace(Source))
            source = Enum.TryParse<LogSource>(Source, true, out var s) ? s : throw new BadRequestException($"unknown source '{Source}'");
        long? sig = null;
        if (!string.IsNullOrWhiteSpace(Signature))
            sig = long.TryParse(Signature, out var sv) ? sv : throw new BadRequestException("signature must be an integer");
        return new LogQuery
        {
            MissionId = missionId,
            Verdicts = verdicts,
            DiffKinds = kinds,
            Path = Path,
            PathMode = PathMode?.ToLowerInvariant() switch { "contains" => PathMatchMode.Contains, "exact" => PathMatchMode.Exact, _ => PathMatchMode.Prefix },
            Method = Method,
            MainStatus = MainStatus,
            DevStatus = DevStatus,
            Source = source,
            JobId = JobId,
            ReplayOf = ReplayOf,
            Signature = sig,
            From = From?.ToUniversalTime(),
            To = To?.ToUniversalTime(),
            Sort = sort?.ToLowerInvariant() switch
            {
                "ts_asc" or "oldest" => LogSort.TsAsc,
                "dev_ms_desc" or "slowest" => LogSort.DevMsDesc,
                "diff_count_desc" or "most_different" => LogSort.DiffCountDesc,
                _ => LogSort.TsDesc,
            },
            Cursor = cursor,
            Limit = limit,
        };
    }
}

public sealed class BadRequestException(string message) : Exception(message);

public static class MissionMapping
{
    public static readonly string[] KnownMethods = ["GET", "HEAD", "OPTIONS", "POST", "PUT", "PATCH", "DELETE"];
    public static readonly string[] UnsafeMethods = ["POST", "PUT", "PATCH", "DELETE"];

    public static string[] Warnings(MissionEntity m)
    {
        var unsafeOn = m.MirrorMethods.Intersect(UnsafeMethods, StringComparer.OrdinalIgnoreCase).ToArray();
        return unsafeOn.Length == 0
            ? []
            : [$"Mirroring {string.Join(", ", unsafeOn)} sends real write requests to Dev. Make sure Dev uses an isolated datastore and cannot call production services."];
    }

    public static MissionDto ToDto(MissionEntity m) => ToDto(m, m.Rules.Count);

    public static MissionDto ToDto(MissionEntity m, int ruleCount) => new(
        m.Id, m.Slug, m.Name, m.Description, m.MainUrl, m.DevUrl, $"/m/{m.Slug}", m.Enabled, m.MirrorEnabled, m.SampleRate,
        m.MirrorMethods, m.MaxMirrorBodyBytes, m.DevTimeoutMs, m.DevMaxConcurrency,
        JsonSerializer.Deserialize<HeaderPolicy>(m.HeaderPolicy, MissionSnapshotLoader.JsonOptions) ?? new HeaderPolicy(),
        m.ComparedHeaders, m.RulesVersion, m.RetentionDays, m.MaxStorageMb, m.CreatedAt, m.UpdatedAt, Warnings(m), ruleCount);

    public static RuleDto ToDto(MissionRuleEntity r) => new(r.Id, r.Order, r.Type, r.Name, r.Enabled, JsonDocument.Parse(r.Config).RootElement.Clone());

    public static CompiledRuleSet CompileRules(MissionEntity m, IEnumerable<MissionRuleEntity> rules) =>
        CompiledRuleSet.Build(m.RulesVersion, rules.Select(r => new RuleDefinition(r.Id, r.Type, r.Enabled, r.Order, JsonDocument.Parse(r.Config).RootElement.Clone(), r.Name)));

    public static DiffSettings DiffSettings(MissionEntity m) => new() { ComparedHeaders = m.ComparedHeaders };

    public static HeaderPolicy Headers(MissionEntity m) =>
        JsonSerializer.Deserialize<HeaderPolicy>(m.HeaderPolicy, MissionSnapshotLoader.JsonOptions) ?? new HeaderPolicy();
}
