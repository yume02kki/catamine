using System.Buffers;
using System.Collections.Frozen;
using System.Net.Http.Headers;

namespace Mirror.Core.Http;

public sealed record CapturedResponse(int Status, Dictionary<string, string> Headers, byte[] Body, bool Overflow);

/// <summary>Builds outbound copies of captured requests (for Dev, replays and fuzzing) and reads responses with a size cap.</summary>
public static class OutboundRequest
{
    public static readonly FrozenSet<string> HopByHop = new[]
    {
        "connection", "keep-alive", "proxy-connection", "transfer-encoding", "te", "trailer", "upgrade", "host",
        "content-length", "expect", "http2-settings",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Headers never persisted in clear text by default.</summary>
    public static readonly FrozenSet<string> SensitiveHeaders = new[]
    {
        "authorization", "proxy-authorization", "cookie", "set-cookie", "x-api-key", "x-auth-token",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public const string Redacted = "[redacted]";

    public static HttpRequestMessage Build(
        string method, string baseUrl, string path, string? query,
        IEnumerable<KeyValuePair<string, string>> headers, byte[]? body,
        IReadOnlySet<string>? strip = null, IReadOnlyDictionary<string, string>? set = null,
        IEnumerable<KeyValuePair<string, string>>? extra = null)
    {
        var url = baseUrl.TrimEnd('/') + (path.StartsWith('/') ? path : "/" + path) +
                  (string.IsNullOrEmpty(query) ? "" : (query[0] == '?' ? query : "?" + query));
        var req = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is { Length: > 0 }) req.Content = new ByteArrayContent(body);

        void Add(string name, string value)
        {
            if (!req.Headers.TryAddWithoutValidation(name, value) && req.Content is not null)
                req.Content.Headers.TryAddWithoutValidation(name, value);
        }

        foreach (var (name, value) in headers)
        {
            if (HopByHop.Contains(name)) continue;
            if (strip is not null && strip.Contains(name)) continue;
            if (set is not null && set.ContainsKey(name)) continue;
            if (value == Redacted) continue;
            Add(name, value);
        }
        if (set is not null)
            foreach (var (name, value) in set) Add(name, value);
        if (extra is not null)
            foreach (var (name, value) in extra)
            {
                req.Headers.Remove(name);
                Add(name, value);
            }
        return req;
    }

    public static async Task<CapturedResponse> ReadAsync(HttpResponseMessage resp, int maxBytes, CancellationToken ct)
    {
        var headers = Flatten(resp.Headers, resp.Content.Headers);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        var overflow = false;
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (ms.Length + n > maxBytes) { overflow = true; break; }
                ms.Write(buffer, 0, n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return new CapturedResponse((int)resp.StatusCode, headers, overflow ? [] : ms.ToArray(), overflow);
    }

    public static Dictionary<string, string> Flatten(HttpResponseHeaders h, HttpContentHeaders? c)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in h) d[k] = string.Join(", ", v);
        if (c is not null)
            foreach (var (k, v) in c) d[k] = string.Join(", ", v);
        return d;
    }

    /// <summary>Copy of the headers with sensitive values replaced, for storage.</summary>
    public static IEnumerable<KeyValuePair<string, string>> Redact(IEnumerable<KeyValuePair<string, string>> headers) =>
        headers.Select(h => SensitiveHeaders.Contains(h.Key) ? new KeyValuePair<string, string>(h.Key, Redacted) : h);

    public static string? Get(IEnumerable<KeyValuePair<string, string>> headers, string name)
    {
        foreach (var (k, v) in headers)
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }
}
