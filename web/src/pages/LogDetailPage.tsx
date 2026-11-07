import { useQuery } from "@tanstack/react-query";
import { useVirtualizer } from "@tanstack/react-virtual";
import { useEffect, useMemo, useRef, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import { api } from "../api/client";
import { useConfig, useLogDetail, useLogs, useMission, useRules } from "../api/queries";
import type { Difference, LogDetail, Mission } from "../api/types";
import { CopyButton, Empty, ErrorNote, MethodPill, StatusPair, VerdictTag } from "../components/Bits";
import { Kitty } from "../components/Kitty";
import { fold, findRow, K, type KeySpec, type Row } from "../diff/align";
import { useDiff } from "../diff/useDiff";
import { fmtBytes, fmtInt, fmtMs, fmtTime, verdictLabel } from "../lib/format";
import { arrayPathOf, encodeDraft, suggestRule, wildcardPath } from "../lib/suggest";
import { neighbours } from "./neighbours";

type Tab = "body" | "headers" | "request" | "replays";
const LINE_H = 21;
const KIND_CLASS: Record<number, string> = { [K.Same]: "", [K.Changed]: "d-changed", [K.Added]: "d-added", [K.Removed]: "d-removed", [K.Type]: "d-type" };
const DIFF_COLOR: Record<string, string> = { changed: "c-amber", added: "c-teal", removed: "c-red", type_changed: "c-lilac", header: "c-blue", status: "c-red", array_length: "c-blue", order: "c-blue", body: "c-amber" };
const DIFF_SIGN: Record<string, string> = { changed: "~", added: "+", removed: "−", type_changed: "T", header: "H", status: "S", array_length: "#", order: "⇅", body: "B" };

export function LogDetailPage() {
  const { id = "", logId = "" } = useParams();
  const { data: mission } = useMission(id);
  const { data: detail, error } = useLogDetail(id, logId);
  if (error) return <div className="page"><ErrorNote error={error} /></div>;
  if (!mission || !detail) return <div className="page"><div className="skeleton" /><div className="skeleton" /></div>;
  return <Detail key={logId} mission={mission} detail={detail} />;
}

function Detail({ mission, detail }: { mission: Mission; detail: LogDetail }) {
  const log = detail.log;
  const nav = useNavigate();
  const [tab, setTab] = useState<Tab>("body");
  const [normalized, setNormalized] = useState(true);
  const [folding, setFolding] = useState(true);
  const [replaying, setReplaying] = useState(false);
  const [replayError, setReplayError] = useState<unknown>(null);
  const nb = neighbours(mission.id, log.id);
  const back = `/missions/${mission.id}${nb.search ? `?${nb.search}` : ""}`;

  const replay = async () => {
    setReplaying(true);
    setReplayError(null);
    try {
      const r = await api.replay(mission.id, log.id, !["GET", "HEAD", "OPTIONS"].includes(log.method));
      nav(`/missions/${mission.id}/logs/${r.replay.id}`);
    } catch (e) {
      setReplayError(e);
    } finally {
      setReplaying(false);
    }
  };

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.target instanceof HTMLInputElement || e.target instanceof HTMLTextAreaElement) return;
      if (e.key === "[" && nb.prev) nav(`/missions/${mission.id}/logs/${nb.prev}`);
      if (e.key === "]" && nb.next) nav(`/missions/${mission.id}/logs/${nb.next}`);
      if (e.key === "Escape") nav(back);
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [nb.prev, nb.next, back, mission.id, nav]);

  const s = log.summary;
  return (
    <div className="page page-fill detail-page">
      <header className="page-head">
        <Link to={back} className="crumb">{mission.name}</Link>
        <span className="dim">/</span>
        <MethodPill method={log.method} />
        <span className="mono strong ellipsis">{log.path}{log.query ? <span className="dim">?{log.query}</span> : null}</span>
        <span className="mono dim small">{fmtTime(log.ts)} · {log.id.slice(0, 13)}</span>
        {log.source !== "live" && <span className="pill">{log.source}</span>}
        {log.replayOf && <Link className="pill pill-pink" to={`/missions/${mission.id}/logs/${log.replayOf}`}>replay of original</Link>}
        <div className="head-actions">
          <button type="button" className="btn" disabled={!nb.prev} onClick={() => nb.prev && nav(`/missions/${mission.id}/logs/${nb.prev}`)} title="[">‹ prev</button>
          <button type="button" className="btn" disabled={!nb.next} onClick={() => nb.next && nav(`/missions/${mission.id}/logs/${nb.next}`)} title="]">next ›</button>
          <button type="button" className="btn btn-primary" disabled={replaying} onClick={replay}>{replaying ? "Replaying…" : "Replay & compare"}</button>
        </div>
      </header>
      <ErrorNote error={replayError} />

      <div className="facts-strip">
        <div><span>Status</span><StatusPair main={log.mainStatus} dev={log.devStatus} /></div>
        <div><span>Latency</span><b className={`mono ${log.devMs != null && log.devMs > log.mainMs * 1.5 ? "c-red" : ""}`}>{fmtMs(log.mainMs)} → {fmtMs(log.devMs)}</b></div>
        <div><span>Size</span><b className="mono">{fmtBytes(detail.main.bodyBytes)} → {fmtBytes(detail.dev?.bodyBytes)}</b></div>
        <div><span>Fields</span><b className="mono"><span className="c-amber">~{fmtInt(s?.changed ?? 0)}</span> <span className="c-teal">+{fmtInt(s?.added ?? 0)}</span> <span className="c-red">−{fmtInt(s?.removed ?? 0)}</span></b></div>
        <div><span>Types</span><b className="mono c-lilac">{fmtInt(s?.typeChanged ?? 0)} changed</b></div>
        <div><span>Verdict</span><span><VerdictTag log={log} /> <span className="dim small">{verdictLabel[log.verdict]} · rules v{log.rulesVersion}</span></span></div>
      </div>
      {log.note && <div className="note">{log.note}</div>}

      <div className="tab-row">
        <div className="tabs" role="tablist" aria-label="View">
          {(["body", "headers", "request", "replays"] as Tab[]).map((t) => (
            <button key={t} type="button" role="tab" aria-selected={tab === t} className={`tab ${tab === t ? "active" : ""}`} onClick={() => setTab(t)}>
              {{ body: "Body", headers: "Headers", request: "Request", replays: "Replays" }[t]}
            </button>
          ))}
        </div>
        {tab === "body" && (
          <>
            <label className="check"><input type="checkbox" checked={normalized} onChange={(e) => setNormalized(e.target.checked)} /> Normalised (rules v{mission.rulesVersion})</label>
            <label className="check"><input type="checkbox" checked={folding} onChange={(e) => setFolding(e.target.checked)} /> Fold unchanged</label>
          </>
        )}
      </div>

      {tab === "body" && <BodyDiff mission={mission} detail={detail} normalized={normalized} folding={folding} />}
      {tab === "headers" && <HeadersTab detail={detail} />}
      {tab === "request" && <RequestTab mission={mission} detail={detail} />}
      {tab === "replays" && <ReplaysTab mission={mission} logId={log.replayOf ?? log.id} />}
    </div>
  );
}

function BodyDiff({ mission, detail, normalized, folding }: { mission: Mission; detail: LogDetail; normalized: boolean; folding: boolean }) {
  const q = normalized ? "?normalized=true" : "";
  const { data: rules } = useRules(mission.id);
  const keys = useMemo<KeySpec[]>(() => (rules ?? [])
    .filter((r) => r.enabled && r.type === "PrimaryKey" && typeof r.config.path === "string")
    .map((r) => ({ pattern: r.config.path as string, keys: Array.isArray(r.config.keys) && r.config.keys.length ? (r.config.keys as string[]) : [String(r.config.key)] })), [rules]);
  const diff = useDiff(`${detail.main.bodyUrl}${q}`, detail.dev ? `${detail.dev.bodyUrl}${q}` : "", keys);
  const rows = diff.result?.rows ?? [];
  // Lines that differ textually but that the server did not report were removed from the comparison by rules:
  // show them dimmed so real differences stand out. Only possible when the server's list is complete.
  const complete = detail.log.diffCount <= detail.differences.length;
  const reported = useMemo(() => detail.differences.filter((d) => d.p.startsWith("$")), [detail.differences]);
  // Array-level findings (length, order) cover only the array line itself, not every element below it.
  const covers = (d: Difference, r: Row) => d.k === "array_length" || d.k === "order"
    ? r.path === d.p
    : r.path === d.p || r.path.startsWith(d.p + ".") || r.path.startsWith(d.p + "[") || (r.k === K.Changed && d.p.startsWith(r.path));
  const isIgnored = (r: Row) => complete && r.k !== K.Same && !reported.some((d) => covers(d, r));
  const ignoredCount = useMemo(() => (complete ? rows.filter(isIgnored).length : 0), [rows, reported, complete]); // eslint-disable-line react-hooks/exhaustive-deps
  const [expanded, setExpanded] = useState<Set<number>>(() => new Set());
  const display = useMemo(() => fold(rows, 3, expanded, folding), [rows, expanded, folding]);
  const [active, setActive] = useState<number>(-1);
  const [jumpRow, setJumpRow] = useState<number | null>(null);
  const scrollRef = useRef<HTMLDivElement>(null);
  const virt = useVirtualizer({ count: display.length, getScrollElement: () => scrollRef.current, estimateSize: () => LINE_H, overscan: 30 });

  const changes = detail.differences;
  const jumpTo = (d: Difference, i: number) => {
    setActive(i);
    const r = findRow(rows, d.p);
    if (r >= 0) setJumpRow(r);
  };
  useEffect(() => {
    if (jumpRow == null) return;
    const di = display.findIndex((x) => x.t === "row" && x.i === jumpRow);
    if (di >= 0) {
      virt.scrollToIndex(di, { align: "center" });
      setJumpRow(null);
      return;
    }
    const f = display.find((x) => x.t === "fold" && x.start <= jumpRow && jumpRow < x.end);
    if (f && f.t === "fold") setExpanded((s) => new Set(s).add(f.start));
    else setJumpRow(null);
  }, [jumpRow, display]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.target instanceof HTMLInputElement || changes.length === 0) return;
      if (e.key === "n") jumpTo(changes[Math.min(changes.length - 1, active + 1)], Math.min(changes.length - 1, active + 1));
      if (e.key === "N") jumpTo(changes[Math.max(0, active - 1)], Math.max(0, active - 1));
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }); // re-bind with fresh state each render (cheap)

  const selected = active >= 0 ? changes[active] : null;
  const suggestions = selected ? [suggestRule(selected), arrayPathOf(selected.p) && selected.k !== "header" && !keys.some((k) => wildcardPath(k.pattern) === arrayPathOf(selected.p))
    ? { label: "Match items by key…", rule: { type: "PrimaryKey", enabled: true, config: { path: arrayPathOf(selected.p)!, key: "id" } } } : null,
    selected.k !== "header" && selected.k !== "status" && !/\]$/.test(selected.p) ? { label: `Ignore ${wildcardPath(selected.p)}`, rule: { type: "IgnoreField", enabled: true, config: { paths: [wildcardPath(selected.p)] } } } : null]
    .filter((x, i, a) => x && a.findIndex((y) => y?.label === x.label) === i) : [];

  const changesRef = useRef<HTMLDivElement>(null);
  const changeVirt = useVirtualizer({ count: changes.length, getScrollElement: () => changesRef.current, estimateSize: () => 28, overscan: 10 });

  return (
    <div className="diff-layout">
      <aside className="panel changes-panel" aria-label="Changes">
        <div className="panel-sub">{fmtInt(changes.length)} differences{detail.log.diffCount > changes.length ? ` (of ${fmtInt(detail.log.diffCount)})` : ""} · <kbd>n</kbd>/<kbd>N</kbd></div>
        <div className="changes-scroll" ref={changesRef}>
          {changes.length === 0 && <div className="calm small"><Kitty size="face" mood="happy" /> No differences.</div>}
          <div style={{ height: changeVirt.getTotalSize(), position: "relative" }}>
            {changeVirt.getVirtualItems().map((vi) => {
              const d = changes[vi.index];
              return (
                <button key={vi.index} type="button" className={`change ${active === vi.index ? "active" : ""}`} style={{ transform: `translateY(${vi.start}px)` }}
                  onClick={() => jumpTo(d, vi.index)} title={`${d.p}\n${d.m ?? "—"} → ${d.d ?? "—"}`}>
                  <span className={`sign ${DIFF_COLOR[d.k] ?? ""}`}>{DIFF_SIGN[d.k] ?? "?"}</span>
                  <span className="mono ellipsis">{d.p}</span>
                </button>
              );
            })}
          </div>
        </div>
        {selected && (
          <div className="selected-change">
            <span className="muted-text small">Selected · {selected.k.replace("_", " ")}</span>
            <div className="mono small vals">{selected.m ?? "—"} → {selected.d ?? "—"}</div>
            {suggestions.map((sg) => sg && (
              <Link key={sg.label} className="btn btn-sm btn-soft" to={`/missions/${mission.id}/rules?add=${encodeDraft(sg.rule)}`}>{sg.label}</Link>
            ))}
          </div>
        )}
      </aside>

      <section className="panel sbs" aria-label="Side by side">
        <div className="sbs-head">
          <span className="c-teal">MAIN · {hostOf(mission.mainUrl)} · {detail.log.mainStatus} · {fmtMs(detail.log.mainMs)}</span>
          <span className="c-blue">DEV · {hostOf(mission.devUrl)} · {detail.log.devStatus ?? "—"} · {fmtMs(detail.log.devMs)}</span>
        </div>
        {diff.loading && <div className="sbs-loading"><Kitty mood="idle" size="sm" /> Diffing in a background worker…</div>}
        {diff.error && <ErrorNote error={diff.error} />}
        {!diff.loading && !diff.error && rows.length === 0 && <Empty title="No body on either side" />}
        <div className="sbs-scroll" ref={scrollRef}>
          <div style={{ height: virt.getTotalSize(), position: "relative" }}>
            {virt.getVirtualItems().map((vi) => {
              const item = display[vi.index];
              if (item.t === "fold") {
                return (
                  <button key={`f${item.start}`} type="button" className="fold" style={{ transform: `translateY(${vi.start}px)`, height: LINE_H }}
                    onClick={() => setExpanded((s) => new Set(s).add(item.start))}>
                    ⋯ {fmtInt(item.end - item.start)} unchanged lines · expand
                  </button>
                );
              }
              const r: Row = rows[item.i];
              const hl = jumpHighlight(r, selected);
              const ignored = isIgnored(r);
              return (
                <div key={item.i} className={`sbs-row ${ignored ? "d-ignored" : KIND_CLASS[r.k]} ${hl ? "hl" : ""}`} style={{ transform: `translateY(${vi.start}px)`, height: LINE_H }}
                  title={ignored ? "Differs, but excluded from the comparison by the mission's rules" : undefined}>
                  <span className="ln">{r.ln || ""}</span><span className={`code ${r.l == null ? "blank" : ""}`}>{r.l ?? ""}</span>
                  <span className="ln">{r.rn || ""}</span><span className={`code ${r.r == null ? "blank" : ""}`}>{r.r ?? ""}</span>
                </div>
              );
            })}
          </div>
        </div>
        <div className="sbs-foot">
          {diff.result && <span>{fmtInt(diff.result.leftLines)} / {fmtInt(diff.result.rightLines)} lines · {fmtBytes(diff.bytes?.main)} / {fmtBytes(diff.bytes?.dev)} · fetched in {diff.ms?.fetch} ms, aligned in a Web Worker in {diff.ms?.diff} ms</span>}
          {diff.result && !diff.result.json && <span className="c-amber"> · not JSON: compared line by line</span>}
          {ignoredCount > 0 && <span> · <span className="ignored-key">dimmed</span> {fmtInt(ignoredCount)} lines differ but are excluded by rules</span>}
          {keys.length > 0 && <span> · arrays matched by key: {keys.map((k) => k.pattern).join(", ")}</span>}
        </div>
      </section>
    </div>
  );
}

const hostOf = (u: string) => { try { return new URL(u).host; } catch { return u; } };

function jumpHighlight(r: Row, d: Difference | null) {
  if (!d || r.k === K.Same) return false;
  return r.path === d.p || wildcardPath(r.path) === wildcardPath(d.p);
}

function HeadersTab({ detail }: { detail: LogDetail }) {
  const names = [...new Set([...Object.keys(detail.main.headers), ...Object.keys(detail.dev?.headers ?? {})].map((h) => h.toLowerCase()))].sort();
  const get = (h: Record<string, string> | undefined, n: string) => (h ? Object.entries(h).find(([k]) => k.toLowerCase() === n)?.[1] : undefined);
  return (
    <section className="panel table-panel">
      <table className="table">
        <thead><tr><th>Header</th><th>Main</th><th>Dev</th></tr></thead>
        <tbody>
          {names.map((n) => {
            const a = get(detail.main.headers, n), b = get(detail.dev?.headers, n);
            return (
              <tr key={n} className={a !== b ? "row-diff" : ""}>
                <td className="mono">{n}</td><td className="mono wrap">{a ?? <span className="dim">—</span>}</td><td className="mono wrap">{b ?? <span className="dim">—</span>}</td>
              </tr>
            );
          })}
        </tbody>
      </table>
      <p className="muted-text small pad">Only headers listed under the mission's “compared headers” count as differences. Sensitive headers are stored redacted.</p>
    </section>
  );
}

function RequestTab({ mission, detail }: { mission: Mission; detail: LogDetail }) {
  const { data: body } = useQuery({ queryKey: ["req-body", detail.request.bodyUrl], queryFn: () => fetch(detail.request.bodyUrl).then((r) => (r.ok ? r.text() : "")) });
  const { data: cfg } = useConfig();
  const pretty = useMemo(() => { try { return body ? JSON.stringify(JSON.parse(body), null, 2) : ""; } catch { return body ?? ""; } }, [body]);
  const log = detail.log;
  const url = `${cfg?.proxyPublicUrl ?? ""}${mission.proxyPath}${log.path}${log.query ? `?${log.query}` : ""}`;
  const headers = Object.entries(detail.request.headers).filter(([k, v]) => !["host", "content-length"].includes(k.toLowerCase()) && v !== "[redacted]");
  const curl = [`curl -X ${log.method} '${url}'`, ...headers.map(([k, v]) => `  -H '${k}: ${v.replace(/'/g, "'\\''")}'`), ...(body ? [`  --data-raw '${body.replace(/'/g, "'\\''")}'`] : [])].join(" \\\n");
  return (
    <div className="req-grid">
      <section className="panel table-panel">
        <div className="panel-title"><h2>Request headers</h2><CopyButton text={curl} label="Copy as curl" /></div>
        <table className="table"><tbody>
          {Object.entries(detail.request.headers).map(([k, v]) => <tr key={k}><td className="mono">{k}</td><td className="mono wrap">{v}</td></tr>)}
        </tbody></table>
      </section>
      <section className="panel">
        <div className="panel-title"><h2>Request body</h2><span className="dim small">{fmtBytes(detail.request.bodyBytes)}</span></div>
        <pre className="code-block">{pretty || <span className="dim">(no body)</span>}</pre>
      </section>
    </div>
  );
}

function ReplaysTab({ mission, logId }: { mission: Mission; logId: string }) {
  const { data, isLoading } = useLogs(mission.id, { replayOf: logId, sort: "newest" });
  const items = data?.pages.flatMap((p) => p.items) ?? [];
  return (
    <section className="panel table-panel">
      {isLoading ? <div className="skeleton" /> : items.length === 0 ? (
        <Empty title="Not replayed yet">Use “Replay &amp; compare” to send this request to Main and Dev again.</Empty>
      ) : (
        <table className="table">
          <thead><tr><th>When</th><th>Status</th><th>Latency</th><th>Result</th><th>Rules</th><th /></tr></thead>
          <tbody>
            {items.map((r) => (
              <tr key={r.id}>
                <td className="mono">{fmtTime(r.ts)}</td><td><StatusPair main={r.mainStatus} dev={r.devStatus} /></td>
                <td className="mono">{fmtMs(r.mainMs)} → {fmtMs(r.devMs)}</td><td><VerdictTag log={r} /></td><td className="mono">v{r.rulesVersion}</td>
                <td><Link to={`/missions/${mission.id}/logs/${r.id}`}>Open</Link></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
