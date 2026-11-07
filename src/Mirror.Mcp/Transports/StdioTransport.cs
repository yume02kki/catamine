using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mirror.Mcp.Protocol;

namespace Mirror.Mcp.Transports;

/// <summary>
/// MCP stdio transport: one JSON-RPC message per line on stdin/stdout, logs on stderr only.
/// Requests run concurrently (so a slow tool doesn't block ping or cancellation); initialize is handled inline.
/// </summary>
public sealed class StdioTransport(McpServer server, ILogger<StdioTransport> log)
{
    public async Task RunAsync(Stream input, Stream output, CancellationToken ct)
    {
        var session = new McpSession();
        var gate = new SemaphoreSlim(1, 1);
        var running = new ConcurrentDictionary<int, Task>();
        var seq = 0;

        async Task SendAsync(JsonNode message)
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                await output.WriteAsync(bytes, CancellationToken.None);
                await output.FlushAsync(CancellationToken.None);
            }
            finally
            {
                gate.Release();
            }
        }

        session.Notify = msg => SendAsync(msg);
        using var reader = new StreamReader(input, new UTF8Encoding(false));
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct); }
            catch (OperationCanceledException) { break; }
            if (line is null) break; // client closed stdin
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (JsonException ex)
            {
                await SendAsync(McpServer.Error(null, RpcError.ParseError, "parse error: " + ex.Message));
                continue;
            }

            if (message is JsonObject o && (string?)o["method"] == "initialize")
            {
                // Inline so nothing races ahead of the handshake.
                if (await server.HandleAsync(message, session, ct) is { } init) await SendAsync(init);
                continue;
            }

            var id = Interlocked.Increment(ref seq);
            running[id] = Task.Run(async () =>
            {
                try
                {
                    if (await server.HandleAsync(message, session, ct) is { } response) await SendAsync(response);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "stdio message failed");
                }
                finally
                {
                    running.TryRemove(id, out _);
                }
            }, CancellationToken.None);
        }
        await Task.WhenAll(running.Values);
    }
}
