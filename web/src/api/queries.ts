import { keepPreviousData, useInfiniteQuery, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "./client";
import type { LogFilters, MissionUpsert, RuleInput } from "./types";

export const keys = {
  config: ["config"] as const,
  missions: ["missions"] as const,
  mission: (id: string) => ["missions", id] as const,
  overview: (hours: number) => ["overview", hours] as const,
  logs: (id: string, f: LogFilters) => ["logs", id, f] as const,
  logDetail: (id: string, logId: string) => ["log-detail", id, logId] as const,
  stats: (id: string, from?: string) => ["stats", id, from] as const,
  clusters: (id: string, from?: string) => ["clusters", id, from] as const,
  rules: (id: string) => ["rules", id] as const,
  ruleTypes: ["rule-types"] as const,
  jobs: (missionId?: string) => ["jobs", missionId ?? "all"] as const,
  proxies: ["proxies"] as const,
};

export const useConfig = () => useQuery({ queryKey: keys.config, queryFn: api.config, staleTime: Infinity });
export const useMissions = () => useQuery({ queryKey: keys.missions, queryFn: () => api.missions() });
export const useMission = (id: string) => useQuery({ queryKey: keys.mission(id), queryFn: () => api.mission(id) });
export const useOverview = (hours = 24) =>
  useQuery({ queryKey: keys.overview(hours), queryFn: () => api.overview(hours, 24), refetchInterval: 30_000 });
export const useProxies = () => useQuery({ queryKey: keys.proxies, queryFn: api.proxies, refetchInterval: 10_000 });
export const useRuleTypes = () => useQuery({ queryKey: keys.ruleTypes, queryFn: api.ruleTypes, staleTime: Infinity });
export const useRules = (id: string) => useQuery({ queryKey: keys.rules(id), queryFn: () => api.rules(id) });

export const useStats = (id: string, from?: string) =>
  useQuery({ queryKey: keys.stats(id, from), queryFn: () => api.stats(id, from), refetchInterval: 15_000, placeholderData: keepPreviousData });
export const useClusters = (id: string, from?: string) =>
  useQuery({ queryKey: keys.clusters(id, from), queryFn: () => api.clusters(id, from), refetchInterval: 30_000, placeholderData: keepPreviousData });

/** Keyset-paginated log; all filtering and sorting happens on the server. */
export const useLogs = (id: string, filters: LogFilters) =>
  useInfiniteQuery({
    queryKey: keys.logs(id, filters),
    queryFn: ({ pageParam, signal }) => api.logs(id, filters, pageParam, 100, signal),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor,
    placeholderData: keepPreviousData,
    staleTime: Infinity, // live updates arrive over SSE
  });

export const useLogDetail = (id: string, logId: string) =>
  useQuery({ queryKey: keys.logDetail(id, logId), queryFn: () => api.logDetail(id, logId) });

export const useJobs = (missionId?: string) =>
  useQuery({
    queryKey: keys.jobs(missionId),
    queryFn: () => api.jobs(missionId),
    refetchInterval: (q) => (q.state.data?.items.some((j) => j.status === "running" || j.status === "queued") ? 1500 : 10_000),
  });

export function useSaveMission() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ id, body }: { id?: string; body: MissionUpsert }) => (id ? api.updateMission(id, body) : api.createMission(body)),
    onSuccess: (m) => {
      qc.setQueryData(keys.mission(m.id), m);
      qc.invalidateQueries({ queryKey: keys.missions });
    },
  });
}

export function useDeleteMission() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (id: string) => api.deleteMission(id),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.missions }),
  });
}

export function useSaveRules(missionId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ rules, rediff }: { rules: RuleInput[]; rediff: boolean }) => api.saveRules(missionId, rules, rediff),
    onSuccess: (r) => {
      qc.setQueryData(keys.rules(missionId), r.rules);
      qc.invalidateQueries({ queryKey: keys.mission(missionId) });
      qc.invalidateQueries({ queryKey: keys.missions });
      qc.invalidateQueries({ queryKey: keys.jobs(missionId) });
    },
  });
}

export function useStartJob(missionId: string) {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ type, body }: { type: "rediff" | "replay" | "fuzz" | "export"; body?: unknown }) => api.startJob(missionId, type, body),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: keys.jobs(missionId) });
      qc.invalidateQueries({ queryKey: keys.jobs() });
    },
  });
}

export function useCancelJob() {
  const qc = useQueryClient();
  return useMutation({ mutationFn: api.cancelJob, onSuccess: () => qc.invalidateQueries({ queryKey: ["jobs"] }) });
}
