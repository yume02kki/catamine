import { useQueryClient, type InfiniteData } from "@tanstack/react-query";
import { useVirtualizer } from "@tanstack/react-virtual";
import { useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { Link, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { exportUrl } from "../api/client";
import { keys, useClusters, useLogs, useMission, useStartJob, useStats } from "../api/queries";
import type { Cluster, LogItem, LogPage, Mission } from "../api/types";
import { Confirm, DiffBar, Empty, ErrorNote, MethodPill, StatusPair, VerdictTag } from "../components/Bits";
import { IconClose, IconSearch } from "../components/Icons";
import { Kitty } from "../components/Kitty";
import { useLiveLogs } from "../live/useLiveLogs";
import { MissionHeader, ProxyBar } from "../components/MissionHeader";
import { matches, RANGES, readView, toFilters, writeView, type Range, type ViewState } from "../lib/filters";
import { fmtInt, fmtMs, fmtPct, fmtTime, sinceHours } from "../lib/format";
import { describe, encodeDraft, suggestRule } from "../lib/suggest";
import { setNeighbours } from "./neighbours";

const ROW_H = 38;

export function TrafficPage() {
  const { id = "" } = useParams();
  const { data: mission, error } = useMission(id);
  if (error) return <div className="page"><ErrorNote error={error} /></div>;
  if (!mission) return <div className="page"><div className="skeleton" /></div>;
  return <Traffic mission={mission} />;
}

function Traffic({ mission }: { mission: Mission }) {
  const id = mission.id;
  const [params, setParams] = useSearchParams();
  const view = readView(params);
  const setView = (patch: Partial<ViewState>) => setParams(writeView({ ...view, ...patch }), { replace: true });
  const [stamp, setStamp] = useState(() => Date.now());
  const filters = useMemo(() => toFilters(view, stamp), [view.q, view.verdict, view.sort, view.range, view.sig, stamp]); // eslint-disable-line react-hooks/exhaustive-deps
  const [draftQ, setDraftQ] = useState(view.q);
  useEffect(() => setDraftQ(view.q), [view.q]);

  const logs = useLogs(id, filters);
  const rows = useMemo(() => logs.data?.pages.flatMap((p) => p.items) ?? [], [logs.data]);
  useEffect(() => setNeighbours(id, rows.map((r) => r.id), params.toString()), [id, rows, params]);

  const statsFrom = useMemo(() => sinceHours(RANGES[view.range]), [view.range, stamp]); // eslint-disable-line react-hooks/exhaustive-deps
  const { data: stats } = useStats(id, statsFrom);
  const { data: clusters } = useClusters(id, statsFrom);

  const [selected, setSelected] = useState<string | null>(null);
  const sel = rows.find((r) => r.id === selected) ?? null;
  const nav = useNavigate();

  // ── live updates ─────────────────────────────────────────────
  const qc = useQueryClient();
  const scrollRef = useRef<HTMLDivElement>(null);
  const [atTop, setAtTop] = useState(true);
  const [pending, setPending] = useState<LogItem[]>([]);
  const [live, setLive] = useState(true);
  const liveFits = view.sort === "newest";
  const connected = useLiveLogs(id, live && liveFits, (items) => {
    const fresh = items.filter((i) => matches(i, filters)).reverse(); // newest first
    if (fresh.length === 0) return;
    if (atTop) prepend(fresh);
    else setPending((p) => [...fresh, ...p].slice(0, 2000));
    qc.invalidateQueries({ queryKey: keys.stats(id, statsFrom) });
  });
  const prepend = (items: LogItem[]) =>
    qc.setQueryData<InfiniteData<LogPage, string | null>>(keys.logs(id, filters), (old) => {
      if (!old || old.pages.length === 0) return old;
      const seen = new Set(old.pages[0].items.map((i) => i.id));
      const add = items.filter((i) => !seen.has(i.id));
      return { ...old, pages: [{ ...old.pages[0], items: [...add, ...old.pages[0].items] }, ...old.pages.slice(1)] };
    });
  const showPending = () => {
    prepend(pending);
    setPending([]);
    scrollRef.current?.scrollTo({ top: 0 });
  };
  useEffect(() => { setPending([]); }, [filters]);

  // ── virtual list ─────────────────────────────────────────────
  const virt = useVirtualizer({ count: rows.length, getScrollElement: () => scrollRef.current, estimateSize: () => ROW_H, overscan: 12 });
  const items = virt.getVirtualItems();
  const last = items[items.length - 1];
  useEffect(() => {
    if (last && last.index >= rows.length - 20 && logs.hasNextPage && !logs.isFetchingNextPage) logs.fetchNextPage();
  }, [last?.index, rows.length, logs.hasNextPage, logs.isFetchingNextPage]); // eslint-disable-line react-hooks/exhaustive-deps

  const onKey = (e: KeyboardEvent) => {
    if (rows.length === 0) return;
    const idx = Math.max(0, rows.findIndex((r) => r.id === selected));
    if (e.key === "j" || e.key === "ArrowDown") { e.preventDefault(); const n = Math.min(rows.length - 1, selected ? idx + 1 : 0); setSelected(rows[n].id); virt.scrollToIndex(n); }
    if (e.key === "k" || e.key === "ArrowUp") { e.preventDefault(); const n = Math.max(0, idx - 1); setSelected(rows[n].id); virt.scrollToIndex(n); }
    if (e.key === "Enter" && selected) nav(`/missions/${id}/logs/${selected}`);
    if (e.key === "Escape") setSelected(null);
  };

  const startJob = useStartJob(id);
  const [confirmReplay, setConfirmReplay] = useState(false);

  const v = stats?.verdicts ?? {};
  const compared = (v.match?.count ?? 0) + (v.mismatch?.count ?? 0);
  const mainAvg = weighted(stats, "mainMsAvg");
  const devAvg = weighted(stats, "devMsAvg");
  const diffChip = (verdicts: string) => () => setView({ verdict: view.verdict === verdicts ? "" : verdicts });

  return (
    <div className="page page-fill">
      <MissionHeader mission={mission}>
        <button type="button" className="btn" onClick={() => setConfirmReplay(true)}>Replay filtered</button>
        <a className="btn" href={exportUrl(id)} download>Export</a>
      </MissionHeader>
      <ProxyBar mission={mission} />

      <div className="stat-strip" aria-label="Summary">
        <span>Total <b>{fmtInt(stats?.total)}</b></span>
        <span>Differ <b className="c-amber">{fmtInt(v.mismatch?.count ?? 0)} · {fmtPct(compared ? (v.mismatch?.count ?? 0) / compared : null)}</b></span>
        <span>Causes <b className="c-pink">{clusters?.length ?? "—"}</b></span>
        <span>Main avg <b>{fmtMs(mainAvg)}</b></span>
        <span>Dev avg <b>{fmtMs(devAvg)}</b>{mainAvg && devAvg ? <em className={devAvg > mainAvg * 1.2 ? "c-red" : "c-teal"}> {devAvg >= mainAvg ? "+" : ""}{Math.round((devAvg / mainAvg - 1) * 100)}%</em> : null}</span>
        <span>Dev errors <b className={(v.dev_error?.count ?? 0) + (v.dev_timeout?.count ?? 0) > 0 ? "c-red" : "c-teal"}>{fmtInt((v.dev_error?.count ?? 0) + (v.dev_timeout?.count ?? 0))}</b></span>
        <span>Not mirrored <b>{fmtInt(v.not_mirrored?.count ?? 0)}</b></span>
      </div>

      <div className="split">
        <section className="panel log-panel" aria-label="Traffic log">
          <div className="filter-bar">
            <form className="search grow" onSubmit={(e) => { e.preventDefault(); setView({ q: draftQ }); }}>
              <IconSearch />
              <label className="sr-only" htmlFor="logq">Filter</label>
              <input id="logq" className="mono" placeholder="path:/chat  method:POST  status:502  kind:added" value={draftQ}
                onChange={(e) => setDraftQ(e.target.value)} onBlur={() => draftQ !== view.q && setView({ q: draftQ })} />
            </form>
            <button type="button" aria-pressed={view.verdict === "mismatch"} className={`chip ${view.verdict === "mismatch" ? "chip-amber" : ""}`} onClick={diffChip("mismatch")}>Diffs only</button>
            <button type="button" aria-pressed={view.verdict === "dev_error,dev_timeout"} className={`chip ${view.verdict === "dev_error,dev_timeout" ? "chip-red" : ""}`} onClick={diffChip("dev_error,dev_timeout")}>Dev errors</button>
            <button type="button" aria-pressed={view.verdict === "not_mirrored"} className={`chip ${view.verdict === "not_mirrored" ? "chip-active" : ""}`} onClick={diffChip("not_mirrored")}>Not mirrored</button>
            <label className="sr-only" htmlFor="sort">Sort</label>
            <select id="sort" className="select" value={view.sort} onChange={(e) => setView({ sort: e.target.value as ViewState["sort"] })}>
              <option value="newest">Newest</option><option value="oldest">Oldest</option><option value="slowest">Slowest Dev</option><option value="most_different">Most different</option>
            </select>
            <label className="sr-only" htmlFor="range">Time range</label>
            <select id="range" className="select" value={view.range} onChange={(e) => { setView({ range: e.target.value as Range }); setStamp(Date.now()); }}>
              <option value="1h">Last hour</option><option value="24h">Last 24 h</option><option value="7d">Last 7 days</option><option value="30d">Last 30 days</option><option value="all">All time</option>
            </select>
          </div>
          {view.sig && (
            <div className="banner">Showing one cause only <button type="button" className="link-btn" onClick={() => setView({ sig: "" })}><IconClose width={12} height={12} /> clear</button></div>
          )}
          {pending.length > 0 && (
            <button type="button" className="banner banner-pink" onClick={showPending}>↑ {fmtInt(pending.length)} new — paused while you scroll · show</button>
          )}
          <div className="log-head" aria-hidden="true">
            <span>time</span><span>method</span><span>path</span><span>main → dev</span><span className="r">dev ms</span><span>differences</span><span className="r">count</span>
          </div>
          <div className="log-scroll" ref={scrollRef} tabIndex={0} role="listbox" aria-label="Requests (j/k to move, enter to open)"
            aria-activedescendant={selected ? `row-${selected}` : undefined}
            onKeyDown={onKey} onScroll={(e) => setAtTop(e.currentTarget.scrollTop < 8)}>
            {logs.isLoading && <div className="skeleton-list">{Array.from({ length: 10 }, (_, i) => <div key={i} className="skeleton skeleton-row" />)}</div>}
            {!logs.isLoading && rows.length === 0 && (
              <Empty title="No requests here yet">Send traffic to the proxy endpoint above, or widen the filters.</Empty>
            )}
            <div style={{ height: virt.getTotalSize(), position: "relative" }}>
              {items.map((vi) => {
                const r = rows[vi.index];
                const slow = r.devMs != null && r.devMs > r.mainMs * 1.5 && r.devMs - r.mainMs > 20;
                return (
                  <div key={r.id} id={`row-${r.id}`} role="option" aria-selected={selected === r.id}
                    className={`log-row ${selected === r.id ? "selected" : ""} v-${r.verdict}`}
                    style={{ transform: `translateY(${vi.start}px)`, height: ROW_H }}
                    onClick={() => setSelected(r.id)} onDoubleClick={() => nav(`/missions/${id}/logs/${r.id}`)}>
                    <span className="mono dim">{fmtTime(r.ts)}</span>
                    <span><MethodPill method={r.method} /></span>
                    <span className="mono ellipsis" title={r.path + (r.query ? `?${r.query}` : "")}>{r.path}{r.query ? <span className="dim">?{r.query}</span> : null}{r.source !== "live" ? <span className="src">{r.source}</span> : null}</span>
                    <span><StatusPair main={r.mainStatus} dev={r.devStatus} /></span>
                    <span className={`mono r ${slow ? "c-red" : ""}`}>{r.verdict === "dev_timeout" ? "timeout" : fmtMs(r.devMs)}</span>
                    <span><DiffBar summary={r.summary} /></span>
                    <span className="r"><VerdictTag log={r} /></span>
                  </div>
                );
              })}
            </div>
            {logs.isFetchingNextPage && <div className="muted-text small pad">loading more…</div>}
          </div>
          <div className="log-foot">
            <span>{fmtInt(rows.length)} loaded{logs.hasNextPage ? " · scroll for more" : ""}</span>
            <label className="check small"><input type="checkbox" checked={live} onChange={(e) => setLive(e.target.checked)} /> live</label>
            <span className={`dot ${connected && liveFits ? "dot-ok" : "dot-off"}`} />
            <span>{!liveFits ? "live updates only when sorted by newest" : connected ? "streaming · 1 s batches" : live ? "connecting…" : "paused"}</span>
            <span className="grow" /><span className="dim">j/k move · enter open · esc clear</span>
          </div>
        </section>

        <aside className="panel side-panel" aria-label={sel ? "Selected request" : "Causes"}>
          {sel ? <Inspector mission={mission} log={sel} onClose={() => setSelected(null)} /> : (
            <Causes mission={mission} clusters={clusters} total={v.mismatch?.count ?? 0} onFilter={(s) => setView({ sig: s, verdict: "" })} />
          )}
        </aside>
      </div>

      {confirmReplay && (
        <Confirm
          title="Replay the filtered requests?"
          body={<>Re-sends up to 5,000 logged requests matching the current filters to <b>both</b> Main and Dev at 10 req/s and compares the results. Write methods are skipped. Progress shows under Replay &amp; fuzz.</>}
          confirm="Start replay"
          onConfirm={() => {
            setConfirmReplay(false);
            const { sort: _s, ...filter } = filters;
            startJob.mutate({ type: "replay", body: { filter, ratePerSecond: 10, limit: 5000 } }, { onSuccess: () => nav(`/missions/${id}/replay`) });
          }}
          onClose={() => setConfirmReplay(false)}
        />
      )}
    </div>
  );
}

function weighted(stats: ReturnType<typeof useStats>["data"], key: "mainMsAvg" | "devMsAvg"): number | null {
  if (!stats) return null;
  let n = 0, sum = 0;
  for (const [verdict, s] of Object.entries(stats.verdicts)) {
    if (!s || (key === "devMsAvg" && verdict === "not_mirrored")) continue;
    n += s.count;
    sum += s[key] * s.count;
  }
  return n ? sum / n : null;
}

function Causes({ mission, clusters, total, onFilter }: { mission: Mission; clusters?: Cluster[]; total: number; onFilter: (sig: string) => void }) {
  return (
    <div className="causes">
      <div className="panel-title"><h2>What's different here</h2><span className="muted-text small">{fmtInt(total)} differing · {clusters?.length ?? 0} causes</span></div>
      {clusters && clusters.length === 0 && (
        <div className="calm"><Kitty mood="happy" size="sm" /><p>Nothing differs in this time range. Main and Dev agree.</p></div>
      )}
      {clusters?.map((c) => {
        const top = c.summary?.top?.[0];
        const sug = top ? suggestRule(top) : null;
        const others = (c.summary?.top?.length ?? 1) - 1 + Math.max(0, (c.summary ? c.summary.changed + c.summary.added + c.summary.removed + c.summary.typeChanged : 0) - (c.summary?.top?.length ?? 0));
        return (
          <div key={c.signature} className="cause">
            <div className="cause-head">
              {top && <span className={`kind kind-${top.k}`}>{top.k.replace("_", " ")}</span>}
              <span className="mono ellipsis" title={top?.p}>{top?.p ?? c.example.path}</span>
              <b className="cause-n">{fmtInt(c.count)}</b>
            </div>
            <div className="cause-text">{top ? describe(top) : "Responses differ."}{others > 0 ? <span className="dim"> · +{fmtInt(others)} more paths</span> : null}</div>
            <div className="cause-actions">
              {sug && <Link to={`/missions/${mission.id}/rules?add=${encodeDraft(sug.rule)}`}>{sug.label}</Link>}
              <Link to={`/missions/${mission.id}/logs/${c.example.id}`} className="muted-link">Show example</Link>
              <button type="button" className="link-btn muted-link" onClick={() => onFilter(c.signature)}>Only these</button>
            </div>
          </div>
        );
      })}
      <div className="hint-card">
        <Kitty size="face" mood="idle" />
        <span>Click a row to inspect it, double-click or <kbd>enter</kbd> for the side-by-side diff. <kbd>j</kbd>/<kbd>k</kbd> to move.</span>
      </div>
    </div>
  );
}

function Inspector({ mission, log, onClose }: { mission: Mission; log: LogItem; onClose: () => void }) {
  const s = log.summary;
  return (
    <div className="inspector">
      <div className="panel-title">
        <MethodPill method={log.method} />
        <span className="mono ellipsis">{log.path}{log.query ? `?${log.query}` : ""}</span>
        <button type="button" className="icon-btn" aria-label="Close" onClick={onClose}><IconClose /></button>
      </div>
      <div className="facts">
        <div><span>Status</span><StatusPair main={log.mainStatus} dev={log.devStatus} /></div>
        <div><span>Latency</span><b className="mono">{fmtMs(log.mainMs)} → {fmtMs(log.devMs)}</b></div>
        <div><span>Verdict</span><VerdictTag log={log} /></div>
        <div><span>Rules</span><b className="mono">v{log.rulesVersion}</b></div>
      </div>
      {log.note && <div className="note">{log.note}</div>}
      {s && (
        <>
          <div className="counts">
            <span className="c-amber">~{fmtInt(s.changed)} changed</span>
            <span className="c-teal">+{fmtInt(s.added)} added</span>
            <span className="c-red">−{fmtInt(s.removed)} removed</span>
            <span className="c-lilac">{fmtInt(s.typeChanged)} type</span>
          </div>
          <ul className="top-diffs">
            {s.top.map((d, i) => {
              const sug = suggestRule(d);
              return (
                <li key={i}>
                  <span className={`kind kind-${d.k}`}>{d.k.replace("_", " ")}</span>
                  <span className="mono ellipsis" title={d.p}>{d.p}</span>
                  <span className="mono vals" title={`${d.m ?? "—"} → ${d.d ?? "—"}`}>{d.m ?? "—"} → {d.d ?? "—"}</span>
                  {sug && <Link className="small" to={`/missions/${mission.id}/rules?add=${encodeDraft(sug.rule)}`}>{sug.label}</Link>}
                </li>
              );
            })}
          </ul>
          {s.arrays.length > 0 && <div className="muted-text small">Array lengths: {s.arrays.map((a) => `${a.p} ${a.m}→${a.d}`).join(" · ")}</div>}
          {s.headers.length > 0 && <div className="muted-text small">Headers differ: {s.headers.join(", ")}</div>}
        </>
      )}
      <div className="row-end">
        <Link className="btn btn-primary" to={`/missions/${mission.id}/logs/${log.id}`}>Open diff ⏎</Link>
      </div>
    </div>
  );
}
