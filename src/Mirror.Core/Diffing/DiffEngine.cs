using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mirror.Core.Rules;

namespace Mirror.Core.Diffing;

public enum DevOutcome { Ok, Timeout, Error }

/// <summary>Everything needed to compare one Main/Dev exchange. Bodies must already be content-decoded.</summary>
public sealed class DiffInput
{
    public int MainStatus { get; init; }
    public int DevStatus { get; init; }
    public ReadOnlyMemory<byte> MainBody { get; init; }
    public ReadOnlyMemory<byte> DevBody { get; init; }
    public IReadOnlyDictionary<string, string>? MainHeaders { get; init; }
    public IReadOnlyDictionary<string, string>? DevHeaders { get; init; }
}

public static class DiffEngine
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };
    private static readonly JsonDocumentOptions DocOptions = new() { MaxDepth = 512, CommentHandling = JsonCommentHandling.Skip };

    public static DiffResult Compare(DiffInput input, CompiledRuleSet rules, DiffSettings? settings = null)
    {
        settings ??= DiffSettings.Default;
        var c = new DiffCollector(settings);
        string? note = null;

        if (input.MainStatus != input.DevStatus)
            c.Add(DiffKind.Status, "status", "status",
                input.MainStatus.ToString(CultureInfo.InvariantCulture), input.DevStatus.ToString(CultureInfo.InvariantCulture));

        CompareHeaders(input, rules, settings, c);

        var mainLen = input.MainBody.Length;
        var devLen = input.DevBody.Length;
        if (mainLen == 0 && devLen == 0)
        {
            // nothing to compare (HEAD, 204…)
        }
        else if (mainLen > settings.MaxDiffBodyBytes || devLen > settings.MaxDiffBodyBytes)
        {
            note = $"body larger than {settings.MaxDiffBodyBytes / 1024} KB: compared byte-for-byte only";
            if (!input.MainBody.Span.SequenceEqual(input.DevBody.Span))
                c.Add(DiffKind.Body, "body", "body", $"{mainLen} bytes", $"{devLen} bytes");
        }
        else
        {
            var mainText = Encoding.UTF8.GetString(input.MainBody.Span);
            var devText = Encoding.UTF8.GetString(input.DevBody.Span);
            if (rules.HasTextStage)
            {
                mainText = rules.ApplyText(mainText, Side.Main);
                devText = rules.ApplyText(devText, Side.Dev);
            }

            var mainOk = TryParse(mainText, out var mainDoc);
            var devOk = TryParse(devText, out var devDoc);
            if (mainOk && devOk)
            {
                if (rules.HasDocumentStage)
                {
                    mainDoc = rules.ApplyDocument(mainDoc, Side.Main);
                    devDoc = rules.ApplyDocument(devDoc, Side.Dev);
                }
                JsonComparer.Compare(mainDoc, devDoc, rules.Options, c);
            }
            else if (!string.Equals(mainText, devText, StringComparison.Ordinal))
            {
                note = !mainOk && !devOk ? "neither body is JSON: compared as text" : (mainOk ? "Dev body is not JSON" : "Main body is not JSON");
                c.Add(DiffKind.Body, "body", "body",
                    DiffCollector.Shorten(mainText, settings.ValueSnippetLength), DiffCollector.Shorten(devText, settings.ValueSnippetLength));
            }
        }

        c.Summary.Truncated = c.Truncated;
        c.Summary.Note = note;
        return new DiffResult
        {
            Differences = c.Differences,
            Kinds = c.Kinds,
            TotalCount = c.Total,
            Truncated = c.Truncated,
            Summary = c.Summary,
            Signature = c.ComputeSignature(),
            Note = note,
        };
    }

    private static void CompareHeaders(DiffInput input, CompiledRuleSet rules, DiffSettings settings, DiffCollector c)
    {
        foreach (var name in settings.ComparedHeaders)
        {
            if (rules.Options.IgnoredHeaders.Contains(name)) continue;
            var m = Get(input.MainHeaders, name);
            var d = Get(input.DevHeaders, name);
            if (!string.Equals(Normalize(m), Normalize(d), StringComparison.OrdinalIgnoreCase))
            {
                var p = "header:" + name.ToLowerInvariant();
                c.Add(DiffKind.Header, p, p, m, d);
            }
        }
    }

    private static string? Normalize(string? headerValue) => headerValue?.Replace(" ", "", StringComparison.Ordinal);

    private static string? Get(IReadOnlyDictionary<string, string>? headers, string name)
    {
        if (headers is null) return null;
        if (headers.TryGetValue(name, out var v)) return v;
        foreach (var (k, val) in headers)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return val;
        return null;
    }

    private static bool TryParse(string text, out JsonNode? node)
    {
        node = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            node = JsonNode.Parse(text, NodeOptions, DocOptions);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Decides the log verdict from the Dev call outcome and the diff.</summary>
    public static Verdict DecideVerdict(DevOutcome outcome, int mainStatus, int devStatus, DiffResult? diff)
    {
        if (outcome == DevOutcome.Timeout) return Verdict.DevTimeout;
        if (outcome == DevOutcome.Error) return Verdict.DevError;
        if (devStatus >= 500 && mainStatus < 500) return Verdict.DevError;
        return diff is { IsEqual: false } ? Verdict.Mismatch : Verdict.Match;
    }

    /// <summary>Body after the text and document rule stages, pretty-printed; for the UI's "normalized" view.</summary>
    public static string? Normalize(ReadOnlyMemory<byte> body, Side side, CompiledRuleSet rules)
    {
        if (body.IsEmpty) return null;
        var text = rules.ApplyText(Encoding.UTF8.GetString(body.Span), side);
        if (!TryParse(text, out var doc)) return text;
        doc = rules.ApplyDocument(doc, side);
        return doc?.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";
    }
}
