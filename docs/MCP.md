# KittyParty MCP server

`kittyparty-mcp` (source: [`src/Mirror.Mcp`](../src/Mirror.Mcp)) is a [Model Context Protocol](https://modelcontextprotocol.io) server for KittyParty. It lets an AI assistant work with your missions: find out why Dev differs from Main, read request diffs and bodies, try and add comparison rules, replay or fuzz requests, and decide whether Dev is ready to replace Main.

It is written for **local LLMs** (Ollama, LM Studio, llama.cpp, Jan, Open WebUI, …) as well as hosted assistants:

* **Short plain-text results.** Every result is a few compact lines that a 7B model can read, capped at `--max-chars` (default 8000). Clients that read `structuredContent` (protocol 2025-06-18) also get the full JSON.
* **Missions by name.** `mission` accepts a slug, a name, an id or a near miss: `"Echo APIs"`, `"echo apis"`, `"/m/echo-apis/"`, `"pokemon"` → *PokéAPI*. If the name is ambiguous or unknown, the error lists the valid slugs, so the model can retry.
* **Forgiving arguments.** Numbers as strings (`"20"`, `"10%"`), any letter case for enum values and keys, `config` as an object or a JSON string, common mistakes fixed (`IgnoreField {"path": …}` → `{"paths": […]}`).
* **Errors the model can act on.** Tool failures come back as `isError` results that say what to do next ("api not reachable at … Is it running? Set KITTYPARTY_API_URL", "no rule '7'; the mission has 3 rules").
* **Read-only mode** (`--read-only`) hides the 10 tools that change data. This leaves 13 tools, which is easier for small models to choose from.
* **No SDK and no extra packages.** The protocol is implemented in [`src/Mirror.Mcp/Protocol`](../src/Mirror.Mcp/Protocol) and [`Transports`](../src/Mirror.Mcp/Transports) on plain ASP.NET Core and System.Text.Json.

The server only talks to the KittyParty **api** over HTTP. It never connects to Postgres or the proxy directly.

## Protocol support

| | |
|---|---|
| Protocol versions | `2025-06-18` (latest), `2025-03-26`, `2024-11-05`. Negotiated in `initialize` |
| Transports | **stdio** (newline-delimited JSON-RPC), **Streamable HTTP** (`POST/DELETE /mcp`, `Mcp-Session-Id`), **legacy HTTP+SSE** (`GET /sse` + `POST /messages?sessionId=…`) |
| Server features | tools (with annotations, titles, `structuredContent`), resources and resource templates, prompts, completions (mission slugs, rule types), logging (`logging/setLevel`, `notifications/message`) |
| JSON-RPC | batches, `ping`, `notifications/cancelled` (cancels the api call in flight), standard error codes |
| HTTP safety | binds to `127.0.0.1` by default; checks `Origin` (localhost and `--allowed-origins` only) to block DNS rebinding; rejects unknown `MCP-Protocol-Version` headers |

## Running it

The server needs a running KittyParty api (`docker compose up` gives you one at `http://localhost:8081`).

### Build

```bash
dotnet publish src/Mirror.Mcp -c Release -o ./bin/mcp
dotnet ./bin/mcp/kittyparty-mcp.dll --help
```

Use the published dll in client configs. Do not use `dotnet run`: it writes build output to stdout, which corrupts the stdio transport and is slow on every start.

### stdio (one process per client)

```bash
KITTYPARTY_API_URL=http://localhost:8081 dotnet ./bin/mcp/kittyparty-mcp.dll --stdio
# or with docker, no .NET install needed:
docker build -f deploy/docker/mcp.Dockerfile -t kittyparty-mcp .
docker run -i --rm --network host -e KITTYPARTY_API_URL=http://localhost:8081 kittyparty-mcp --stdio
```

stdout carries only JSON-RPC; logs go to stderr.

### HTTP (one server, many clients)

```bash
dotnet ./bin/mcp/kittyparty-mcp.dll --http --port 8090 --api-url http://localhost:8081
```

* Streamable HTTP: `http://127.0.0.1:8090/mcp`
* Legacy SSE (older clients): `http://127.0.0.1:8090/sse`
* Health: `http://127.0.0.1:8090/healthz`

`docker compose up` starts it as the `mcp` service on `127.0.0.1:8090`. For Kubernetes see [`deploy/k8s/mcp.yaml`](../deploy/k8s/mcp.yaml) (read-only by default, no Ingress, reach it with `kubectl port-forward`).

### Options

| flag | environment variable | default | |
|---|---|---|---|
| `--stdio` / `--http` | `KITTYPARTY_MCP_TRANSPORT` | `stdio` | transport |
| `--api-url` | `KITTYPARTY_API_URL` | `http://localhost:8081` | KittyParty api base URL |
| `--host` | `KITTYPARTY_MCP_HOST` | `127.0.0.1` | HTTP bind address (the container image sets `0.0.0.0`) |
| `--port` | `KITTYPARTY_MCP_PORT` | `8090` | HTTP port |
| `--read-only` | `KITTYPARTY_MCP_READONLY` | `false` | hide and refuse tools that change data |
| `--max-chars` | `KITTYPARTY_MCP_MAX_CHARS` | `8000` | cap on the text of one tool result. Lower it for small context windows |
| `--allowed-origins` | `KITTYPARTY_MCP_ALLOWED_ORIGINS` | – | extra browser origins allowed to call the HTTP endpoint (comma separated, `*` for any) |
| `--verbose` | | | debug logging |

**Security.** The HTTP endpoint has no authentication of its own. Keep it on loopback (the default, and how compose publishes it) or put an authenticating reverse proxy in front of it. Use `--read-only` for assistants that should only look. With write tools enabled, the server still applies the app's own safety rules: write methods are never mirrored, replayed or fuzzed unless the model passes `allow_writes=true` / `acknowledge_unsafe_methods=true`, and `delete_mission` needs `confirm` set to the mission's slug.

## Client setup

The JSON below uses the common `mcpServers` format. Replace the dll path with your own path.

```json
{
  "mcpServers": {
    "kittyparty": {
      "command": "dotnet",
      "args": ["/path/to/bin/mcp/kittyparty-mcp.dll", "--stdio"],
      "env": { "KITTYPARTY_API_URL": "http://localhost:8081", "KITTYPARTY_MCP_READONLY": "false" }
    }
  }
}
```

If the client supports remote servers, you can point it at the running HTTP server instead: `{ "mcpServers": { "kittyparty": { "url": "http://127.0.0.1:8090/mcp" } } }`.

| client | where |
|---|---|
| **LM Studio** | Program → Install → *Edit mcp.json*, paste the block above. Pick a model with tool use support. |
| **Ollama** | Ollama itself has no MCP client. Use one of the terminal clients, which accept the same `mcpServers` file: [`ollmcp`](https://github.com/jonigl/mcp-client-for-ollama) (`ollmcp --servers-json mcp.json --model qwen3:8b`) or [`mcphost`](https://github.com/mark3labs/mcphost) (`mcphost --config mcp.json -m ollama:qwen3:8b`). |
| **Open WebUI** | Admin Settings → External Tools → add an MCP (Streamable HTTP) server with URL `http://127.0.0.1:8090/mcp` (from Docker: `http://host.docker.internal:8090/mcp`, and start the server with `--host 0.0.0.0`). Older versions: expose it through `mcpo`. |
| **Jan / AnythingLLM / Cherry Studio** | MCP server settings take the same `command` / `args` / `env` fields. |
| **Continue** (VS Code / JetBrains) | `config.yaml` → `mcpServers: [{ name: kittyparty, command: dotnet, args: [/path/to/kittyparty-mcp.dll, --stdio], env: { KITTYPARTY_API_URL: http://localhost:8081 } }]` |
| **Claude Desktop / Claude Code** | Claude Desktop: `claude_desktop_config.json`, block above. Claude Code: `claude mcp add kittyparty -e KITTYPARTY_API_URL=http://localhost:8081 -- dotnet /path/to/kittyparty-mcp.dll --stdio`. |
| **Debugging** | MCP Inspector: `npx @modelcontextprotocol/inspector dotnet /path/to/kittyparty-mcp.dll --stdio` |

### Tips for local models

* Use a model trained for tool calling: Qwen 3 / Qwen 2.5 (7B or larger), Llama 3.1 / 3.2, Mistral Small, Granite. Models of 3B and smaller often choose the wrong tool.
* Give the model enough context. Ollama's default context window is small; set `num_ctx` to 16384 or more (`OLLAMA_CONTEXT_LENGTH=16384`, or `/set parameter num_ctx 16384`). The tool list alone is about 4.5k tokens, and about 2.2k with `--read-only`.
* With 8k context or less, start with `--read-only --max-chars 3000`.
* Prompts are a good starting point when the client supports them (`triage_mission`, `tune_rules`, `release_check`). Otherwise ask *"Use list_causes for echo-apis and tell me which differences are noise"*.

## Tools

Read-only (always available):

| tool | what it does |
|---|---|
| `list_missions` | every mission with 24 h volume, diff rate, Dev errors, rule count. **Start here.** |
| `get_mission` | one mission's configuration, proxy endpoint and rules |
| `mission_stats` | counts per verdict, match rate, Main vs Dev latency over N hours |
| `list_causes` | differing requests grouped by cause (same set of differing paths), each with a suggested rule. **Best first step for a mission.** |
| `search_traffic` | find requests by verdict, path (`*` wildcards), method, status, diff kind, source, time; keyset cursor for more |
| `get_request_diff` | status, latency, sizes and each difference with JSON path and both values |
| `get_response_body` | Main / Dev response body or the request body, pretty-printed; `normalized=true` applies the rules first |
| `get_rules` | the mission's rules in order, numbered |
| `list_rule_types` | every rule type with an example config |
| `preview_rule` | dry run: how many recent mismatches a new rule would resolve (or newly cause) |
| `list_jobs`, `get_job` | background jobs and their progress / summary |
| `proxy_health` | mirror queue, drops, log writer, main-path latency and Dev circuit breakers per proxy instance |

Changes data (hidden with `--read-only`):

| tool | what it does |
|---|---|
| `add_rule` | validate and append a rule, then re-diff stored requests in the background |
| `remove_rule` | remove a rule by number or id (re-diffs) |
| `replay_request` | send a logged request again to Main and Dev now and compare (write methods need `allow_writes`) |
| `start_replay` | background replay of filtered traffic at a limited rate |
| `start_fuzz` | background fuzzing of a request (nulls, wrong types, boundaries, huge/empty strings, missing/extra fields) |
| `start_rediff` | recompute every stored diff with the current rules |
| `cancel_job` | cancel a queued or running job |
| `create_mission` | new Main/Dev pair (GET/HEAD only unless write methods are acknowledged) |
| `update_mission` | change settings; pause with `mirror_enabled=false` or `sample_rate=0` |
| `delete_mission` | delete a mission and its log; `confirm` must equal the slug |

Each tool has a JSON Schema and MCP annotations (`readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`), so clients can ask for confirmation before destructive calls.

## Resources and prompts

Resources (also listed per mission):

* `kittyparty://missions`: the mission list
* `kittyparty://missions/{mission}`: configuration and rules
* `kittyparty://missions/{mission}/rules`, `…/causes`, `…/stats`
* `kittyparty://missions/{mission}/requests/{request_id}`: one request diff

Prompts:

* `triage_mission(mission)`: find out why Dev differs from Main and which differences matter
* `tune_rules(mission)`: propose rules for expected noise, check each with `preview_rule`, and do not hide real regressions
* `release_check(mission, hours)`: decide with evidence whether Dev can replace Main

## Example session

```
> list_causes {"mission": "echo"}
Error: 'echo' matches several missions: echo-2, echo-apis. Use one of these slugs.

> list_causes {"mission": "Echo APIs"}
4 causes for Echo APIs (last 24 h), most frequent first:
1. 3 requests, e.g. GET /get (request_id 01a1b72b-163f-7671-aff5-6bc190b5b7d9)
   status code: 431 → 414
2. 1 requests, e.g. GET /delay/1 (request_id 01a1b729-4e34-7b25-a3d1-bd5ef5a8c594)
   missing on Dev $.args (Main: {})
   missing on Dev $.url (Main: "https://httpbin.org/delay/1")
   suggested rule: IgnoreField {"paths":["$.args"]}
…

> preview_rule {"mission": "echo-apis", "type": "ignorefield", "config": "{\"path\": \"$.headers.x-amzn-trace-id\"}"}
On the last 14 compared requests: 3 differ now, 3 would differ with this rule (0 newly matching, 0 newly differing).
Call add_rule with the same type and config to save it.
```

## Code layout

| file | |
|---|---|
| `Protocol/McpServer.cs` | JSON-RPC dispatch, version negotiation, sessions, cancellation, logging, completions |
| `Transports/StdioTransport.cs` | newline-delimited stdio; requests run concurrently, writes are serialised |
| `Transports/HttpTransport.cs` | Streamable HTTP and legacy SSE endpoints, origin and version checks |
| `Features/ToolRegistry.cs` | tool definitions, annotations, read-only enforcement, truncation, forgiving argument parsing |
| `Features/MirrorTools.cs` | the 23 tools |
| `Features/ResourcesAndPrompts.cs` | resources, templates and prompts |
| `Features/KittyApi.cs` | api client and mission lookup by name |
| `Program.cs` | command line and environment configuration |

Tests are in [`tests/Mirror.Mcp.Tests`](../tests/Mirror.Mcp.Tests). They run the real server against an in-process fake api and cover the protocol, every transport, argument handling and the main tools.
