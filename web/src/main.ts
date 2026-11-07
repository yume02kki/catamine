// Placeholder until the real React UI (TanStack Query/Virtual, Web Worker diffing) is built.
async function show(id: string, url: string, pick: (data: any) => unknown) {
  const el = document.getElementById(id)!;
  try {
    const res = await fetch(url);
    el.textContent = res.ok ? JSON.stringify(pick(await res.json()), null, 2) : `HTTP ${res.status}`;
  } catch (e) {
    el.textContent = String(e);
  }
}

function refresh() {
  show("missions", "/api/missions", (ms: any[]) =>
    ms.map((m) => ({ name: m.name, proxy: m.proxyPath, main: m.mainUrl, dev: m.devUrl, sampleRate: m.sampleRate })));
  show("proxies", "/api/proxy/instances", (ps: any[]) =>
    ps.map((p) => ({ id: p.instanceId, queue: p.metrics.queueDepth, dropped: p.metrics.dropped, latency: p.metrics.mainLatencyMs })));
}

refresh();
setInterval(refresh, 5000);
