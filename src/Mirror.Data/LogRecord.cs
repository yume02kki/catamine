using System.Text.Json;
using Mirror.Core.Compression;
using Mirror.Core.Diffing;

namespace Mirror.Data;

public enum LogSource : short
{
    Live = 0,
    Replay = 1,
    Fuzz = 2,
    Import = 3,
}

/// <summary>One traffic log entry plus its (already compressed) payloads, ready for COPY.</summary>
public sealed class LogRecord
{
    public Guid Id { get; init; }
    public Guid MissionId { get; init; }
    /// <summary>UTC, millisecond precision (matches the UUIDv7 timestamp in <see cref="Id"/>).</summary>
    public DateTime Ts { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }
    public string? Query { get; init; }
    public short MainStatus { get; init; }
    public short? DevStatus { get; set; }
    public float MainMs { get; init; }
    public float? DevMs { get; set; }
    public Verdict Verdict { get; set; }
    public int DiffKinds { get; set; }
    public int DiffCount { get; set; }
    public long Signature { get; set; }
    public int RulesVersion { get; set; }
    public LogSource Source { get; init; }
    public Guid? JobId { get; init; }
    public Guid? ReplayOf { get; init; }
    public string? Note { get; set; }
    public string? SummaryJson { get; set; }

    public byte[]? RequestHeaders { get; init; }
    public byte[]? RequestBody { get; init; }
    public byte[]? MainHeaders { get; init; }
    public byte[]? MainBody { get; init; }
    public byte[]? DevHeaders { get; set; }
    public byte[]? DevBody { get; set; }
    public byte[]? Diff { get; set; }

    /// <summary>Approximate memory held by this record, for queue accounting.</summary>
    public int ApproximateBytes =>
        256 + Path.Length * 2 + (Query?.Length ?? 0) * 2 + (SummaryJson?.Length ?? 0) * 2 +
        (RequestHeaders?.Length ?? 0) + (RequestBody?.Length ?? 0) + (MainHeaders?.Length ?? 0) + (MainBody?.Length ?? 0) +
        (DevHeaders?.Length ?? 0) + (DevBody?.Length ?? 0) + (Diff?.Length ?? 0);

    /// <summary>Creates a time-ordered id and the matching millisecond timestamp.</summary>
    public static (Guid Id, DateTime Ts) NewId(DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        var ms = new DateTimeOffset(now.UtcTicks - now.UtcTicks % TimeSpan.TicksPerMillisecond, TimeSpan.Zero);
        return (Guid.CreateVersion7(ms), ms.UtcDateTime);
    }

    /// <summary>Extracts the timestamp embedded in a UUIDv7 log id (used to prune partitions on lookups).</summary>
    public static DateTime TimestampOf(Guid id)
    {
        Span<byte> b = stackalloc byte[16];
        id.TryWriteBytes(b, bigEndian: true, out _);
        long ms = ((long)b[0] << 40) | ((long)b[1] << 32) | ((long)b[2] << 24) | ((long)b[3] << 16) | ((long)b[4] << 8) | b[5];
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
    }

    public static byte[]? PackHeaders(IEnumerable<KeyValuePair<string, string>>? headers)
    {
        if (headers is null) return null;
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in headers) dict[k] = dict.TryGetValue(k, out var e) ? e + ", " + v : v;
        return dict.Count == 0 ? null : BodyCodec.Compress(JsonSerializer.SerializeToUtf8Bytes(dict));
    }

    public static Dictionary<string, string> UnpackHeaders(byte[]? packed)
    {
        if (packed is null || packed.Length == 0) return new(StringComparer.OrdinalIgnoreCase);
        var d = JsonSerializer.Deserialize<Dictionary<string, string>>(BodyCodec.Decompress(packed)) ?? [];
        return new Dictionary<string, string>(d, StringComparer.OrdinalIgnoreCase);
    }

    public static readonly JsonSerializerOptions SummaryJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Applies a diff result (verdict, kinds, summary, packed full diff).</summary>
    public void ApplyDiff(DiffResult diff, Verdict verdict, int rulesVersion)
    {
        Verdict = verdict;
        DiffKinds = (int)diff.Kinds;
        DiffCount = diff.TotalCount;
        Signature = verdict == Verdict.Mismatch ? diff.Signature : 0;
        RulesVersion = rulesVersion;
        SummaryJson = JsonSerializer.Serialize(diff.Summary, SummaryJsonOptions);
        Diff = diff.Differences.Count == 0 ? null : BodyCodec.Compress(JsonSerializer.SerializeToUtf8Bytes(diff.Differences, SummaryJsonOptions));
        if (diff.Note is not null && Note is null) Note = diff.Note;
    }
}
