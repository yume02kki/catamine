using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Mirror.Mcp.Protocol;

namespace Mirror.Mcp.Transports;

/// <summary>
/// HTTP transports:
/// <list type="bullet">
/// <item>Streamable HTTP (MCP 2025-03-26 / 2025-06-18): <c>POST /mcp</c> (JSON responses), <c>DELETE /mcp</c> ends the session, <c>GET /mcp</c> → 405.</item>
/// <item>HTTP+SSE (MCP 2024-11-05) for older clients: <c>GET /sse</c> stream + <c>POST /messages?sessionId=…</c>.</item>
/// </list>
/// Origin is checked to stop DNS-rebinding attacks from web pages; by default only localhost origins are allowed.
/// </summary>
public static class HttpTransport
{
    private const string SessionHeader = "Mcp-Session-Id";
    private const string VersionHeader = "MCP-Protocol-Version";
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(1);

    private sealed class SseSession(McpSession session)
    {
        public McpSession Session { get; } = session;
        public Channel<string> Outbox { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
    }

    public static void MapMcp(this WebApplication app, McpServer server, McpOptions options)
    {
        var sessions = new ConcurrentDictionary<string, McpSession>();
        var sse = new ConcurrentDictionary<string, SseSession>();

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok", server = "kittyparty-mcp" }));

        app.MapPost("/mcp", async (HttpContext ctx) =>
        {
            if (!OriginAllowed(ctx, options)) return Results.Json(McpServer.Error(null, RpcError.InvalidRequest, "origin not allowed"), statusCode: 403);
            if (ctx.Request.Headers[VersionHeader] is { Count: > 0 } pv && !McpServer.SupportedProtocolVersions.Contains(pv.ToString()))
                return Results.Json(McpServer.Error(null, RpcError.InvalidRequest, $"unsupported protocol version {pv}"), statusCode: 400);

            JsonNode? message;
            try { message = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
            catch (JsonException ex) { return Results.Json(McpServer.Error(null, RpcError.ParseError, "parse error: " + ex.Message), statusCode: 400); }

            var isInit = IsInitialize(message);
            McpSession session;
            var sid = ctx.Request.Headers[SessionHeader].ToString();
            if (isInit)
            {
                Prune(sessions);
                session = new McpSession();
            }
            else if (sid.Length > 0)
            {
                if (!sessions.TryGetValue(sid, out session!))
                    return Results.Json(McpServer.Error(null, RpcError.InvalidRequest, "unknown or expired session; initialize again"), statusCode: 404);
            }
            else
            {
                // Lenient for simple clients and curl: a one-off session.
                session = new McpSession { ProtocolVersion = ctx.Request.Headers[VersionHeader].FirstOrDefault() ?? McpServer.LatestProtocolVersion, Initialized = true };
            }

            var response = await server.HandleAsync(message, session, ctx.RequestAborted);
            if (isInit)
            {
                sessions[session.Id] = session;
                ctx.Response.Headers[SessionHeader] = session.Id;
            }
            if (response is null) return Results.Accepted();
            return Results.Content(response.ToJsonString(), "application/json", Encoding.UTF8);
        });

        app.MapGet("/mcp", (HttpContext ctx) =>
        {
            ctx.Response.Headers.Allow = "POST, DELETE";
            return Results.StatusCode(405); // no server-initiated stream: this server never sends requests to the client
        });

        app.MapDelete("/mcp", (HttpContext ctx) =>
            sessions.TryRemove(ctx.Request.Headers[SessionHeader].ToString(), out _) ? Results.Ok() : Results.NotFound());

        // ── legacy HTTP+SSE transport ──
        app.MapGet("/sse", async (HttpContext ctx) =>
        {
            if (!OriginAllowed(ctx, options)) { ctx.Response.StatusCode = 403; return; }
            var s = new SseSession(new McpSession { ProtocolVersion = "2024-11-05" });
            s.Session.Notify = msg => s.Outbox.Writer.WriteAsync(msg.ToJsonString()).AsTask();
            sse[s.Session.Id] = s;
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            try
            {
                var basePath = ctx.Request.PathBase.Add("/messages");
                await ctx.Response.WriteAsync($"event: endpoint\ndata: {basePath}?sessionId={s.Session.Id}\n\n", ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                while (!ctx.RequestAborted.IsCancellationRequested)
                {
                    using var keepAlive = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
                    keepAlive.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        var json = await s.Outbox.Reader.ReadAsync(keepAlive.Token);
                        await ctx.Response.WriteAsync($"event: message\ndata: {json}\n\n", ctx.RequestAborted);
                    }
                    catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
                    {
                        await ctx.Response.WriteAsync(": keep-alive\n\n", ctx.RequestAborted);
                    }
                    await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                sse.TryRemove(s.Session.Id, out _);
            }
        });

        app.MapPost("/messages", async (HttpContext ctx, string? sessionId) =>
        {
            if (!OriginAllowed(ctx, options)) return Results.StatusCode(403);
            if (sessionId is null || !sse.TryGetValue(sessionId, out var s)) return Results.NotFound("unknown sessionId");
            JsonNode? message;
            try { message = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ctx.RequestAborted); }
            catch (JsonException ex)
            {
                await s.Outbox.Writer.WriteAsync(McpServer.Error(null, RpcError.ParseError, "parse error: " + ex.Message).ToJsonString());
                return Results.Accepted();
            }
            _ = Task.Run(async () =>
            {
                var response = await server.HandleAsync(message, s.Session, CancellationToken.None);
                if (response is not null) await s.Outbox.Writer.WriteAsync(response.ToJsonString());
            });
            return Results.Accepted();
        });
    }

    private static bool IsInitialize(JsonNode? m) =>
        m is JsonObject o ? (string?)o["method"] == "initialize" : m is JsonArray a && a.Any(IsInitialize);

    private static void Prune(ConcurrentDictionary<string, McpSession> sessions)
    {
        var cutoff = DateTimeOffset.UtcNow - IdleTimeout;
        foreach (var (k, v) in sessions)
            if (v.LastSeen < cutoff) sessions.TryRemove(k, out _);
    }

    /// <summary>Requests without an Origin (desktop apps, CLIs) are fine; browser origins must be localhost or explicitly allowed.</summary>
    internal static bool OriginAllowed(HttpContext ctx, McpOptions options)
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length == 0) return true;
        if (options.AllowedOrigins.Contains("*") || options.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var u) && (u.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1");
    }
}
