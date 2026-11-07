# API Mirror & Diff Tool — Architecture

## 1. Services

```
                 ┌───────────────────────── critical path ─────────────────────────┐
  client ──HTTP──▶  proxy  ──(YARP IHttpForwarder, streaming)──▶  Main URL (prod)   │
                 │    │  ◀────────────── response streamed back unchanged ─────────┘
                 │    │ (after the response completes: Channel.TryWrite, never waits)
                 │    ▼
                 │  bounded Channel<MirrorJob>  ──full?──▶ drop + count
                 │    │
                 │  worker pool ──▶ dev HttpClient (own pool, timeout, Polly circuit breaker,
                 │    │                              per-mission concurrency limiter) ──▶ Dev URL
                 │    ▼
                 │  diff engine (rules)  ──▶ bounded Channel<LogRecord> ──full?──▶ drop + count
                 │                                   │
                 │                         batch writer (binary COPY, 1 s / 500 rows)
                 └───────────────────────────────────┼─────────────────────────────────────
                                                     ▼
                                               PostgreSQL (PVC)
                                                     ▲
   browser ──▶ web (nginx, Vite build) ──/api──▶  api  (CRUD, log queries, SSE, jobs)
                                                     ▲
   AI assistant / local LLM ──stdio or HTTP──▶  mcp (MCP server, kittyparty-mcp)
```

| Service | Role | State | Scales |
|---|---|---|---|
| **proxy** | Proxy endpoints `/m/{slug}/**`, mirroring, diffing, log writes | none (config snapshot cached in memory + optional file) | horizontally, behind any L4/L7 LB |
| **api** | Missions/rules CRUD, keyset log queries, live SSE, background jobs (re-diff, replay, fuzz, export, import), partition maintenance & retention, migrations | jobs in Postgres; export files on a volume | horizontally (jobs claimed with `FOR UPDATE SKIP LOCKED`) |
| **web** | Static Vite build served by nginx; proxies `/api` to api | none | horizontally |
| **mcp** | Model Context Protocol server for AI assistants and local LLMs (stdio, Streamable HTTP, legacy SSE); calls only the api. See [`MCP.md`](MCP.md) | Streamable HTTP sessions in memory | one per client (stdio) or one shared HTTP instance |
| **postgres** | Config, jobs, partitioned traffic log | PVC / named volume | single primary |

Shared code lives in `src/Mirror.Core` (diff engine, rules, JSON paths, models, compression) and `src/Mirror.Data` (EF Core context + migrations, raw Npgsql log writer/reader, partition SQL).

## 2. Request flow

1. A request hits `proxy` at `/m/{slug}/{path}?{query}`.
2. The proxy looks the mission up in an in-memory, immutable **config snapshot** (dictionary swap, no locks, no I/O).
3. A mirroring decision is taken *before* forwarding: mission enabled, method in the mission's mirror set (default GET/HEAD), `Random < sampleRate`, the global in-flight capture memory budget is not exhausted. Unsampled requests take the exact same path as a plain reverse proxy, with no tee and no allocation.
4. For sampled requests the request body stream is wrapped in a **tee** that copies bytes into a pooled buffer *as YARP reads them for Main*. Nothing is read ahead, so Main sees the same streaming behaviour. If `Content-Length` (or the running total for chunked bodies) exceeds the mission's `maxMirrorBodyBytes`, capture stops and the request is logged as **not mirrored**; forwarding is unaffected. The response body is teed the same way (up to `MaxCapturedResponseBytes`).
5. YARP `IHttpForwarder` streams the request to Main and the response back. This is the only work on the critical path.
6. In `Response.OnCompleted` (after the last byte has gone to the client) a `MirrorJob` is offered with `Channel.TryWrite`. If the channel is full the job is dropped, its buffers are returned to the pool and `mirror_dropped_total{reason="queue_full"}` is incremented. The proxy never awaits the channel.
7. A fixed worker pool reads the channel. For each job it builds the Dev request (header strip/override rules applied, `X-Mirror-Request: 1` added), sends it through the mission's resilience pipeline (Polly timeout → circuit breaker → concurrency limiter, using a **separate** `SocketsHttpHandler` with its own connection limits), reads the Dev body up to the capture limit, then runs the diff engine.
8. The resulting `LogRecord` is offered (again `TryWrite`) to the log channel. The batch writer flushes every second or 500 rows using **binary COPY** into `traffic_log` and `traffic_body`, upserts per-minute stats rows and sends `NOTIFY mirror_log` so the api can push live updates. If Postgres is down the batch is dropped and counted (`log_dropped_total`), with exponential back-off on reconnect attempts.

## 3. How production safety is guaranteed

| Failure | Effect on Main traffic | Why |
|---|---|---|
| Dev slow / down | none | Dev calls happen only in workers after the response completed; separate handler and pool; per-mission timeout, circuit breaker and concurrency limit; workers bounded. |
| Workers saturated | none | `TryWrite` on a bounded channel; full ⇒ drop and count. No backpressure. |
| Large bodies | none | Tee only copies up to the limit; never reads ahead. Over-limit ⇒ "not mirrored". |
| Memory pressure | none | Global capture budget (`Mirror__MaxInFlightCaptureBytes`): when exceeded, new requests are simply not sampled. |
| Diff engine slow or throws | none | Runs in workers; exceptions are caught per job; diffs bigger than `MaxDiffBodyBytes` are skipped and flagged. |
| Postgres down | none | Config served from the in-memory snapshot (and from `Mirror__SnapshotFile` on cold start). Log writes are dropped and counted. |
| api / web down | none | The proxy never calls the api; it reads config straight from Postgres on a timer. |
| Main down | 502 as YARP returns it | Not mirrored (nothing to compare). |

The main-path cost for a sampled request is: one random number, two stream wrappers that `memcpy` into pooled segments, one `OnCompleted` callback, one `TryWrite`. The load test (`loadtest/`) measures it.

## 4. Diff engine and rules

`Mirror.Core.Diffing.JsonDiffEngine` compares two documents after a rule pipeline:

1. **Raw text stage** — `SearchAndReplace` with scope "whole response" (plain or regex) runs on the decoded text of both bodies.
2. **Parse** with `System.Text.Json` (`JsonNode`).
3. **Document stage** — rules that rewrite the tree (`IgnoreField` removes matches, field-scoped `SearchAndReplace`).
4. **Compare stage** — rules contribute options to the comparer: `PrimaryKey` (array path → key path(s), nested arrays supported), `IgnoreOrder` (multiset compare), `NumericTolerance` (absolute or relative), `TypeCoercion` (`"1" == 1`, `"true" == true`), `IgnoreHeader`.

Rules implement one interface (`IDiffRule`, with default no-op hooks for each stage) and register in `RuleRegistry` with a typed config record. Adding a rule type = one class + one `Register` line.

Paths use a JSONPath subset: `$`, `.name`, `['name']`, `[n]`, `[*]`, `.*`, `..name`. Patterns are compiled to a small NFA and advanced incrementally while walking the tree, so matching cost does not grow with depth. Diff output uses concrete paths; array elements matched by primary key are rendered `[sku=LMP-0042]`.

Each comparison yields: status equality, both latencies, array length changes, field differences (`changed`, `added`, `removed`, `type_changed`, `array_length`, `order`), selected header differences, a short summary (stored in `traffic_log.summary`), the full difference list (compressed in `traffic_body.diff`), a bitmask of difference kinds (filterable) and a **signature** (hash of normalised kind+path pairs) used to group mismatches by cause.

Verdicts: `match`, `mismatch`, `dev_error` (transport error or Dev 5xx where Main was not 5xx), `dev_timeout`, `not_mirrored`.

## 5. Data model (PostgreSQL)

EF Core owns the config tables; the log tables are created by raw SQL inside the same migration.

* `missions`, `mission_rules` (ordered, typed JSON config), `jobs`, `proxy_instances` (heartbeat + metric snapshot per proxy pod).
* `traffic_log` — **`PARTITION BY LIST (mission_id)`, each mission partition `PARTITION BY RANGE (ts)` by day.** Narrow index row: ids, timestamps, method, path, statuses, latencies, verdict, diff kind mask, diff count, signature, rules version, source (live/replay/fuzz/import), job id, summary jsonb.
* `traffic_body` — same two-level partitioning; request/response headers and bodies and the full diff, each **Brotli-compressed** `bytea` with `STORAGE EXTERNAL` (no double compression by TOAST).
* `traffic_stats_minute` — per mission/minute/verdict counters, maintained by the writer, for cheap dashboards.

Indexes on each day partition (declared on the parent): `(ts DESC, id DESC)`, `(verdict, ts DESC, id DESC)`, `(path text_pattern_ops, ts DESC)` for prefix filters, trigram GIN on `path` for contains search, `(signature, ts DESC)` for clusters, partial `(job_id)`.

Partition maintenance (api, every 10 min, plus `mirror_ensure_partitions()` called by the proxy writer when an insert hits a missing partition): creates partitions ahead, **drops day partitions older than the mission's `retentionDays`**, and drops the oldest day partitions while the mission's total size exceeds `maxStorageMb`. Deleting a mission drops its list partition outright.

IDs are UUIDv7 generated by the writer, so a log row and its body row are written in the same COPY batch without a round-trip.

## 6. API

All list endpoints use keyset pagination (`cursor` = opaque base64 of the last sort key + id); limits are capped; no endpoint materialises a full log. Export streams `application/gzip` NDJSON directly from a server-side cursor.

* `GET/POST/PUT/DELETE /api/missions`, `GET/PUT /api/missions/{id}/rules` (replace ordered list, bumps `rulesVersion`, optional `rediff=true` starts a job), `POST /api/rules/validate`, `GET /api/rule-types`.
* `GET /api/missions/{id}/logs?verdict=&diffKind=&path=&pathMode=prefix|contains&method=&status=&source=&jobId=&signature=&from=&to=&sort=&cursor=&limit=`
* `GET /api/logs/{id}?missionId=` (summary), `GET /api/logs/{id}/detail` (bodies + full diff, decompressed on demand), `POST /api/logs/{id}/replay`.
* `GET /api/missions/{id}/stats`, `GET /api/missions/{id}/clusters`, `GET /api/missions/{id}/live` (SSE, batched once per second).
* `POST /api/missions/{id}/jobs/{rediff|replay|fuzz|export}`, `POST /api/import`, `GET /api/jobs`, `GET /api/jobs/{id}`, `POST /api/jobs/{id}/cancel`, `GET /api/jobs/{id}/download`, `GET /api/missions/{id}/export` (direct stream).
* `GET /api/proxy/instances` (aggregated proxy metrics for the UI).

## 6b. Frontend

* **Server state**: TanStack Query. Logs use an infinite query over the keyset cursor, and every filter and sort is a query parameter evaluated by Postgres. Nothing ever loads a whole log.
* **Large lists**: TanStack Virtual renders only the visible rows of the log, the change list and the side-by-side view.
* **Live updates**: one `EventSource` per open mission. The api's LISTEN/NOTIFY-driven stream emits at most one batch per second, the client coalesces again to one render per second, applies the current filters locally, and holds rows behind a "↑ N new" pill while the user is scrolled.
* **Diffs**: the detail endpoint returns headers and the server's difference list. Bodies are fetched lazily, inside a Web Worker, which pretty-prints both documents in lockstep (object keys unioned, arrays matched by primary key when a rule exists) and returns aligned rows tagged with JSON paths and kinds. The main thread folds unchanged runs and virtualizes, and cross-checks rows against the server's list to dim differences that rules exclude.

## 7. Background jobs

Rows in `jobs` (`queued → running → succeeded|failed|cancelled`). Each api instance runs a `JobRunner` that claims work with `FOR UPDATE SKIP LOCKED`, heartbeats every few seconds and stores a **checkpoint** (keyset cursor / line number). A job whose heartbeat is stale (instance died) is re-claimed and resumes from its checkpoint. Cancellation is a flag checked between batches. Rate limits use `System.Threading.RateLimiting` token buckets.

* **re-diff** — walks the mission's log by keyset, re-runs the diff on stored bodies with current rules, updates rows in place.
* **replay** — re-sends logged requests (filtered) to Main and Dev, stores new rows (`source=replay`, `replay_of`), summarises newly matching / newly mismatching.
* **fuzz** — generates variants of a sample or logged request (null, empty, huge, boundary numbers, wrong types, missing/extra fields, query param mutations), sends to both, reports mismatches per mutation.
* **export** — streams gzip NDJSON to the export volume; **import** — streams an uploaded file line by line into a new mission using COPY.

## 8. Metrics and health

Proxy (`/metrics`, Prometheus): `mirror_queue_depth`, `mirror_dropped_total{reason}`, `mirror_not_mirrored_total{reason}`, `dev_requests_total{mission,outcome}`, `dev_request_duration_seconds`, `main_request_duration_seconds`, `log_records_written_total`, `log_records_dropped_total{reason}`, `circuit_state{mission}`. The same snapshot is written to `proxy_instances` every 10 s for the UI.

Every service exposes `/healthz` (liveness) and `/readyz` (readiness). The proxy is ready once it has a config snapshot; it stays ready if Postgres later goes away.

## 9. Deployment

Multi-stage Dockerfiles per service; `docker-compose.yml` (postgres with named volume, migrations run by the api on start, demo upstreams and k6 under profiles); `deploy/k8s/` with Deployments, Services, a Postgres StatefulSet with PVC, ConfigMap/Secret, a migration Job, and HPA for the proxy. All configuration is environment variables (`ConnectionStrings__Postgres`, `Mirror__*`).
