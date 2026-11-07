# Proxy load test (k6)

`proxy-load.js` drives four back-to-back phases at the same constant arrival rate and reports main-path latency for each:

| phase | target | what it isolates |
|---|---|---|
| `direct` | upstream Main, no proxy | the floor |
| `proxy_off` | proxy, mission with mirroring disabled | cost of the reverse proxy itself |
| `proxy_10` | proxy, 10 % of GETs mirrored | a typical production setting |
| `proxy_on` | proxy, 100 % of GETs mirrored to a slower, flakier Dev (5–25 ms, 1 % 502s, 0.5 % 3 s stalls) | worst case |

The number that matters is **`proxy_on`/`proxy_10` minus `proxy_off`**: what mirroring adds to the critical path.

## Run

```bash
# with docker compose (builds images, seeds the bench missions)
docker compose --profile loadtest up -d --build
RATE=1500 DURATION=60s docker compose --profile loadtest run --rm k6

# or against services you run yourself
k6 run -e PROXY=http://localhost:8080 -e MAIN=http://localhost:5001 -e DEV=http://localhost:5002 \
       -e API=http://localhost:8081 -e RATE=1000 -e DURATION=30s loadtest/proxy-load.js
```

Results: printed table, `results/compare.json`, `results/summary.json`. The proxy's own view (queue depth, drops, latency measured inside the proxy) is printed at teardown from `/metrics/json`.

## Results from the development run

Single 4-vCPU VM running **everything** (k6, proxy, both upstreams, Postgres, api). 1000 req/s, 30 s per phase, default proxy settings:

| phase | avg | p50 | p95 | p99 | failed |
|---|---|---|---|---|---|
| direct | 0.42 ms | 0.34 ms | 0.58 ms | 2.04 ms | 0 % |
| proxy_off | 0.72 ms | 0.64 ms | 1.09 ms | 2.64 ms | 0 % |
| proxy_10 | 0.75 ms | 0.63 ms | 1.36 ms | 3.75 ms | 0 % |
| proxy_on | 1.16 ms | 0.74 ms | 3.19 ms | 5.82 ms | 0 % |

Mirroring overhead vs `proxy_off`: **10 %: p50 −0.01 ms, p95 +0.27 ms, p99 +1.1 ms. 100 %: p50 +0.10 ms, p95 +2.1 ms, p99 +3.2 ms.**
No mirror jobs or log records were dropped, and the circuit stayed closed. Inside the proxy, time on the main path averaged 0.51 ms for mirrored requests and 0.40 ms for the rest.

The compose run (800 req/s, 20 s phases) gave the same picture: +0 / +0.25 / +0.9 ms (p50/p95/p99) at 10 %, +0.09 / +2.2 / +3.3 ms at 100 %.

The median is unchanged. The tail grows when mirroring is on because, on this box, the *same four cores* also run the Dev upstream, the diff workers, Brotli compression and the Postgres writes, and all of that only happens in the mirrored phases. The critical path does no Dev, diff or database work itself (see `docs/ARCHITECTURE.md` §3). With Dev and Postgres on other machines, as in a real deployment, most of that contention goes away. `Mirror__DiffConcurrency` (default: cores/4) caps how much CPU the diff stage can take.
