import type { LogFilters, LogItem } from "../api/types";

export const RANGES = { "1h": 1, "24h": 24, "7d": 24 * 7, "30d": 24 * 30, all: null } as const;
export type Range = keyof typeof RANGES;

export interface ViewState {
  q: string;
  verdict: string; // comma-separated verdicts or ""
  sort: NonNullable<LogFilters["sort"]>;
  range: Range;
  sig: string;
}

export function readView(p: URLSearchParams): ViewState {
  const range = (p.get("range") ?? "24h") as Range;
  return {
    q: p.get("q") ?? "",
    verdict: p.get("verdict") ?? "",
    sort: (p.get("sort") as ViewState["sort"]) ?? "newest",
    range: range in RANGES ? range : "24h",
    sig: p.get("sig") ?? "",
  };
}

export function writeView(v: ViewState): URLSearchParams {
  const p = new URLSearchParams();
  if (v.q) p.set("q", v.q);
  if (v.verdict) p.set("verdict", v.verdict);
  if (v.sort !== "newest") p.set("sort", v.sort);
  if (v.range !== "24h") p.set("range", v.range);
  if (v.sig) p.set("sig", v.sig);
  return p;
}

/**
 * Search syntax: `path:/v1/search` (prefix), `method:POST`, `status:502` (Dev), `main:500`, `kind:added`,
 * `verdict:dev_error`, `source:replay`, `job:<id>`; any other text matches anywhere in the path.
 */
export function toFilters(v: ViewState, now = Date.now()): LogFilters {
  const f: LogFilters = { sort: v.sort };
  const words: string[] = [];
  for (const tok of v.q.trim().split(/\s+/).filter(Boolean)) {
    const m = /^(\w+):(.*)$/.exec(tok);
    if (!m) { words.push(tok); continue; }
    const [, key, val] = m;
    switch (key.toLowerCase()) {
      case "path": f.path = val; f.pathMode = "prefix"; break;
      case "method": f.method = val.toUpperCase(); break;
      case "status": case "dev": f.devStatus = Number(val) || undefined; break;
      case "main": f.mainStatus = Number(val) || undefined; break;
      case "kind": f.diffKind = val; break;
      case "verdict": f.verdict = val; break;
      case "source": f.source = val; break;
      case "job": f.jobId = val; break;
      default: words.push(tok);
    }
  }
  if (words.length && !f.path) { f.path = words.join(" "); f.pathMode = "contains"; }
  if (v.verdict && !f.verdict) f.verdict = v.verdict;
  if (v.sig) f.signature = v.sig;
  const hours = RANGES[v.range];
  if (hours != null) f.from = new Date(now - hours * 3600_000).toISOString();
  return f;
}

/** Client-side check used to decide whether a live (SSE) row belongs in the current filtered view. */
export function matches(item: LogItem, f: LogFilters): boolean {
  if (f.verdict && !f.verdict.split(",").includes(item.verdict)) return false;
  if (f.method && item.method !== f.method) return false;
  if (f.devStatus && item.devStatus !== f.devStatus) return false;
  if (f.mainStatus && item.mainStatus !== f.mainStatus) return false;
  if (f.diffKind && !f.diffKind.split(",").some((k) => item.diffKinds.includes(k as never))) return false;
  if (f.source && item.source !== f.source) return false;
  if (f.signature && item.signature !== f.signature) return false;
  if (f.jobId && item.jobId !== f.jobId) return false;
  if (f.path) {
    if (f.pathMode === "prefix" && !item.path.startsWith(f.path.replace(/\*$/, ""))) return false;
    if (f.pathMode === "contains" && !item.path.toLowerCase().includes(f.path.toLowerCase())) return false;
  }
  return true;
}
