using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mirror.Mcp.Protocol;

namespace Mirror.Mcp.Features;

public sealed class ApiException(string message, int status = 0) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Thin client for the KittyParty api service. The MCP server never talks to Postgres or the proxy directly.</summary>
public sealed class KittyApi
{
    private readonly HttpClient _http;
    private readonly string _base;
    private JsonArray? _missions;
    private DateTime _missionsAt;

    public KittyApi(HttpClient http, McpOptions options)
    {
        _http = http;
        _base = options.ApiUrl.TrimEnd('/');
    }

    public string BaseUrl => _base;

    public Task<JsonNode> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);
    public Task<JsonNode> PostAsync(string path, JsonNode? body, CancellationToken ct) => SendAsync(HttpMethod.Post, path, body, ct);
    public Task<JsonNode> PutAsync(string path, JsonNode body, CancellationToken ct) => SendAsync(HttpMethod.Put, path, body, ct);
    public Task<JsonNode> DeleteAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Delete, path, null, ct);

    public async Task<string> GetTextAsync(string path, CancellationToken ct)
    {
        using var resp = await SendRawAsync(HttpMethod.Get, path, null, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var resp = await SendRawAsync(method, path, body, ct);
        if (resp.StatusCode == HttpStatusCode.NoContent) return new JsonObject();
        var text = await resp.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) ?? new JsonObject();
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, _base + path);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException($"the KittyParty api is not reachable at {_base} ({ex.Message}). Is it running? Set KITTYPARTY_API_URL.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ApiException($"the KittyParty api at {_base} did not answer in time");
        }
        if (resp.IsSuccessStatusCode) return resp;

        var detail = await resp.Content.ReadAsStringAsync(ct);
        resp.Dispose();
        throw new ApiException(Describe((int)resp.StatusCode, detail), (int)resp.StatusCode);
    }

    private static string Describe(int status, string body)
    {
        try
        {
            var n = JsonNode.Parse(body);
            if (n?["errors"] is JsonObject errors)
                return string.Join("; ", errors.Select(e => $"{e.Key}: {string.Join(" ", (e.Value as JsonArray ?? []).Select(x => (string?)x))}"));
            if ((string?)n?["error"] is { } e) return e;
            if ((string?)n?["title"] is { } t) return t;
        }
        catch (JsonException) { }
        return status == 404 ? "not found" : $"HTTP {status}";
    }

    public async Task<JsonArray> MissionsAsync(CancellationToken ct, bool fresh = false)
    {
        if (fresh || _missions is null || DateTime.UtcNow - _missionsAt > TimeSpan.FromSeconds(5))
        {
            _missions = (JsonArray)await GetAsync("/api/missions", ct);
            _missionsAt = DateTime.UtcNow;
        }
        return _missions;
    }

    public void InvalidateMissions() => _missions = null;

    /// <summary>Finds a mission by id, slug or name (exact, then unique partial match). Small models rarely know ids.</summary>
    public async Task<JsonObject> ResolveMissionAsync(string reference, CancellationToken ct)
    {
        var all = (await MissionsAsync(ct)).OfType<JsonObject>().ToList();
        var r = reference.Trim().Trim('"', '\'', '/');
        if (r.StartsWith("m/", StringComparison.OrdinalIgnoreCase)) r = r[2..];
        bool Eq(JsonObject m, string key) => string.Equals((string?)m[key], r, StringComparison.OrdinalIgnoreCase);
        var hit = all.FirstOrDefault(m => Eq(m, "id")) ?? all.FirstOrDefault(m => Eq(m, "slug")) ?? all.FirstOrDefault(m => Eq(m, "name"));
        if (hit is not null) return hit;

        var norm = Normalize(r);
        var partial = all.Where(m => Normalize((string?)m["slug"]).Contains(norm) || Normalize((string?)m["name"]).Contains(norm)).ToList();
        if (partial.Count == 1) return partial[0];
        if (partial.Count == 0 && norm.Length >= 4)
        {
            // Near miss ("pokemon" → "pokeapi"): unique mission sharing the first 4 letters of the slug or name.
            var prefix = all.Where(m => Normalize((string?)m["slug"]).StartsWith(norm[..4]) || Normalize((string?)m["name"]).StartsWith(norm[..4])).ToList();
            if (prefix.Count == 1) return prefix[0];
        }
        var names = string.Join(", ", all.Select(m => (string?)m["slug"]));
        if (partial.Count > 1)
            throw new ToolException($"'{reference}' matches several missions: {string.Join(", ", partial.Select(m => (string?)m["slug"]))}. Use one of these slugs.");
        throw new ToolException(all.Count == 0
            ? "there are no missions yet (create_mission creates one)"
            : $"no mission called '{reference}'. Available: {names}");
    }

    /// <summary>Lower case, accents folded ("PokéAPI" → "pokeapi"), letters and digits only.</summary>
    /// <remarks>The app runs with invariant globalization, where string.Normalize is a no-op, so Latin accents are folded by table.</remarks>
    internal static string Normalize(string? s)
    {
        var sb = new StringBuilder((s ?? "").Length);
        foreach (var ch in (s ?? "").ToLowerInvariant())
        {
            var c = Fold(ch);
            if (c == 'ß') sb.Append("ss");
            else if (c == 'æ') sb.Append("ae");
            else if (c == 'œ') sb.Append("oe");
            else if (char.IsLetterOrDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private const string Accented = "àáâãäåāăąçćĉċčďđèéêëēĕėęěĝğġģĥħìíîïĩīĭįıĵķĺļľŀłñńņňòóôõöøōŏőŕŗřśŝşšţťŧùúûüũūŭůűųŵýÿŷźżžð";
    private const string Plain    = "aaaaaaaaacccccddeeeeeeeeegggghhiiiiiiiiijklllllnnnnooooooooorrrsssstttuuuuuuuuuuwyyyzzzd";

    private static char Fold(char c)
    {
        var i = Accented.IndexOf(c);
        return i < 0 ? c : Plain[i];
    }

    public static JsonObject Body(object anonymous) => JsonSerializer.SerializeToNode(anonymous, JsonSerializerOptions.Web)!.AsObject();
}
