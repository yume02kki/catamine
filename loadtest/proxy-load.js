// Main-path latency with and without mirroring, under the same load.
//
// Three back-to-back phases at the same constant arrival rate:
//   direct     → upstream Main directly (no proxy)           — the floor
//   proxy_off  → proxy, mission with mirroring disabled     — cost of the reverse proxy itself
//   proxy_10   → proxy, mission mirroring 10% (a typical production sampling rate)
//   proxy_on   → proxy, mission mirroring 100% to a slower, flakier Dev
// The interesting number is proxy_on vs proxy_off: what mirroring adds to the critical path.
//
// Env: PROXY, MAIN, DEV, API, RATE (req/s, default 1500), DURATION (per phase, default 60s), VUS (default 400).
import http from "k6/http";
import { check, sleep } from "k6";

const PROXY = __ENV.PROXY || "http://localhost:8080";
const MAIN = __ENV.MAIN || "http://localhost:5001";
const API = __ENV.API || "http://localhost:8081";
const DEV = __ENV.DEV || MAIN.replace("main", "dev");
const RATE = parseInt(__ENV.RATE || "1500", 10);
const DURATION = __ENV.DURATION || "60s";
const VUS = parseInt(__ENV.VUS || "400", 10);
const seconds = parseInt(DURATION, 10);
const gap = 10;

function phase(name, startAfter) {
  return {
    executor: "constant-arrival-rate",
    rate: RATE,
    timeUnit: "1s",
    duration: DURATION,
    preAllocatedVUs: Math.min(VUS, RATE),
    maxVUs: VUS,
    startTime: `${startAfter}s`,
    exec: name,
    tags: { phase: name },
  };
}

export const options = {
  discardResponseBodies: true,
  scenarios: {
    direct: phase("direct", 0),
    proxy_off: phase("proxy_off", seconds + gap),
    proxy_10: phase("proxy_10", 2 * (seconds + gap)),
    proxy_on: phase("proxy_on", 3 * (seconds + gap)),
  },
  summaryTrendStats: ["avg", "p(50)", "p(90)", "p(95)", "p(99)", "max"],
  thresholds: {
    // Declaring per-phase thresholds makes k6 report each phase separately in the summary.
    "http_req_duration{phase:direct}": ["p(99)<1000"],
    "http_req_duration{phase:proxy_off}": ["p(99)<1000"],
    "http_req_duration{phase:proxy_10}": ["p(99)<1000"],
    "http_req_duration{phase:proxy_on}": ["p(99)<1000"],
    "http_req_failed{phase:proxy_on}": ["rate<0.001"],
    "http_req_failed{phase:proxy_off}": ["rate<0.001"],
  },
};

function path() {
  const q = ["lamp", "desk", "chair", "sofa", "rug", "shelf", "bed", "table"][Math.floor(Math.random() * 8)];
  const page = 1 + Math.floor(Math.random() * 5);
  return `/v1/search?q=${q}&page=${page}`;
}

function hit(url) {
  const res = http.get(url, { timeout: "10s" });
  check(res, { "status 200": (r) => r.status === 200 });
}

export function direct() { hit(`${MAIN}${path()}`); }
export function proxy_off() { hit(`${PROXY}/m/bench-off${path()}`); }
export function proxy_10() { hit(`${PROXY}/m/bench-10${path()}`); }
export function proxy_on() { hit(`${PROXY}/m/bench-on${path()}`); }

export function setup() {
  // Make sure both missions exist (the compose "seed" service creates them too).
  const missions = [
    { slug: "bench-off", name: "Load test (mirroring off)", mainUrl: MAIN, devUrl: DEV, mirrorEnabled: false, sampleRate: 0 },
    { slug: "bench-10", name: "Load test (10% mirrored)", mainUrl: MAIN, devUrl: DEV, sampleRate: 0.1, mirrorMethods: ["GET", "HEAD"] },
    { slug: "bench-on", name: "Load test (100% mirrored)", mainUrl: MAIN, devUrl: DEV, sampleRate: 1, mirrorMethods: ["GET", "HEAD"] },
  ];
  for (const m of missions) {
    http.post(`${API}/api/missions`, JSON.stringify(m), { headers: { "content-type": "application/json" } });
  }
  // Wait for the proxy to pick up the config (it polls Postgres every few seconds).
  for (let i = 0; i < 30; i++) {
    const ok = ["bench-on", "bench-10", "bench-off"].every((s) => http.get(`${PROXY}/m/${s}/health`).status === 200);
    if (ok) break;
    sleep(1);
  }
}

export function teardown() {
  // Proxy-side view: queue, drops and main-path latency as measured inside the proxy.
  const res = http.get(`${PROXY}/metrics/json`, { responseType: "text" });
  if (res.status === 200) console.log(`proxy metrics: ${res.body}`);
}

function stat(data, phase, key) {
  const m = data.metrics[`http_req_duration{phase:${phase}}`];
  return m ? m.values[key] : NaN;
}

export function handleSummary(data) {
  const phases = ["direct", "proxy_off", "proxy_10", "proxy_on"];
  const keys = ["avg", "p(50)", "p(95)", "p(99)", "max"];
  const rows = phases.map((p) => ({
    phase: p,
    ...Object.fromEntries(keys.map((k) => [k, Number(stat(data, p, k).toFixed(2))])),
    failed: data.metrics[`http_req_failed{phase:${p}}`]?.values.rate ?? null,
  }));
  const off = rows[1];
  const delta = (r) => ({
    p50_ms: Number((r["p(50)"] - off["p(50)"]).toFixed(2)),
    p95_ms: Number((r["p(95)"] - off["p(95)"]).toFixed(2)),
    p99_ms: Number((r["p(99)"] - off["p(99)"]).toFixed(2)),
  });
  const overhead = { mirror10: delta(rows[2]), mirror100: delta(rows[3]) };
  const pad = (s, n) => String(s).padEnd(n);
  let text = `\nMain-path latency at ${RATE} req/s for ${DURATION} per phase (ms)\n`;
  text += pad("phase", 12) + keys.map((k) => pad(k, 10)).join("") + "failed\n";
  for (const r of rows) text += pad(r.phase, 12) + keys.map((k) => pad(r[k], 10)).join("") + `${((r.failed ?? 0) * 100).toFixed(3)}%\n`;
  for (const [name, o] of [["10% mirrored", overhead.mirror10], ["100% mirrored", overhead.mirror100]])
    text += `\nOverhead vs proxy_off, ${name}: p50 ${o.p50_ms} ms, p95 ${o.p95_ms} ms, p99 ${o.p99_ms} ms`;
  text += "\n";

  return {
    stdout: text,
    "/scripts/results/compare.json": JSON.stringify({ rate: RATE, duration: DURATION, phases: rows, overhead }, null, 2),
  };
}
