import { useEffect, useRef, useState } from "react";
import type { AlignResult, KeySpec } from "./align";

export interface DiffState {
  loading: boolean;
  error: string | null;
  result: AlignResult | null;
  bytes: { main: number; dev: number } | null;
  ms: { fetch: number; diff: number } | null;
}

let worker: Worker | null = null;
let seq = 0;

function getWorker(): Worker {
  worker ??= new Worker(new URL("./diff.worker.ts", import.meta.url), { type: "module" });
  return worker;
}

/** Runs the side-by-side alignment for two body URLs in the shared diff worker. */
export function useDiff(mainUrl: string | null, devUrl: string | null, keys: KeySpec[] = []): DiffState {
  const [state, setState] = useState<DiffState>({ loading: true, error: null, result: null, bytes: null, ms: null });
  const current = useRef(0);
  const keysJson = JSON.stringify(keys);

  useEffect(() => {
    if (mainUrl == null || devUrl == null) return;
    const w = getWorker();
    const id = ++seq;
    current.current = id;
    setState((s) => ({ ...s, loading: true, error: null }));
    const onMessage = (e: MessageEvent) => {
      if (e.data.id !== current.current) return;
      if (e.data.ok) setState({ loading: false, error: null, result: e.data.result, bytes: e.data.bytes, ms: e.data.ms });
      else setState({ loading: false, error: e.data.error, result: null, bytes: null, ms: null });
    };
    w.addEventListener("message", onMessage);
    w.postMessage({ id, mainUrl, devUrl, keys: JSON.parse(keysJson) });
    return () => w.removeEventListener("message", onMessage);
  }, [mainUrl, devUrl, keysJson]);

  return state;
}
