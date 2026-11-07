// Wire types of the api service (see src/Mirror.Api/Contracts.cs).

export type Verdict = "match" | "mismatch" | "dev_error" | "dev_timeout" | "not_mirrored";
export type DiffKind = "status" | "changed" | "added" | "removed" | "type_changed" | "array_length" | "order" | "header" | "body";

export interface HeaderPolicy {
  strip: string[];
  set: Record<string, string>;
}

export interface Mission {
  id: string;
  slug: string;
  name: string;
  description: string | null;
  mainUrl: string;
  devUrl: string;
  proxyPath: string;
  enabled: boolean;
  mirrorEnabled: boolean;
  sampleRate: number;
  mirrorMethods: string[];
  maxMirrorBodyBytes: number;
  devTimeoutMs: number;
  devMaxConcurrency: number;
  headerPolicy: HeaderPolicy;
  comparedHeaders: string[];
  rulesVersion: number;
  retentionDays: number;
  maxStorageMb: number | null;
  createdAt: string;
  updatedAt: string;
  warnings: string[];
  ruleCount: number;
}

export type MissionUpsert = Partial<Omit<Mission, "id" | "proxyPath" | "createdAt" | "updatedAt" | "warnings" | "ruleCount" | "rulesVersion">> & {
  acknowledgeUnsafeMethods?: boolean;
};

/** One difference: kind, path, main value, dev value (compact JSON, truncated). */
export interface Difference {
  k: DiffKind;
  p: string;
  m: string | null;
  d: string | null;
}

export interface DiffSummary {
  changed: number;
  added: number;
  removed: number;
  typeChanged: number;
  order: number;
  headers: string[];
  arrays: { p: string; m: number; d: number }[];
  top: Difference[];
  truncated: boolean;
  note: string | null;
}

export interface LogItem {
  id: string;
  ts: string;
  method: string;
  path: string;
  query: string | null;
  mainStatus: number;
  devStatus: number | null;
  mainMs: number;
  devMs: number | null;
  verdict: Verdict;
  diffKinds: DiffKind[];
  diffCount: number;
  signature: string;
  rulesVersion: number;
  source: "live" | "replay" | "fuzz" | "import";
  jobId: string | null;
  replayOf: string | null;
  note: string | null;
  summary: DiffSummary | null;
}

export interface LogPage {
  items: LogItem[];
  nextCursor: string | null;
}

export interface ExchangePart {
  status: number | null;
  headers: Record<string, string>;
  bodyBytes: number;
  bodyUrl: string;
}

export interface LogDetail {
  log: LogItem;
  request: ExchangePart;
  main: ExchangePart;
  dev: ExchangePart | null;
  differences: Difference[];
}

export interface Rule {
  id: string;
  order: number;
  type: string;
  name: string | null;
  enabled: boolean;
  config: Record<string, unknown>;
}

export interface RuleInput {
  id?: string;
  type: string;
  name?: string | null;
  enabled: boolean;
  config: Record<string, unknown>;
}

export interface RuleType {
  type: string;
  description: string;
  example: Record<string, unknown>;
}

export interface RulePreview {
  sampled: number;
  mismatchesBefore: number;
  mismatchesAfter: number;
  nowMatching: number;
  nowMismatching: number;
  ruleErrors: { rule: string; error: string }[];
  examples: { logId: string; path: string; before: number; after: number }[];
}

export type JobStatus = "queued" | "running" | "succeeded" | "failed" | "cancelled";

export interface Job {
  id: string;
  missionId: string | null;
  type: "rediff" | "replay" | "fuzz" | "export" | "import";
  status: JobStatus;
  done: number;
  total: number | null;
  params: Record<string, unknown> | null;
  result: Record<string, unknown> | null;
  error: string | null;
  cancelRequested: boolean;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
}

export interface Stats {
  from: string;
  to: string;
  total: number;
  matchRate: number | null;
  verdicts: Partial<Record<Verdict, { count: number; mainMsAvg: number; devMsAvg: number }>>;
  timeline: { bucket: string; counts: Partial<Record<Verdict, number>> }[];
}

export interface Cluster {
  signature: string;
  count: number;
  firstSeen: string;
  lastSeen: string;
  example: { id: string; method: string; path: string };
  summary: DiffSummary | null;
}

export interface MissionOverview {
  missionId: string;
  total: number;
  match: number;
  mismatch: number;
  devError: number;
  devTimeout: number;
  notMirrored: number;
  diffRate: number | null;
  mainMsAvg: number;
  devMsAvg: number;
  buckets: { total: number; mismatch: number; errors: number }[];
}

export interface LatencySnapshot {
  count: number;
  avgMs: number;
  p50Ms: number;
  p99Ms: number;
}

export interface ProxyInstance {
  instanceId: string;
  lastSeen: string;
  startedAt: string;
  metrics: {
    queueDepth: number;
    queueCapacity: number;
    logQueueDepth: number;
    captureBytes: number;
    enqueued: number;
    dropped: Record<string, number>;
    notMirrored: Record<string, number>;
    logWritten: number;
    logDropped: Record<string, number>;
    mainRequests: Record<string, number>;
    mainLatencyMs: { mirrored: LatencySnapshot; unmirrored: LatencySnapshot };
    circuits: Record<string, string>;
    configSource: string | null;
    configLoadedAt: string | null;
    workingSetBytes: number;
  };
}

export interface AppConfig {
  proxyPublicUrl: string;
  version: string;
}

/** Filters understood by GET /api/missions/{id}/logs (all optional). */
export interface LogFilters {
  verdict?: string;
  diffKind?: string;
  path?: string;
  pathMode?: "prefix" | "contains" | "exact";
  method?: string;
  mainStatus?: number;
  devStatus?: number;
  source?: string;
  jobId?: string;
  replayOf?: string;
  signature?: string;
  from?: string;
  to?: string;
  sort?: "newest" | "oldest" | "slowest" | "most_different";
}
