using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Mirror.Mcp.Protocol;

namespace Mirror.Mcp.Features;

/// <summary>
/// The KittyParty tools. Outputs are compact plain text (local models have small context windows and follow
/// text better than raw JSON); the same data is attached as structuredContent for clients that use it.
/// </summary>
public static partial class MirrorTools
{
    private static readonly string[] Verdicts = ["match", "mismatch", "dev_error", "dev_timeout", "not_mirrored"];
    private static readonly string[] DiffKinds = ["status", "changed", "added", "removed", "type_changed", "array_length", "order", "header", "body"];
    private static readonly string[] Unsafe = ["POST", "PUT", "PATCH", "DELETE"];

    private static JsonObject MissionArg => Schema.String("Mission name, slug or id (see list_missions), e.g. \"orders\"");

    public static IEnumerable<ToolDef> Create(KittyApi api)
    {
        // ───────────────────────── read-only ─────────────────────────
        yield return new ToolDef("list_missions", "List missions",
            "List every mission (a Main/Dev API pair) with its last-24h volume and how often Main and Dev differ. Start here.",
            Schema.Object(), true, false, true, false,
            async (_, _, ct) =>
            {
                var missions = await api.MissionsAsync(ct, fresh: true);
                var overview = (JsonArray)await api.GetAsync("/api/missions/overview?hours=24", ct);
                var cfg = await api.GetAsync("/api/config", ct);
                var byId = overview.OfType<JsonObject>().ToDictionary(o => (string)o["missionId"]!);
                if (missions.Count == 0) return new ToolResult("No missions yet. Use create_mission to compare a Main URL with a Dev URL.", new JsonObject { ["missions"] = new JsonArray() });
                var sb = new StringBuilder($"{missions.Count} missions. Proxy endpoint: {(string?)cfg["proxyPublicUrl"]}/m/<slug>/\n");
                var structured = new JsonArray();
                foreach (var m in missions.OfType<JsonObject>())
                {
                    byId.TryGetValue((string)m["id"]!, out var o);
                    var paused = !(bool)m["enabled"]! || !(bool)m["mirrorEnabled"]! || (double)m["sampleRate"]! == 0;
                    var errors = (long?)o?["devError"] + (long?)o?["devTimeout"] ?? 0;
                    var methods = Join(m["mirrorMethods"]);
                    sb.Append($"- {m["slug"]} · \"{m["name"]}\" · {m["mainUrl"]} → {m["devUrl"]} · ");
                    sb.Append(paused ? "PAUSED" : $"mirrors {methods} at {Pct((double)m["sampleRate"]!)}");
                    sb.Append($" · 24h: {(long?)o?["total"] ?? 0} requests, {Pct((double?)o?["diffRate"])} differ, {errors} Dev errors · {m["ruleCount"]} rules");
                    if (methods.Split(',').Any(x => Unsafe.Contains(x))) sb.Append(" · WARNING mirrors write methods");
                    sb.Append('\n');
                    structured.Add(new JsonObject
                    {
                        ["slug"] = (string?)m["slug"], ["name"] = (string?)m["name"], ["id"] = (string?)m["id"],
                        ["mainUrl"] = (string?)m["mainUrl"], ["devUrl"] = (string?)m["devUrl"], ["paused"] = paused,
                        ["mirrorMethods"] = m["mirrorMethods"]!.DeepClone(), ["sampleRate"] = (double)m["sampleRate"]!, ["ruleCount"] = (int)m["ruleCount"]!,
                        ["requests24h"] = (long?)o?["total"] ?? 0, ["diffRate24h"] = (double?)o?["diffRate"], ["devErrors24h"] = errors,
                    });
                }
                return new ToolResult(sb.ToString().TrimEnd(), new JsonObject { ["missions"] = structured });
            });

        yield return new ToolDef("get_mission", "Mission details",
            "Show one mission's configuration: URLs, proxy endpoint, mirrored methods, sampling, header handling, retention and its rules.",
            Schema.Object(("mission", MissionArg, true)), true, false, true, false,
            async (a, _, ct) =>
            {
                var m = await api.ResolveMissionAsync(a.Req("mission"), ct);
                var full = await api.GetAsync($"/api/missions/{m["id"]}", ct);
                var rules = (JsonArray)await api.GetAsync($"/api/missions/{m["id"]}/rules", ct);
                var cfg = await api.GetAsync("/api/config", ct);
                var h = full["headerPolicy"]!;
                var sb = new StringBuilder();
                sb.AppendLine($"Mission \"{full["name"]}\" (slug {full["slug"]}, id {full["id"]})");
                sb.AppendLine($"Proxy endpoint: {(string?)cfg["proxyPublicUrl"]}{full["proxyPath"]}/  (clients call this instead of Main)");
                sb.AppendLine($"Main: {full["mainUrl"]}\nDev:  {full["devUrl"]}");
                sb.AppendLine($"Enabled: {full["enabled"]}, mirroring: {full["mirrorEnabled"]}, sample rate {Pct((double)full["sampleRate"]!)}, methods {Join(full["mirrorMethods"])}");
                sb.AppendLine($"Dev timeout {full["devTimeoutMs"]} ms, max {full["devMaxConcurrency"]} concurrent Dev calls, request bodies up to {(int)full["maxMirrorBodyBytes"]! / 1024} KB mirrored");
                sb.AppendLine($"Headers stripped before Dev: {Join(h["strip"])}; set on Dev: {string.Join(", ", (h["set"] as JsonObject ?? []).Select(kv => kv.Key))}; compared: {Join(full["comparedHeaders"])}");
                sb.AppendLine($"Keeps logs {full["retentionDays"]} days{(full["maxStorageMb"] is null ? "" : $", at most {full["maxStorageMb"]} MB")}. Rules version {full["rulesVersion"]}.");
                foreach (var w in full["warnings"] as JsonArray ?? []) sb.AppendLine("WARNING: " + (string?)w);
                sb.Append(FormatRules(rules));
                return new ToolResult(sb.ToString().TrimEnd(), new JsonObject { ["mission"] = full.DeepClone(), ["rules"] = rules.DeepClone() });
            });

        yield return new ToolDef("mission_stats", "Mission statistics",
            "Counts per verdict (match, mismatch, dev_error, dev_timeout, not_mirrored), match rate and average latency of Main and Dev over the last N hours.",
            Schema.Object(("mission", MissionArg, true), ("hours", Schema.Integer("Time window in hours", 24, 1, 2160), false)),
            true, false, true, false,
            async (a, _, ct) =>
            {
                var m = await api.ResolveMissionAsync(a.Req("mission"), ct);
                var hours = a.Int("hours", 24, 1, 2160);
                var s = await api.GetAsync($"/api/missions/{m["id"]}/stats?from={Since(hours)}", ct);
                var v = s["verdicts"] as JsonObject ?? [];
                long Count(string k) => (long?)v[k]?["count"] ?? 0;
                double Avg(string field)
                {
                    double n = 0, sum = 0;
                    foreach (var (k, x) in v)
                    {
                        if (field == "devMsAvg" && k == "not_mirrored") continue;
                        n += (long)x!["count"]!;
                        sum += (double)x[field]! * (long)x["count"]!;
                    }
                    return n == 0 ? 0 : sum / n;
                }
                var text = $"""
                    {m["name"]}, last {hours} h: {s["total"]} mirrored requests
                    match {Count("match")} · mismatch {Count("mismatch")} · dev_error {Count("dev_error")} · dev_timeout {Count("dev_timeout")} · not_mirrored {Count("not_mirrored")}
                    match rate (of compared requests): {Pct((double?)s["matchRate"])}
                    average latency: Main {Ms(Avg("mainMsAvg"))}, Dev {Ms(Avg("devMsAvg"))}
                    """;
                return new ToolResult(text, s.DeepClone());
            });

        yield return new ToolDef("list_causes", "Why Main and Dev differ",
            "Group differing requests by cause (same set of differing JSON paths) and suggest a rule for each. Best first step to understand a mission.",
            Schema.Object(("mission", MissionArg, true), ("hours", Schema.Integer("Time window in hours", 24, 1, 2160), false),
                ("limit", Schema.Integer("Maximum causes", 10, 1, 50), false)),
            true, false, true, false,
            async (a, _, ct) =>
            {
                var m = await api.ResolveMissionAsync(a.Req("mission"), ct);
                var hours = a.Int("hours", 24, 1, 2160);
                var clusters = (JsonArray)await api.GetAsync($"/api/missions/{m["id"]}/clusters?from={Since(hours)}&limit={a.Int("limit", 10, 1, 50)}", ct);
                if (clusters.Count == 0) return new ToolResult($"No differing requests for {m["name"]} in the last {hours} h. Main and Dev agree (or nothing was mirrored: check mission_stats).", new JsonObject { ["causes"] = new JsonArray() });
                var sb = new StringBuilder($"{clusters.Count} causes for {m["name"]} (last {hours} h), most frequent first:\n");
                var structured = new JsonArray();
                var n = 0;
                foreach (var c in clusters.OfType<JsonObject>())
                {
                    var top = c["summary"]?["top"] as JsonArray;
                    var first = top?.FirstOrDefault() as JsonObject;
                    sb.Append($"{++n}. {c["count"]} requests, e.g. {c["example"]?["method"]} {c["example"]?["path"]} (request_id {c["example"]?["id"]})\n");
                    foreach (var d in (top ?? []).OfType<JsonObject>().Take(3))
                        sb.Append($"   {Diff(d)}\n");
                    var sug = first is null ? null : Suggest((string)first["k"]!, (string)first["p"]!, (string?)first["m"], (string?)first["d"]);
                    if (sug is not null) sb.Append($"   suggested rule: {sug["type"]} {sug["config"]!.ToJsonString()}{((string?)sug["note"] is { } note ? $" ({note})" : "")}\n");
                    structured.Add(new JsonObject { ["count"] = (long)c["count"]!, ["signature"] = (string?)c["signature"], ["exampleRequestId"] = (string?)c["example"]?["id"], ["differences"] = top?.DeepClone(), ["suggestedRule"] = sug?.DeepClone() });
                }
                sb.Append("Use preview_rule to check a rule's effect before add_rule. Only hide differences that are expected (ids, timestamps, hosts, new optional fields).");
                return new ToolResult(sb.ToString(), new JsonObject { ["causes"] = structured });
            });

        yield return new ToolDef("search_traffic", "Search the traffic log",
            "Find mirrored requests, newest first. Filters are optional. Returns request ids for get_request_diff. Use cursor from the previous result for the next page.",
            Schema.Object(
                ("mission", MissionArg, true),
                ("verdict", Schema.String("Only this outcome", Verdicts), false),
                ("path", Schema.String("Only paths starting with this, e.g. /v1/orders"), false),
                ("method", Schema.String("HTTP method, e.g. GET"), false),
                ("status", Schema.Integer("Only this Dev status code, e.g. 500"), false),
                ("diff_kind", Schema.String("Only requests with this kind of difference", DiffKinds), false),
                ("source", Schema.String("live traffic, replays, fuzz runs or imports", ["live", "replay", "fuzz", "import"]), false),
                ("hours", Schema.Integer("Only the last N hours (default: all)"), false),
                ("sort", Schema.String("Order", ["newest", "oldest", "slowest", "most_different"], "newest"), false),
                ("limit", Schema.Integer("Rows to return", 15, 1, 100), false),
                ("cursor", Schema.String("Paging cursor from a previous call"), false)),
            true, false, true, false,
            async (a, _, ct) =>
            {
                var m = await api.ResolveMissionAsync(a.Req("mission"), ct);
                var q = new List<string>();
                void Add(string k, string? v) { if (!string.IsNullOrEmpty(v)) q.Add($"{k}={Uri.EscapeDataString(v)}"); }
                Add("verdict", a.Enum("verdict", Verdicts));
                Add("path", a.Str("path"));
                Add("method", a.Str("method")?.ToUpperInvariant());
                if (a.Has("status")) Add("devStatus", a.Int("status", 0, 100, 599).ToString(CultureInfo.InvariantCulture));
                Add("diffKind", a.Enum("diff_kind", DiffKinds));
                Add("source", a.Enum("source", ["live", "replay", "fuzz", "import"]));
                if (a.Has("hours")) Add("from", Since(a.Int("hours", 24, 1, 24 * 365)));
                Add("sort", a.Enum("sort", ["newest", "oldest", "slowest", "most_different"]));
                Add("cursor", a.Str("cursor"));
                q.Add($"limit={a.Int("limit", 15, 1, 100)}");
                var page = await api.GetAsync($"/api/missions/{m["id"]}/logs?{string.Join("&", q)}", ct);
                var items = page["items"] as JsonArray ?? [];
                if (items.Count == 0) return new ToolResult("No requests match these filters.", page.DeepClone());
                var sb = new StringBuilder($"{items.Count} requests for {m["name"]}:\n");
                foreach (var i in items.OfType<JsonObject>()) sb.Append(Row(i)).Append('\n');
                if ((string?)page["nextCursor"] is { } next) sb.Append($"More results: call again with cursor=\"{next}\"");
                return new ToolResult(sb.ToString().TrimEnd(), page.DeepClone());
            });

        yield return new ToolDef("get_request_diff", "Differences of one request",
            "Show how Main and Dev answered one request: status, latency, sizes and each difference with its JSON path and both values.",
            Schema.Object(("mission", MissionArg, true), ("request_id", Schema.String("Request id from search_traffic or list_causes"), true),
                ("max_differences", Schema.Integer("How many differences to list", 25, 1, 200), false)),
            true, false, true, false,
            async (a, _, ct) =>
            {
                var m = await api.ResolveMissionAsync(a.Req("mission"), ct);
                var id = RequestId(a);
                var d = await api.GetAsync($"/api/missions/{m["id"]}/logs/{id}/detail", ct);
                var log = d["log"]!;
                var diffs = d["differences"] as JsonArray ?? [];
                var max = a.Int("max_differences", 25, 1, 200);
                var sb = new StringBuilder();
                sb.AppendLine($"{log["method"]} {log["path"]}{((string?)log["query"] is { } qs ? "?" + qs : "")} at {log["ts"]} ({log["source"]})");
                sb.AppendLine($"verdict: {log["verdict"]} · status Main {log["mainStatus"]} / Dev {log["devStatus"]?.ToString() ?? "none"} · latency {Ms((double?)log["mainMs"])} / {Ms((double?)log["devMs"])} · body {Bytes((int?)d["main"]?["bodyBytes"])} / {Bytes((int?)d["dev"]?["bodyBytes"])} · rules v{log["rulesVersion"]}");
                if ((string?)log["note"] is { } note) sb.AppendLine("note: " + note);
                if (diffs.Count == 0) sb.AppendLine("No differences.");
                else
                {
                    sb.AppendLine($"{log["diffCount"]} differences{(diffs.Count > max ? $", first {max}" : "")}:");
                    foreach (var x in diffs.OfType<JsonObject>().Take(max)) sb.AppendLine("  " + Diff(x));
                }
                if ((string?)log["replayOf"] is { } orig) sb.AppendLine($"This is a replay of request {orig}.");
                sb.Append("Bodies: get_response_body with side=main|dev|request.");
                return new ToolResult(sb.ToString(), new JsonObject { ["log"] = log.DeepClone(), ["differences"] = new JsonArray(diffs.Take(max).Select(x => x!.DeepClone()).ToArray()) });
            });

        yield return new ToolDef("get_response_body", "Response or request body",
            "Read the body Main or Dev returned for a request (or the request body), pretty-printed and truncated. normalized=true applies the mission's text and field rules first.",
            Schema.Object(("mission", MissionArg, true), ("request_id", Schema.String("Request id"), true),
                ("side", Schema.String("Which body", ["main", "dev", "request"], "main"), false),
                ("normalized", Schema.Boolean("Apply the mission's replace rules first", false), false),
                ("max_chars", Schema.Integer("Truncate after this many characters", 3000, 200, 20000), false)),
            true, false, true, false,
            async (a, _, ct) =>
            {
                var m = await api.ResolveMissionAsync(a.Req("mission"), ct);
                var id = RequestId(a);
                var side = a.Enum("side", ["main", "dev", "request"], "main")!;
                var normalized = a.Bool("normalized") && side != "request";
                var max = a.Int("max_chars", 3000, 200, 20000);
                var text = await api.GetTextAsync($"/api/missions/{m["id"]}/logs/{id}/body/{side}{(normalized ? "?normalized=true" : "")}", ct);
                if (text.Length == 0) return new ToolResult($"The {side} body is empty.");
                try { if (!normalized) text = JsonNode.Parse(text)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }); }
                catch (JsonException) { }
                var total = text.Length;
                if (total > max) text = text[..max] + $"\n… ({total - max} more characters; raise max_chars or use get_request_diff)";
                return new ToolResult($"{side} body ({total} characters):\n{text}");
            });

        yield return new ToolDef("get_rules", "List rules",
            "Show the mission's comparison rules in the order they run, with their numbers (used by remove_rule).",
            Schema.Object(("mission", MissionArg, true)), true, false, true, false,
            async (a, _, ct) =>
            {
                var m = await api.ResolveMissionAsync(a.Req("mission"), ct);
                var rules = (JsonArray)await api.GetAsync($"/api/missions/{m["id"]}/rules", ct);
                return new ToolResult($"Mission \"{m["name"]}\":\n" + FormatRules(rules).TrimEnd(), new JsonObject { ["rules"] = rules.DeepClone() });
            });

        yield return new ToolDef("list_rule_types", "Rule types",
            "Explain the available rule types and show an example config for each (needed for preview_rule and add_rule).",
            Schema.Object(), true, false, true, false,
            async (_, _, ct) =>
            {
                var types = (JsonArray)await api.GetAsync("/api/rule-types", ct);
                var sb = new StringBuilder("Rule types (paths use $.field, $.list[*].field, $..anywhere):\n");
                foreach (var t in types.OfType<JsonObject>()) sb.Append($"- {t["type"]}: {t["description"]} Example config: {t["example"]!.ToJsonString()}\n");
                return new ToolResult(sb.ToString().TrimEnd(), new JsonObject { ["types"] = types.DeepClone() });
            });

        yield return new ToolDef("preview_rule", "Preview a rule",
            "Dry run: how many recent requests would still differ if this rule were added. Nothing is saved.",
            RuleSchema(), true, false, true, false,
            async (a, _, ct) =>
            {
                var m = await api.ResolveMissionAsync(a.Req("mission"), ct);
                var (type, config) = await ValidRuleAsync(api, a, ct);
                var rules = await CurrentRulesAsync(api, m, ct);
                rules.Add(new JsonObject { ["type"] = type, ["enabled"] = true, ["config"] = config });
                var p = await api.PostAsync($"/api/missions/{m["id"]}/rules/preview", new JsonObject { ["rules"] = rules, ["sampleSize"] = 200 }, ct);
                var text = (int)p["sampled"]! == 0
                    ? "No compared traffic yet, so the effect cannot be measured. The rule config is valid."
                    : $"On the last {p["sampled"]} compared requests: {p["mismatchesBefore"]} differ now, {p["mismatchesAfter"]} would differ with this rule ({p["nowMatching"]} newly matching, {p["nowMismatching"]} newly differing).";
                foreach (var e in p["ruleErrors"] as JsonArray ?? []) text += $"\nRule error: {e?["rule"]}: {e?["error"]}";
                return new ToolResult(text + "\nCall add_rule with the same type and config to save it.", p.DeepClone());
            });

        yield return new ToolDef("list_jobs", "List background jobs",
            "Show recent background jobs (re-diff, replay, fuzz, export, import) with status and progress.",
            Schema.Object(("mission", Schema.String("Optional mission name or slug"), false), ("limit", Schema.Integer("Maximum jobs", 10, 1, 50), false)),
            true, false, true, false,
            async (a, _, ct) =>
            {
                var path = $"/api/jobs?limit={a.Int("limit", 10, 1, 50)}";
                if (a.Str("mission") is { } mm) path += $"&missionId={(await api.ResolveMissionAsync(mm, ct))["id"]}";
                var jobs = (await api.GetAsync(path, ct))["items"] as JsonArray ?? [];
                if (jobs.Count == 0) return new ToolResult("No jobs.");
                return new ToolResult(string.Join("\n", jobs.OfType<JsonObject>().Select(JobLine)), new JsonObject { ["jobs"] = jobs.DeepClone() });
            });

        yield return new ToolDef("get_job", "Job status",
            "Status, progress and result summary of one background job.",
            Schema.Object(("job_id", Schema.String("Job id"), true)), true, false, true, false,
            async (a, _, ct) =>
            {
                var j = (JsonObject)await api.GetAsync($"/api/jobs/{Guid(a.Req("job_id"), "job_id")}", ct);
                var text = JobLine(j);
                if (j["result"] is JsonObject r) text += "\nresult: " + r.ToJsonString();
                return new ToolResult(text, j.DeepClone());
            });

        yield return new ToolDef("proxy_health", "Proxy health",
            "State of every running proxy instance: mirror queue, dropped jobs, log writer, main-path latency and Dev circuit breakers.",
            Schema.Object(), true, false, true, false,
            async (_, _, ct) =>
            {
                var list = (JsonArray)await api.GetAsync("/api/proxy/instances", ct);
                if (list.Count == 0) return new ToolResult("No proxy instance has reported in the last 2 minutes. Is the proxy running and connected to Postgres?");
                var sb = new StringBuilder();
                foreach (var p in list.OfType<JsonObject>())
                {
                    var x = p["metrics"]!;
                    sb.AppendLine($"{p["instanceId"]}: queue {x["queueDepth"]}/{x["queueCapacity"]}, enqueued {x["enqueued"]}, dropped {Sum(x["dropped"])} {Kv(x["dropped"])}, logs written {x["logWritten"]}, logs dropped {Sum(x["logDropped"])}");
                    sb.AppendLine($"  main path avg {Ms((double?)x["mainLatencyMs"]?["mirrored"]?["avgMs"])} (mirrored) vs {Ms((double?)x["mainLatencyMs"]?["unmirrored"]?["avgMs"])} (not mirrored); circuits {Kv(x["circuits"])}");
                }
                return new ToolResult(sb.ToString().TrimEnd(), new JsonObject { ["instances"] = list.DeepClone() });
            });

        // ───────────────────────── changes data ─────────────────────────
        yield return new ToolDef("add_rule", "Add a rule",
            "Add a comparison rule to the end of the mission's rules and (by default) re-check stored requests in the background. Use preview_rule first.",
            RuleSchema(withRediff: true), false, false, false, false,
            async (a, _, ct) =>
            {
                var m = await api.ResolveMissionAsync(a.Req("mission"), ct);
                var (type, config) = await ValidRuleAsync(api, a, ct);
                var rules = await CurrentRulesAsync(api, m, ct);
                rules.Add(new JsonObject { ["type"] = type, ["enabled"] = true, ["config"] = config });
                var r = await api.PutAsync($"/api/missions/{m["id"]}/rules", new JsonObject { ["rules"] = rules, ["rediff"] = a.Bool("rediff", true) }, ct);
                api.InvalidateMissions();
                var job = r["job"] as JsonObject;
                return new ToolResult($"Added {type} {config.ToJsonString()} as rule {rules.Count}. Rules are now version {r["rulesVersion"]}." +
                    (job is null ? "" : $" Re-diff job {job["id"]} started (get_job to follow it)."), r.DeepClone());
            });

        yield return new ToolDef("remove_rule", "Remove a rule",
            "Remove one rule by its number from get_rules (1 = first) or by its id, and (by default) re-check stored requests.",
            Schema.Object(("mission", MissionArg, true), ("rule", Schema.String("Rule number from get_rules (1, 2, …) or rule id"), true),
                ("rediff", Schema.Boolean("Re-check stored requests with the new rules", true), false)),
            false, true, false, false,
            async (a, _, ct) =>
            {
                var m = await api.ResolveMissionAsync(a.Req("mission"), ct);
                var rules = await CurrentRulesAsync(api, m, ct);
                var which = a.Req("rule");
                var index = int.TryParse(which.TrimStart('#'), out var n) ? n - 1 : rules.ToList().FindIndex(r => string.Equals((string?)r!["id"], which, StringComparison.OrdinalIgnoreCase));
                if (index < 0 || index >= rules.Count) throw new ToolException($"no rule '{which}'; the mission has {rules.Count} rules (numbers 1–{rules.Count})");
                var removed = rules[index]!.DeepClone();
                rules.RemoveAt(index);
                var r = await api.PutAsync($"/api/missions/{m["id"]}/rules", new JsonObject { ["rules"] = rules, ["rediff"] = a.Bool("rediff", true) }, ct);
                api.InvalidateMissions();
                return new ToolResult($"Removed rule {index + 1} ({removed["type"]} {removed["config"]!.ToJsonString()}). Rules are now version {r["rulesVersion"]}." +
                    (r["job"] is JsonObject job ? $" Re-diff job {job["id"]} started." : ""), r.DeepClone());
            });

        yield return new ToolDef("replay_request", "Replay one request",
            "Send a logged request again to both Main and Dev right now and compare. Write methods need allow_writes=true because Main is production.",
            Schema.Object(("mission", MissionArg, true), ("request_id", Schema.String("Request id"), true),
                ("allow_writes", Schema.Boolean("Allow replaying POST/PUT/PATCH/DELETE (sent to production too)", false), false)),
            false, false, false, true,
            async (a, _, ct) =>
            {
                var m = await api.ResolveMissionAsync(a.Req("mission"), ct);
                var r = await api.PostAsync($"/api/missions/{m["id"]}/logs/{RequestId(a)}/replay{(a.Bool("allow_writes") ? "?allowUnsafe=true" : "")}", null, ct);
                var o = r["original"]!; var n = r["replay"]!;
                return new ToolResult($"Replayed {o["method"]} {o["path"]}. Original: {o["verdict"]} ({o["diffCount"]} differences). Now: {n["verdict"]} ({n["diffCount"]} differences), new request_id {n["id"]}.", r.DeepClone());
            });

        yield return new ToolDef("start_replay", "Replay many requests",
            "Background job: re-send logged requests (filtered) to Main and Dev at a limited rate and report which now match. Write methods are skipped unless allow_writes=true.",
            Schema.Object(("mission", MissionArg, true),
                ("verdict", Schema.String("Which requests", Verdicts, "mismatch"), false),
                ("path", Schema.String("Only paths starting with this"), false),
                ("limit", Schema.Integer("At most this many requests", 200, 1, 100000), false),
                ("rate", Schema.Number("Requests per second (default 10)"), false),
                ("allow_writes", Schema.Boolean("Include POST/PUT/PATCH/DELETE (re-sent to production too)", false), false)),
            false, false, false, true,
            async (a, _, ct) =>
            {
                var m = await api.ResolveMissionAsync(a.Req("mission"), ct);
                var body = new JsonObject
                {
                    ["filter"] = new JsonObject { ["verdict"] = a.Enum("verdict", Verdicts, "mismatch"), ["path"] = a.Str("path"), ["source"] = "live" },
                    ["ratePerSecond"] = a.Dbl("rate") ?? 10,
                    ["limit"] = a.Int("limit", 200, 1, 100000),
                    ["allowUnsafeMethods"] = a.Bool("allow_writes"),
                };
                var j = await api.PostAsync($"/api/missions/{m["id"]}/jobs/replay", body, ct);
                return new ToolResult($"Replay job {j["id"]} queued. Check it with get_job.", j.DeepClone());
            });

        yield return new ToolDef("start_fuzz", "Fuzz an endpoint",
            "Background job: generate variants of a request (nulls, wrong types, boundary numbers, huge/empty strings, missing/extra fields) and send each to Main and Dev. Give request_id of a logged request, or method+path (+query, body).",
            Schema.Object(("mission", MissionArg, true),
                ("request_id", Schema.String("Seed: a logged request id"), false),
                ("method", Schema.String("Seed method when no request_id", null, "GET"), false),
                ("path", Schema.String("Seed path when no request_id, e.g. /v1/search"), false),
                ("query", Schema.String("Seed query string, e.g. q=lamp&page=2"), false),
                ("body", Schema.String("Seed JSON body"), false),
                ("volume", Schema.Integer("Number of variants", 100, 1, 100000), false),
                ("rate", Schema.Number("Requests per second (default 10)"), false),
                ("mutations", Schema.StringArray("Optional subset: null, empty, huge_string, boundary_number, wrong_type, missing, extra_field, empty_array, special_chars"), false),
                ("allow_writes", Schema.Boolean("Required for POST/PUT/PATCH/DELETE seeds (sent to production too)", false), false)),
            false, false, false, true,
            async (a, _, ct) =>
            {
                var m = await api.ResolveMissionAsync(a.Req("mission"), ct);
                var body = new JsonObject
                {
                    ["volume"] = a.Int("volume", 100, 1, 100000),
                    ["ratePerSecond"] = a.Dbl("rate") ?? 10,
                    ["allowUnsafeMethods"] = a.Bool("allow_writes"),
                    ["hugeStringLength"] = 65536,
                };
                if (a.List("mutations") is { Length: > 0 } muts) body["mutations"] = new JsonArray(muts.Select(x => (JsonNode)x).ToArray());
                if (a.Str("request_id") is not null) body["seedLogId"] = RequestId(a);
                else
                {
                    var path = a.Str("path") ?? throw new ToolException("give request_id, or method and path");
                    body["sample"] = new JsonObject
                    {
                        ["method"] = (a.Str("method") ?? "GET").ToUpperInvariant(), ["path"] = path.StartsWith('/') ? path : "/" + path,
                        ["query"] = a.Str("query")?.TrimStart('?'), ["body"] = a.Str("body"),
                        ["headers"] = a.Str("body") is null ? null : new JsonObject { ["content-type"] = "application/json" },
                    };
                }
                var j = await api.PostAsync($"/api/missions/{m["id"]}/jobs/fuzz", body, ct);
                return new ToolResult($"Fuzz job {j["id"]} queued ({body["volume"]} variants). Check it with get_job; results appear in search_traffic with source=fuzz.", j.DeepClone());
            });

        yield return new ToolDef("start_rediff", "Re-check stored requests",
            "Background job: recompute every stored diff with the mission's current rules. Nothing is re-sent.",
            Schema.Object(("mission", MissionArg, true)), false, false, true, false,
            async (a, _, ct) =>
            {
                var m = await api.ResolveMissionAsync(a.Req("mission"), ct);
                var j = await api.PostAsync($"/api/missions/{m["id"]}/jobs/rediff", new JsonObject(), ct);
                return new ToolResult($"Re-diff job {j["id"]} queued.", j.DeepClone());
            });

        yield return new ToolDef("cancel_job", "Cancel a job",
            "Cancel a queued or running background job.",
            Schema.Object(("job_id", Schema.String("Job id"), true)), false, false, true, false,
            async (a, _, ct) =>
            {
                var j = await api.PostAsync($"/api/jobs/{Guid(a.Req("job_id"), "job_id")}/cancel", null, ct);
                return new ToolResult($"Job {j["id"]}: {j["status"]}{((bool?)j["cancelRequested"] == true ? " (cancelling)" : "")}.", j.DeepClone());
            });

        yield return new ToolDef("create_mission", "Create a mission",
            "Create a mission comparing a production API (main_url) with a candidate (dev_url). Only GET/HEAD are mirrored unless methods says otherwise; write methods also need acknowledge_unsafe_methods=true.",
            Schema.Object(
                ("name", Schema.String("Display name"), true),
                ("main_url", Schema.String("Production base URL (source of truth)"), true),
                ("dev_url", Schema.String("Candidate base URL"), true),
                ("slug", Schema.String("URL-safe id for the proxy endpoint /m/<slug>/ (default: from name)"), false),
                ("sample_rate", Schema.Number("Fraction of requests to mirror, 0–1 (default 1)"), false),
                ("methods", Schema.StringArray("HTTP methods to mirror (default GET, HEAD)"), false),
                ("acknowledge_unsafe_methods", Schema.Boolean("Confirm mirroring POST/PUT/PATCH/DELETE sends real writes to Dev", false), false),
                ("set_headers", Schema.AnyObject("Headers to set on Dev requests, e.g. {\"x-api-key\": \"dev\"}"), false)),
            false, false, false, false,
            async (a, _, ct) =>
            {
                var name = a.Req("name");
                var body = new JsonObject
                {
                    ["name"] = name,
                    ["slug"] = a.Str("slug") ?? Slug(name),
                    ["mainUrl"] = a.Req("main_url"),
                    ["devUrl"] = a.Req("dev_url"),
                    ["sampleRate"] = SampleRate(a) ?? 1,
                    ["mirrorMethods"] = new JsonArray((a.List("methods") ?? ["GET", "HEAD"]).Select(x => (JsonNode)x.ToUpperInvariant()).ToArray()),
                    ["acknowledgeUnsafeMethods"] = a.Bool("acknowledge_unsafe_methods"),
                };
                if (a.Obj("set_headers") is { } sh) body["headerPolicy"] = new JsonObject { ["strip"] = new JsonArray("authorization", "cookie", "proxy-authorization"), ["set"] = sh.DeepClone() };
                var m = await api.PostAsync("/api/missions", body, ct);
                api.InvalidateMissions();
                var cfg = await api.GetAsync("/api/config", ct);
                var warn = (m["warnings"] as JsonArray)?.FirstOrDefault() is { } w ? "\nWARNING: " + (string?)w : "";
                return new ToolResult($"Created mission \"{m["name"]}\" (slug {m["slug"]}). Point clients at {(string?)cfg["proxyPublicUrl"]}{m["proxyPath"]}/ instead of {m["mainUrl"]}; mirrored requests start appearing in a few seconds.{warn}", m.DeepClone());
            });

        yield return new ToolDef("update_mission", "Change a mission",
            "Change a mission's settings. Only the fields you pass change. Pause mirroring with mirror_enabled=false or sample_rate=0.",
            Schema.Object(("mission", MissionArg, true),
                ("name", Schema.String("New display name"), false),
                ("main_url", Schema.String("New production URL"), false),
                ("dev_url", Schema.String("New candidate URL"), false),
                ("enabled", Schema.Boolean("Proxy endpoint on/off"), false),
                ("mirror_enabled", Schema.Boolean("Mirroring to Dev on/off"), false),
                ("sample_rate", Schema.Number("Fraction of requests to mirror, 0–1"), false),
                ("methods", Schema.StringArray("HTTP methods to mirror"), false),
                ("acknowledge_unsafe_methods", Schema.Boolean("Required when adding POST/PUT/PATCH/DELETE", false), false),
                ("dev_timeout_ms", Schema.Integer("Dev timeout in ms"), false),
                ("retention_days", Schema.Integer("Days of logs to keep"), false)),
            false, false, true, false,
            async (a, _, ct) =>
            {
                var m = await api.ResolveMissionAsync(a.Req("mission"), ct);
                var body = new JsonObject { ["acknowledgeUnsafeMethods"] = a.Bool("acknowledge_unsafe_methods") };
                if (a.Str("name") is { } n) body["name"] = n;
                if (a.Str("main_url") is { } mu) body["mainUrl"] = mu;
                if (a.Str("dev_url") is { } du) body["devUrl"] = du;
                if (a.OptBool("enabled") is { } en) body["enabled"] = en;
                if (a.OptBool("mirror_enabled") is { } me) body["mirrorEnabled"] = me;
                if (SampleRate(a) is { } sr) body["sampleRate"] = sr;
                if (a.List("methods") is { } ms) body["mirrorMethods"] = new JsonArray(ms.Select(x => (JsonNode)x.ToUpperInvariant()).ToArray());
                if (a.Has("dev_timeout_ms")) body["devTimeoutMs"] = a.Int("dev_timeout_ms", 2000, 10, 120000);
                if (a.Has("retention_days")) body["retentionDays"] = a.Int("retention_days", 14, 1, 3650);
                if (body.Count == 1) throw new ToolException("nothing to change: pass at least one field (e.g. sample_rate=0.1)");
                var r = await api.PutAsync($"/api/missions/{m["id"]}", body, ct);
                api.InvalidateMissions();
                return new ToolResult($"Updated \"{r["name"]}\": enabled {r["enabled"]}, mirroring {r["mirrorEnabled"]} at {Pct((double)r["sampleRate"]!)}, methods {Join(r["mirrorMethods"])}. Proxies apply it within a few seconds.", r.DeepClone());
            });

        yield return new ToolDef("delete_mission", "Delete a mission",
            "Delete a mission and its whole traffic log. Irreversible. confirm must repeat the mission's slug exactly.",
            Schema.Object(("mission", MissionArg, true), ("confirm", Schema.String("The mission's slug, typed again to confirm"), true)),
            false, true, true, false,
            async (a, _, ct) =>
            {
                var m = await api.ResolveMissionAsync(a.Req("mission"), ct);
                if (!string.Equals(a.Req("confirm"), (string?)m["slug"], StringComparison.Ordinal))
                    throw new ToolException($"not deleted: confirm must be exactly \"{m["slug"]}\"");
                await api.DeleteAsync($"/api/missions/{m["id"]}", ct);
                api.InvalidateMissions();
                return new ToolResult($"Deleted mission \"{m["name"]}\" and its traffic log. Its proxy endpoint no longer works.");
            });
    }

    // ───────────────────────── helpers ─────────────────────────

    private static JsonObject RuleSchema(bool withRediff = false)
    {
        var props = new List<(string, JsonObject, bool)>
        {
            ("mission", MissionArg, true),
            ("type", Schema.String("Rule type (see list_rule_types)", ToolRegistry.RuleTypes), true),
            ("config", Schema.AnyObject("Rule config, e.g. {\"paths\": [\"$..requestId\"]} for IgnoreField or {\"path\": \"$.items\", \"key\": \"id\"} for PrimaryKey"), true),
        };
        if (withRediff) props.Add(("rediff", Schema.Boolean("Re-check stored requests with the new rules", true), false));
        return Schema.Object(props.ToArray());
    }

    private static async Task<(string Type, JsonObject Config)> ValidRuleAsync(KittyApi api, Args a, CancellationToken ct)
    {
        var type = a.Enum("type", ToolRegistry.RuleTypes) ?? throw new ToolException("'type' is required");
        var config = (JsonObject)(a.Obj("config") ?? throw new ToolException("'config' is required, e.g. {\"paths\": [\"$.field\"]}")).DeepClone();
        // Forgive the most common small-model slips.
        if (type == "IgnoreField" && config["path"] is JsonValue && config["paths"] is null) { config["paths"] = new JsonArray(config["path"]!.DeepClone()); config.Remove("path"); }
        if (type == "IgnoreHeader" && config["header"] is JsonValue && config["headers"] is null) { config["headers"] = new JsonArray(config["header"]!.DeepClone()); config.Remove("header"); }
        var v = await api.PostAsync("/api/rules/validate", new JsonObject { ["type"] = type, ["config"] = config.DeepClone() }, ct);
        if ((bool?)v["valid"] != true)
            throw new ToolException($"invalid {type} config {config.ToJsonString()}: {string.Join("; ", (v["errors"] as JsonArray ?? []).Select(e => (string?)e))}. See list_rule_types for an example.");
        return (type, config);
    }

    private static async Task<JsonArray> CurrentRulesAsync(KittyApi api, JsonObject m, CancellationToken ct)
    {
        var rules = (JsonArray)await api.GetAsync($"/api/missions/{m["id"]}/rules", ct);
        return new JsonArray(rules.OfType<JsonObject>().Select(r => (JsonNode)new JsonObject
        {
            ["id"] = (string?)r["id"], ["type"] = (string?)r["type"], ["name"] = (string?)r["name"], ["enabled"] = (bool)r["enabled"]!, ["config"] = r["config"]!.DeepClone(),
        }).ToArray());
    }

    private static string FormatRules(JsonArray rules)
    {
        if (rules.Count == 0) return "Rules: none (Main and Dev are compared exactly).\n";
        var sb = new StringBuilder($"Rules ({rules.Count}, applied in order):\n");
        var i = 0;
        foreach (var r in rules.OfType<JsonObject>())
            sb.Append($"  {++i}. {r["type"]} {r["config"]!.ToJsonString()}{((bool?)r["enabled"] == false ? " (disabled)" : "")}  [id {r["id"]}]\n");
        return sb.ToString();
    }

    private static readonly string[] FieldKinds = ["changed", "added", "removed", "type_changed"];

    /// <summary>"2 changed, 1 removed, status, body": field counts first, then the other kinds of difference.</summary>
    private static string MismatchParts(JsonObject i, JsonObject? s)
    {
        var parts = new[] { ("changed", s?["changed"]), ("added", s?["added"]), ("removed", s?["removed"]), ("type", s?["typeChanged"]) }
            .Where(x => x.Item2 is JsonValue v && v.TryGetValue<int>(out var n) && n > 0).Select(x => $"{x.Item2} {x.Item1}").ToList();
        parts.AddRange((i["diffKinds"] as JsonArray ?? []).Select(k => (string?)k).Where(k => k is not null && !FieldKinds.Contains(k)).Select(k => k!.Replace('_', ' ')));
        return parts.Count == 0 ? "see get_request_diff" : string.Join(", ", parts);
    }

    private static string Row(JsonObject i)
    {
        var s = i["summary"] as JsonObject;
        var detail = (string?)i["verdict"] switch
        {
            "mismatch" => $"{i["diffCount"]} differences ({MismatchParts(i, s)})",
            "dev_error" or "dev_timeout" or "not_mirrored" => (string?)i["note"] ?? "",
            _ => "",
        };
        var q = (string?)i["query"] is { } qs ? "?" + (qs.Length > 60 ? qs[..60] + "…" : qs) : "";
        return $"- {i["id"]} · {((string?)i["ts"])?[..19].Replace('T', ' ')} · {i["method"]} {i["path"]}{q} · {i["mainStatus"]}→{i["devStatus"]?.ToString() ?? "-"} · {Ms((double?)i["mainMs"])}/{Ms((double?)i["devMs"])} · {i["verdict"]} {detail}".TrimEnd();
    }

    private static string Diff(JsonObject d)
    {
        string V(string? v) => v is null ? "(absent)" : v.Length > 70 ? v[..70] + "…" : v;
        return (string?)d["k"] switch
        {
            "added" => $"added {d["p"]} on Dev: {V((string?)d["d"])}",
            "removed" => $"missing on Dev {d["p"]} (Main: {V((string?)d["m"])})",
            "type_changed" => $"type changed {d["p"]}: {V((string?)d["m"])} → {V((string?)d["d"])}",
            "array_length" => $"array length {d["p"]}: {d["m"]} → {d["d"]}",
            "order" => $"order differs {d["p"]}: {V((string?)d["m"])}",
            "status" => $"status code: {d["m"]} → {d["d"]}",
            "body" => $"body (not JSON on both sides): {V((string?)d["m"])} → {V((string?)d["d"])}",
            "header" => $"header {((string?)d["p"])?.Replace("header:", "")}: {V((string?)d["m"])} → {V((string?)d["d"])}",
            _ => $"{d["k"]} {d["p"]}: {V((string?)d["m"])} → {V((string?)d["d"])}",
        };
    }

    private static string JobLine(JsonObject j)
    {
        var progress = j["total"] is null ? $"{j["done"]} done" : $"{j["done"]}/{j["total"]}";
        return $"- {j["type"]} job {j["id"]} · {j["status"]}{((bool?)j["cancelRequested"] == true ? " (cancelling)" : "")} · {progress}{((string?)j["error"] is { } e && (string?)j["status"] != "cancelled" ? " · error: " + e : "")}";
    }

    /// <summary>Same heuristics as the UI's one-click suggestions.</summary>
    internal static JsonObject? Suggest(string kind, string path, string? main, string? dev)
    {
        var wild = WildcardRegex().Replace(path, "[*]");
        if (kind is "added" or "removed" && path.EndsWith(']')) return null;
        JsonObject R(string type, JsonObject cfg, string? note = null) => new() { ["type"] = type, ["config"] = cfg, ["note"] = note };
        switch (kind)
        {
            case "header": return R("IgnoreHeader", new JsonObject { ["headers"] = new JsonArray(path.Replace("header:", "")) });
            case "type_changed": return R("TypeCoercion", new JsonObject { ["path"] = wild });
            case "order": return R("IgnoreOrder", new JsonObject { ["path"] = wild });
            case "array_length": return R("PrimaryKey", new JsonObject { ["path"] = wild, ["key"] = "id" }, "set key to the field that identifies items");
            case "changed" when double.TryParse(main, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(dev, NumberStyles.Float, CultureInfo.InvariantCulture, out var y):
                return R("NumericTolerance", new JsonObject { ["path"] = wild, ["tolerance"] = double.Parse((Math.Abs(x - y) * 1.05).ToString("G2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture), ["mode"] = "absolute" });
            case "status" or "body": return null;
            default: return R("IgnoreField", new JsonObject { ["paths"] = new JsonArray(wild) }, kind == "changed" ? "only if this value is expected to differ" : null);
        }
    }

    [GeneratedRegex(@"\[(?:\d+|[^\]'""]+=[^\]]*)\]")]
    private static partial Regex WildcardRegex();

    private static string RequestId(Args a) => Guid(a.Req("request_id"), "request_id");

    private static string Guid(string s, string name) =>
        System.Guid.TryParse(s.Trim(), out var g) ? g.ToString() : throw new ToolException($"'{name}' must be a full id like 01a1b728-d8c6-72c5-81c4-3fe2c070e4c6 (copy it from a previous result)");

    private static double? SampleRate(Args a)
    {
        var v = a.Dbl("sample_rate");
        if (v is null) return null;
        if (v > 1 && v <= 100) v /= 100; // "10" meaning 10 %
        if (v is < 0 or > 1) throw new ToolException("sample_rate must be between 0 and 1 (0.1 = 10 %)");
        return v;
    }

    private static string Slug(string name)
    {
        var s = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return s.Length == 0 ? "mission" : s[..Math.Min(63, s.Length)];
    }

    private static string Since(int hours) => Uri.EscapeDataString(DateTime.UtcNow.AddHours(-hours).ToString("O", CultureInfo.InvariantCulture));
    private static string Join(JsonNode? arr) => string.Join(",", (arr as JsonArray ?? []).Select(x => (string?)x));
    private static string Pct(double? r) => r is null ? "n/a" : (r * 100).Value.ToString(r is 0 or 1 ? "0" : "0.#", CultureInfo.InvariantCulture) + "%";
    private static string Ms(double? ms) => ms is null ? "-" : ms >= 1000 ? (ms / 1000).Value.ToString("0.00", CultureInfo.InvariantCulture) + " s" : Math.Round(ms.Value).ToString(CultureInfo.InvariantCulture) + " ms";
    private static string Bytes(int? b) => b is null ? "-" : b >= 1048576 ? $"{b / 1048576.0:0.0} MB" : b >= 1024 ? $"{b / 1024.0:0.0} KB" : $"{b} B";
    private static long Sum(JsonNode? o) => (o as JsonObject ?? []).Sum(kv => (long?)kv.Value?.GetValue<double>() ?? 0);
    private static string Kv(JsonNode? o) => (o as JsonObject ?? []).Count == 0 ? "" : "(" + string.Join(", ", (o as JsonObject)!.Select(kv => $"{kv.Key} {kv.Value}")) + ")";
}
