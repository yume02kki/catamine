import type {
  AppConfig, Cluster, Job, LogDetail, LogFilters, LogItem, LogPage, Mission, MissionOverview, MissionUpsert,
  ProxyInstance, Rule, RuleInput, RulePreview, RuleType, Stats,
} from "./types";

export class ApiError extends Error {
  constructor(public status: number, message: string, public fields?: Record<string, string[]>) {
    super(message);
  }
}

async function request<T>(method: string, url: string, body?: unknown, signal?: AbortSignal): Promise<T> {
  const res = await fetch(url, {
    method,
    signal,
    headers: body === undefined ? undefined : { "content-type": "application/json" },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    let message = `${res.status} ${res.statusText}`;
    let fields: Record<string, string[]> | undefined;
    try {
      const data = await res.json();
      if (data?.errors) {
        fields = data.errors;
        message = Object.values(data.errors as Record<string, string[]>).flat().join(" ");
      } else if (data?.error) message = data.error;
      else if (data?.title) message = data.title;
    } catch {
      /* not JSON */
    }
    throw new ApiError(res.status, message, fields);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

const get = <T>(url: string, signal?: AbortSignal) => request<T>("GET", url, undefined, signal);

export function toQuery(params: object): string {
  const q = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v === undefined || v === null || v === "") continue;
    q.set(k, String(v));
  }
  const s = q.toString();
  return s ? `?${s}` : "";
}

export const api = {
  config: () => get<AppConfig>("/api/config"),

  missions: (q?: string) => get<Mission[]>(`/api/missions${toQuery({ q })}`),
  mission: (id: string) => get<Mission>(`/api/missions/${id}`),
  overview: (hours = 24, buckets = 24) => get<MissionOverview[]>(`/api/missions/overview${toQuery({ hours, buckets })}`),
  createMission: (body: MissionUpsert) => request<Mission>("POST", "/api/missions", body),
  updateMission: (id: string, body: MissionUpsert) => request<Mission>("PUT", `/api/missions/${id}`, body),
  deleteMission: (id: string) => request<void>("DELETE", `/api/missions/${id}`),

  logs: (missionId: string, filters: LogFilters, cursor?: string | null, limit = 100, signal?: AbortSignal) =>
    get<LogPage>(`/api/missions/${missionId}/logs${toQuery({ ...filters, cursor, limit })}`, signal),
  log: (missionId: string, id: string) => get<LogItem>(`/api/missions/${missionId}/logs/${id}`),
  logDetail: (missionId: string, id: string) => get<LogDetail>(`/api/missions/${missionId}/logs/${id}/detail`),
  replay: (missionId: string, id: string, allowUnsafe = false) =>
    request<{ original: LogItem; replay: LogItem; differences: unknown[] }>("POST", `/api/missions/${missionId}/logs/${id}/replay${toQuery({ allowUnsafe: allowUnsafe || undefined })}`),
  stats: (missionId: string, from?: string, to?: string) => get<Stats>(`/api/missions/${missionId}/stats${toQuery({ from, to })}`),
  clusters: (missionId: string, from?: string, to?: string, limit = 20) =>
    get<Cluster[]>(`/api/missions/${missionId}/clusters${toQuery({ from, to, limit })}`),

  rules: (missionId: string) => get<Rule[]>(`/api/missions/${missionId}/rules`),
  saveRules: (missionId: string, rules: RuleInput[], rediff: boolean) =>
    request<{ rulesVersion: number; rules: Rule[]; job: Job | null }>("PUT", `/api/missions/${missionId}/rules`, { rules, rediff }),
  previewRules: (missionId: string, rules: RuleInput[], sampleSize = 200, signal?: AbortSignal) =>
    request<RulePreview>("POST", `/api/missions/${missionId}/rules/preview`, { rules, sampleSize }, signal),
  ruleTypes: () => get<RuleType[]>("/api/rule-types"),
  validateRule: (type: string, config: unknown, signal?: AbortSignal) =>
    request<{ valid: boolean; errors: string[] }>("POST", "/api/rules/validate", { type, config }, signal),

  jobs: (missionId?: string, cursor?: string | null, limit = 50) =>
    get<{ items: Job[]; nextCursor: string | null }>(`/api/jobs${toQuery({ missionId, cursor, limit })}`),
  job: (id: string) => get<Job>(`/api/jobs/${id}`),
  cancelJob: (id: string) => request<Job>("POST", `/api/jobs/${id}/cancel`),
  startJob: (missionId: string, type: "rediff" | "replay" | "fuzz" | "export", body: unknown = {}) =>
    request<Job>("POST", `/api/missions/${missionId}/jobs/${type}`, body),
  importFile: async (file: File, slug?: string) => {
    const res = await fetch(`/api/import${toQuery({ slug })}`, { method: "POST", body: file, headers: { "content-type": "application/gzip" } });
    if (!res.ok) throw new ApiError(res.status, (await res.json().catch(() => ({})))?.error ?? res.statusText);
    return (await res.json()) as Job;
  },

  proxies: () => get<ProxyInstance[]>("/api/proxy/instances"),
};

export const exportUrl = (missionId: string) => `/api/missions/${missionId}/export`;
export const downloadUrl = (jobId: string) => `/api/jobs/${jobId}/download`;
export const liveUrl = (missionId: string) => `/api/missions/${missionId}/live`;
