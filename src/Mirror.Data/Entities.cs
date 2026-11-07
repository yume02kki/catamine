namespace Mirror.Data;

public sealed class MissionEntity
{
    public Guid Id { get; set; }
    public required string Slug { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string MainUrl { get; set; }
    public required string DevUrl { get; set; }
    public bool Enabled { get; set; } = true;
    public bool MirrorEnabled { get; set; } = true;
    public double SampleRate { get; set; } = 1.0;
    public string[] MirrorMethods { get; set; } = ["GET", "HEAD"];
    public int MaxMirrorBodyBytes { get; set; } = 256 * 1024;
    public int DevTimeoutMs { get; set; } = 2000;
    public int DevMaxConcurrency { get; set; } = 64;
    /// <summary>JSON: <see cref="Mirror.Core.Models.HeaderPolicy"/>.</summary>
    public string HeaderPolicy { get; set; } = """{"strip":["authorization","cookie","proxy-authorization"],"set":{}}""";
    public string[] ComparedHeaders { get; set; } = ["content-type"];
    public int RulesVersion { get; set; } = 1;
    public int RetentionDays { get; set; } = 14;
    public int? MaxStorageMb { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<MissionRuleEntity> Rules { get; set; } = [];
}

public sealed class MissionRuleEntity
{
    public Guid Id { get; set; }
    public Guid MissionId { get; set; }
    public int Order { get; set; }
    public required string Type { get; set; }
    public string? Name { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Rule-type specific JSON config.</summary>
    public string Config { get; set; } = "{}";
}

public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

public sealed class JobEntity
{
    public Guid Id { get; set; }
    public Guid? MissionId { get; set; }
    public required string Type { get; set; }
    public JobStatus Status { get; set; }
    public string Params { get; set; } = "{}";
    public long Done { get; set; }
    public long? Total { get; set; }
    public string? Result { get; set; }
    /// <summary>Opaque per-job resume state (keyset cursor, line number…).</summary>
    public string? Checkpoint { get; set; }
    public string? Error { get; set; }
    public bool CancelRequested { get; set; }
    public string? WorkerId { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public DateTimeOffset? HeartbeatAt { get; set; }
}

public sealed class ProxyInstanceEntity
{
    public required string InstanceId { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public string Metrics { get; set; } = "{}";
}
