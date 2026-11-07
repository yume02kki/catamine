/// <reference lib="webworker" />
// Fetches both bodies and aligns them off the main thread, so multi-MB responses never block the UI.
import { align, type KeySpec } from "./align";

export interface DiffRequest {
  id: number;
  mainUrl: string;
  devUrl: string;
  keys?: KeySpec[];
}

self.onmessage = async (e: MessageEvent<DiffRequest>) => {
  const { id, mainUrl, devUrl, keys } = e.data;
  const started = performance.now();
  try {
    const [mainText, devText] = await Promise.all([fetchText(mainUrl), fetchText(devUrl)]);
    const fetched = performance.now();
    const result = align(mainText, devText, { keys });
    (self as unknown as Worker).postMessage({
      id,
      ok: true,
      result,
      bytes: { main: mainText.length, dev: devText.length },
      ms: { fetch: Math.round(fetched - started), diff: Math.round(performance.now() - fetched) },
    });
  } catch (err) {
    (self as unknown as Worker).postMessage({ id, ok: false, error: String(err) });
  }
};

async function fetchText(url: string): Promise<string> {
  if (!url) return "";
  const res = await fetch(url);
  if (res.status === 404) return "";
  if (!res.ok) throw new Error(`${url}: HTTP ${res.status}`);
  return res.text();
}
