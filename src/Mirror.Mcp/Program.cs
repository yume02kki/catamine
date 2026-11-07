using System.Globalization;
using Mirror.Mcp.Features;
using Mirror.Mcp.Protocol;
using Mirror.Mcp.Transports;

// KittyParty MCP server. Talks to the KittyParty api; exposes it to MCP clients (LM Studio, Open WebUI, Jan,
// AnythingLLM, Ollama-based clients such as ollmcp/mcphost, Claude Desktop, …).
//
//   kittyparty-mcp                       stdio (default; the client starts the process)
//   kittyparty-mcp --http --port 8090    Streamable HTTP on /mcp (+ legacy SSE on /sse)
//
// Environment: KITTYPARTY_API_URL, KITTYPARTY_MCP_TRANSPORT (stdio|http), KITTYPARTY_MCP_HOST, KITTYPARTY_MCP_PORT,
// KITTYPARTY_MCP_READONLY, KITTYPARTY_MCP_MAX_CHARS, KITTYPARTY_MCP_ALLOWED_ORIGINS (comma separated).

var cli = CliOptions.Parse(args);
if (cli.Help)
{
    Console.Error.WriteLine("""
        kittyparty-mcp — MCP server for KittyParty (API Mirror & Diff)

        Options (each also has an environment variable):
          --stdio | --http               transport (KITTYPARTY_MCP_TRANSPORT, default stdio)
          --host <ip>                    HTTP bind address (KITTYPARTY_MCP_HOST, default 127.0.0.1)
          --port <n>                     HTTP port (KITTYPARTY_MCP_PORT, default 8090)
          --api-url <url>                KittyParty api (KITTYPARTY_API_URL, default http://localhost:8081)
          --read-only                    hide tools that change data (KITTYPARTY_MCP_READONLY)
          --max-chars <n>                cap on text per tool result (KITTYPARTY_MCP_MAX_CHARS, default 8000)
          --allowed-origins <a,b>        extra browser origins for HTTP (KITTYPARTY_MCP_ALLOWED_ORIGINS)
        """);
    return;
}
if (cli.Version)
{
    Console.WriteLine("kittyparty-mcp 2.0.0");
    return;
}

var options = cli.ToOptions();

if (cli.Transport == "stdio")
{
    // stdout belongs to the protocol: every log line goes to stderr.
    using var loggerFactory = LoggerFactory.Create(b => b
        .SetMinimumLevel(cli.Verbose ? LogLevel.Debug : LogLevel.Warning)
        .AddSimpleConsole(o => o.SingleLine = true)
        .AddFilter("System.Net.Http", LogLevel.Warning));
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    var server = Compose(http, options, loggerFactory);
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    await new StdioTransport(server, loggerFactory.CreateLogger<StdioTransport>())
        .RunAsync(Console.OpenStandardInput(), Console.OpenStandardOutput(), cts.Token);
    return;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.WebHost.UseUrls($"http://{cli.Host}:{cli.Port}");
builder.Services.AddSingleton(options);
builder.Services.AddHttpClient("kittyparty", c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddSingleton(sp => Compose(sp.GetRequiredService<IHttpClientFactory>().CreateClient("kittyparty"), options, sp.GetRequiredService<ILoggerFactory>()));
var app = builder.Build();
app.MapMcp(app.Services.GetRequiredService<McpServer>(), options);
app.Logger.LogInformation("KittyParty MCP on http://{Host}:{Port}/mcp (legacy SSE: /sse), api {Api}{ReadOnly}", cli.Host, cli.Port, options.ApiUrl, options.ReadOnly ? ", read-only" : "");
app.Run();

static McpServer Compose(HttpClient http, McpOptions options, ILoggerFactory logs)
{
    var api = new KittyApi(http, options);
    var tools = MirrorTools.Create(api).ToList();
    return new McpServer(new ToolRegistry(tools, options), new ResourceProvider(api, tools), new PromptProvider(), options, logs.CreateLogger<McpServer>());
}

internal sealed record CliOptions(string Transport, string Host, int Port, string ApiUrl, bool ReadOnly, int MaxChars, string[] Origins, bool Help, bool Version, bool Verbose)
{
    public static CliOptions Parse(string[] args)
    {
        string Env(string k, string d) => Environment.GetEnvironmentVariable(k) is { Length: > 0 } v ? v : d;
        var transport = Env("KITTYPARTY_MCP_TRANSPORT", "stdio").ToLowerInvariant();
        var host = Env("KITTYPARTY_MCP_HOST", "127.0.0.1");
        var port = int.Parse(Env("KITTYPARTY_MCP_PORT", "8090"), CultureInfo.InvariantCulture);
        var api = Env("KITTYPARTY_API_URL", "http://localhost:8081");
        var ro = Env("KITTYPARTY_MCP_READONLY", "false") is "1" or "true" or "yes";
        var max = int.Parse(Env("KITTYPARTY_MCP_MAX_CHARS", "8000"), CultureInfo.InvariantCulture);
        var origins = Env("KITTYPARTY_MCP_ALLOWED_ORIGINS", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool help = false, version = false, verbose = false;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--stdio": transport = "stdio"; break;
                case "--http": transport = "http"; break;
                case "--transport": transport = Next().ToLowerInvariant(); break;
                case "--host": host = Next(); break;
                case "--port": port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--api-url" or "--api": api = Next(); break;
                case "--read-only": ro = true; break;
                case "--max-chars": max = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--allowed-origins": origins = Next().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries); break;
                case "--verbose" or "-v": verbose = true; break;
                case "--help" or "-h": help = true; break;
                case "--version": version = true; break;
            }
        }
        if (transport is not ("stdio" or "http")) throw new ArgumentException("transport must be stdio or http");
        return new CliOptions(transport, host, port, api, ro, Math.Clamp(max, 500, 200_000), origins, help, version, verbose);
    }

    public McpOptions ToOptions() => new() { ApiUrl = ApiUrl, ReadOnly = ReadOnly, MaxChars = MaxChars, AllowedOrigins = Origins };
}

public partial class Program;
