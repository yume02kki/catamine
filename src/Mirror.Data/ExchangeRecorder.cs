using System.IO.Compression;
using Mirror.Core.Compression;
using Mirror.Core.Diffing;
using Mirror.Core.Http;
using Mirror.Core.Rules;

namespace Mirror.Data;

/// <summary>One side of an exchange. <see cref="Body"/> may still be content-encoded (see <see cref="Headers"/>).</summary>
public sealed record ExchangeSide(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body, double Ms);

public sealed record ExchangeRequest(
    Guid MissionId, string Method, string Path, string? Query,
    IEnumerable<KeyValuePair<string, string>> Headers, byte[]? Body);

public sealed record RecordOptions(bool StoreBodies, LogSource Source, Guid? JobId = null, Guid? ReplayOf = null, bool RedactHeaders = true, string? Note = null);

/// <summary>Turns a captured Main/Dev exchange into a diffed <see cref="LogRecord"/>. Shared by the proxy workers and the api's replay/fuzz jobs.</summary>
public static class ExchangeRecorder
{
    public static (LogRecord Record, DiffResult? Diff) Record(
        ExchangeRequest req, ExchangeSide main, ExchangeSide? dev, DevOutcome outcome, string? devError,
        CompiledRuleSet rules, DiffSettings settings, RecordOptions options, (Guid Id, DateTime Ts)? identity = null)
    {
        var (id, ts) = identity ?? LogRecord.NewId();
        string? note = devError ?? options.Note;

        var mainBody = Decode(main.Body, main.Headers, settings.MaxDiffBodyBytes, ref note);
        var devBody = dev is null ? [] : Decode(dev.Body, dev.Headers, settings.MaxDiffBodyBytes, ref note);

        DiffResult? diff = null;
        if (outcome == DevOutcome.Ok && dev is not null)
        {
            diff = DiffEngine.Compare(new DiffInput
            {
                MainStatus = main.Status,
                DevStatus = dev.Status,
                MainBody = mainBody,
                DevBody = devBody,
                MainHeaders = main.Headers,
                DevHeaders = dev.Headers,
            }, rules, settings);
        }
        var verdict = DiffEngine.DecideVerdict(outcome, main.Status, dev?.Status ?? 0, diff);

        var record = new LogRecord
        {
            Id = id,
            MissionId = req.MissionId,
            Ts = ts,
            Method = req.Method,
            Path = req.Path,
            Query = string.IsNullOrEmpty(req.Query) ? null : req.Query.TrimStart('?'),
            MainStatus = (short)main.Status,
            DevStatus = dev is null || outcome != DevOutcome.Ok ? null : (short)dev.Status,
            MainMs = (float)main.Ms,
            DevMs = dev is null ? null : (float)dev.Ms,
            Verdict = verdict,
            RulesVersion = rules.Version,
            Source = options.Source,
            JobId = options.JobId,
            ReplayOf = options.ReplayOf,
            Note = note,
            RequestHeaders = LogRecord.PackHeaders(options.RedactHeaders ? OutboundRequest.Redact(req.Headers) : req.Headers),
            RequestBody = options.StoreBodies ? BodyCodec.Compress(req.Body) : null,
            MainHeaders = LogRecord.PackHeaders(options.RedactHeaders ? OutboundRequest.Redact(main.Headers) : main.Headers),
            MainBody = options.StoreBodies ? BodyCodec.Compress(mainBody) : null,
            DevHeaders = dev is null ? null : LogRecord.PackHeaders(options.RedactHeaders ? OutboundRequest.Redact(dev.Headers) : dev.Headers),
            DevBody = options.StoreBodies && dev is not null ? BodyCodec.Compress(devBody) : null,
        };
        if (diff is not null) record.ApplyDiff(diff, verdict, rules.Version);
        return (record, diff);
    }

    private static byte[] Decode(byte[] body, IReadOnlyDictionary<string, string> headers, int max, ref string? note)
    {
        headers.TryGetValue("content-encoding", out var enc);
        if (string.IsNullOrEmpty(enc)) return body;
        try
        {
            return BodyCodec.DecodeContent(body, enc, max, out _);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            note ??= $"could not decode {enc} body: {ex.Message}";
            return body;
        }
    }
}
