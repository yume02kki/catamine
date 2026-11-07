using Mirror.Core.Rules;

namespace Mirror.Core.Models;

/// <summary>Header handling for requests sent to Dev.</summary>
public sealed record HeaderPolicy
{
    public static readonly string[] DefaultStrip = ["authorization", "cookie", "proxy-authorization"];

    /// <summary>Headers removed before sending to Dev (case-insensitive).</summary>
    public string[] Strip { get; init; } = DefaultStrip;
    /// <summary>Headers set (or overridden) on Dev requests, e.g. a Dev API key.</summary>
    public Dictionary<string, string> Set { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Everything the proxy needs to serve one mission. Immutable snapshot built from Postgres.</summary>
public sealed record MissionConfig
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required string MainUrl { get; init; }
    public required string DevUrl { get; init; }
    /// <summary>When false the proxy endpoint returns 404.</summary>
    public bool Enabled { get; init; } = true;
    public bool MirrorEnabled { get; init; } = true;
    /// <summary>0..1 fraction of eligible requests that are mirrored.</summary>
    public double SampleRate { get; init; } = 1.0;
    public string[] MirrorMethods { get; init; } = ["GET", "HEAD"];
    public int MaxMirrorBodyBytes { get; init; } = 256 * 1024;
    public int DevTimeoutMs { get; init; } = 2000;
    public int DevMaxConcurrency { get; init; } = 64;
    public HeaderPolicy Headers { get; init; } = new();
    public string[] ComparedHeaders { get; init; } = ["content-type"];
    public int RulesVersion { get; init; }
    public IReadOnlyList<RuleDefinition> Rules { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Fingerprint of everything that affects the Dev pipeline, to know when to rebuild it.</summary>
    public string PipelineKey => $"{DevUrl}|{DevTimeoutMs}|{DevMaxConcurrency}";

    public static readonly string[] SafeMethods = ["GET", "HEAD", "OPTIONS"];
}
