import { useEffect, useRef, useState } from "react";
import { liveUrl } from "../api/client";
import type { LogItem } from "../api/types";

/**
 * Subscribes to the mission's SSE stream. The server already batches (≤ 1 event/s); on top of that the client
 * coalesces events into one callback per second so a burst never causes more than one re-render per second.
 */
export function useLiveLogs(missionId: string, enabled: boolean, onBatch: (items: LogItem[]) => void) {
  const [connected, setConnected] = useState(false);
  const cb = useRef(onBatch);
  cb.current = onBatch;

  useEffect(() => {
    if (!enabled) {
      setConnected(false);
      return;
    }
    const es = new EventSource(liveUrl(missionId));
    let buffer: LogItem[] = [];
    let timer: ReturnType<typeof setTimeout> | null = null;
    const flush = () => {
      timer = null;
      if (buffer.length === 0) return;
      const items = buffer;
      buffer = [];
      cb.current(items);
    };
    es.addEventListener("ready", () => setConnected(true));
    es.addEventListener("logs", (e) => {
      const data = JSON.parse((e as MessageEvent).data) as { items: LogItem[] };
      buffer = buffer.concat(data.items);
      if (buffer.length > 2000) buffer = buffer.slice(-2000);
      timer ??= setTimeout(flush, 1000);
    });
    es.onerror = () => setConnected(false);
    es.onopen = () => setConnected(true);
    return () => {
      es.close();
      if (timer) clearTimeout(timer);
    };
  }, [missionId, enabled]);

  return connected;
}
