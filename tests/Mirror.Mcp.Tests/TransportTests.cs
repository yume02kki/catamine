using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mirror.Mcp.Protocol;
using Mirror.Mcp.Transports;

namespace Mirror.Mcp.Tests;

public class StdioTransportTests : IAsyncLifetime
{
    private FakeApi _api = null!;
    public async Task InitializeAsync() => _api = await FakeApi.StartAsync();
    public async Task DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task Newline_delimited_messages_round_trip_and_stdout_has_only_json()
    {
        var input = new Pipe();
        var output = new MemoryStream();
        var transport = new StdioTransport(_api.CreateServer(), NullLogger<StdioTransport>.Instance);
        var run = transport.RunAsync(input.Reader.AsStream(), output, default);

        var lines = new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_missions","arguments":{}}}""",
            "this is not json",
            """{"jsonrpc":"2.0","id":"three","method":"ping"}""",
        };
        await input.Writer.WriteAsync(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        await input.Writer.CompleteAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        var responses = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        Assert.Equal(4, responses.Count);
        Assert.Equal("2025-06-18", (string?)responses.Single(r => r["id"]?.ToJsonString() == "1")["result"]!["protocolVersion"]);
        Assert.Contains("echo-apis", responses.Single(r => r["id"]?.ToJsonString() == "2")["result"]!["content"]![0]!["text"]!.ToString());
        Assert.Equal(RpcError.ParseError, (int)responses.Single(r => r["error"] is not null)["error"]!["code"]!);
        Assert.NotNull(responses.Single(r => (string?)r["id"]?.ToString() == "three")["result"]);
    }
}

public class HttpTransportTests : IAsyncLifetime
{
    private FakeApi _api = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _api = await FakeApi.StartAsync();
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        _app = b.Build();
        _app.MapMcp(_api.CreateServer(), new McpOptions { ApiUrl = _api.Url, AllowedOrigins = ["https://ui.example"] });
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _api.DisposeAsync();
    }

    private static StringContent Json(string s) => new(s, Encoding.UTF8, "application/json");

    private async Task<(HttpResponseMessage Resp, JsonNode? Body)> PostAsync(string json, string? session = null, string? origin = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = Json(json) };
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        if (session is not null) req.Headers.Add("Mcp-Session-Id", session);
        if (origin is not null) req.Headers.Add("Origin", origin);
        var resp = await _http.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp, text.Length == 0 ? null : JsonNode.Parse(text));
    }

    [Fact]
    public async Task Streamable_http_session_lifecycle()
    {
        var (init, body) = await PostAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{}}}""");
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        Assert.Equal("2025-03-26", (string?)body!["result"]!["protocolVersion"]);
        var sid = init.Headers.GetValues("Mcp-Session-Id").Single();

        var (note, noteBody) = await PostAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""", sid);
        Assert.Equal(HttpStatusCode.Accepted, note.StatusCode);
        Assert.Null(noteBody);

        var (list, listBody) = await PostAsync("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", sid);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("application/json", list.Content.Headers.ContentType!.MediaType);
        Assert.True(listBody!["result"]!["tools"]!.AsArray().Count >= 20);
        Assert.NotNull(listBody["result"]!["tools"]![0]!["annotations"]); // negotiated 2025-03-26

        var (unknown, _) = await PostAsync("""{"jsonrpc":"2.0","id":3,"method":"ping"}""", "not-a-session");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var del = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
        del.Headers.Add("Mcp-Session-Id", sid);
        Assert.Equal(HttpStatusCode.OK, (await _http.SendAsync(del)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync("""{"jsonrpc":"2.0","id":4,"method":"ping"}""", sid)).Resp.StatusCode);
    }

    [Fact]
    public async Task Stateless_requests_and_batches_work_without_a_session()
    {
        var (resp, body) = await PostAsync("""[{"jsonrpc":"2.0","id":1,"method":"ping"},{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_missions"}}]""");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, body!.AsArray().Count);
    }

    [Fact]
    public async Task Origins_versions_and_methods_are_checked()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await PostAsync("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", origin: "https://evil.example")).Resp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", origin: "http://localhost:3000")).Resp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostAsync("""{"jsonrpc":"2.0","id":1,"method":"ping"}""", origin: "https://ui.example")).Resp.StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = Json("""{"jsonrpc":"2.0","id":1,"method":"ping"}""") };
        req.Headers.Add("MCP-Protocol-Version", "1999-01-01");
        Assert.Equal(HttpStatusCode.BadRequest, (await _http.SendAsync(req)).StatusCode);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _http.GetAsync("/mcp")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostAsync("{not json")).Resp.StatusCode);
    }

    [Fact]
    public async Task Legacy_sse_transport_delivers_responses_on_the_stream()
    {
        using var stream = await _http.GetStreamAsync("/sse");
        using var reader = new StreamReader(stream);
        async Task<(string Event, string Data)> NextEventAsync()
        {
            string? ev = null, data = null, line;
            while ((line = await reader.ReadLineAsync()) is not null)
            {
                if (line.StartsWith("event: ")) ev = line[7..];
                else if (line.StartsWith("data: ")) data = line[6..];
                else if (line.Length == 0 && ev is not null) return (ev, data!);
            }
            throw new EndOfStreamException();
        }
        var (endpointEvent, endpoint) = await NextEventAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("endpoint", endpointEvent);
        Assert.StartsWith("/messages?sessionId=", endpoint);

        var post = await _http.PostAsync(endpoint, Json("""{"jsonrpc":"2.0","id":7,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{}}}"""));
        Assert.Equal(HttpStatusCode.Accepted, post.StatusCode);
        var (ev, data) = await NextEventAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("message", ev);
        var msg = JsonNode.Parse(data)!;
        Assert.Equal(7, (int)msg["id"]!);
        Assert.Equal("2024-11-05", (string?)msg["result"]!["protocolVersion"]);

        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsync("/messages?sessionId=nope", Json("{}"))).StatusCode);
    }
}
