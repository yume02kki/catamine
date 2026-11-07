import { useMemo, useState } from "react";
import { Link, useNavigate } from "react-router-dom";
import { useDeleteMission, useMissions, useOverview, useProxies, useSaveMission } from "../api/queries";
import type { Mission, MissionOverview } from "../api/types";
import { Confirm, Empty, ErrorNote, Modal, Sparkline } from "../components/Bits";
import { IconEdit, IconPlus, IconSearch, IconTrash } from "../components/Icons";
import { Kitty } from "../components/Kitty";
import { MissionForm } from "../components/MissionForm";
import { fmtAgo, fmtCompact, fmtInt, fmtMs, fmtPct } from "../lib/format";

type Filter = "all" | "attention" | "writes" | "paused";
const UNSAFE = ["POST", "PUT", "PATCH", "DELETE"];

const needsAttention = (o?: MissionOverview) => !!o && ((o.diffRate ?? 0) > 0.2 || o.devError + o.devTimeout > 0);

export function MissionsPage() {
  const { data: missions, isLoading, error } = useMissions();
  const { data: overview } = useOverview(24);
  const { data: proxies } = useProxies();
  const [q, setQ] = useState("");
  const [filter, setFilter] = useState<Filter>("all");
  const [creating, setCreating] = useState(false);
  const [deleting, setDeleting] = useState<Mission | null>(null);
  const save = useSaveMission();
  const del = useDeleteMission();
  const nav = useNavigate();

  const byId = useMemo(() => new Map((overview ?? []).map((o) => [o.missionId, o])), [overview]);
  const list = (missions ?? []).filter((m) => {
    const t = q.trim().toLowerCase();
    if (t && ![m.name, m.slug, m.mainUrl, m.devUrl].some((s) => s.toLowerCase().includes(t))) return false;
    if (filter === "attention") return needsAttention(byId.get(m.id));
    if (filter === "writes") return m.mirrorMethods.some((x) => UNSAFE.includes(x));
    if (filter === "paused") return !m.enabled || !m.mirrorEnabled || m.sampleRate === 0;
    return true;
  });

  const totals = (overview ?? []).reduce((a, o) => ({ total: a.total + o.total, errors: a.errors + o.devError + o.devTimeout }), { total: 0, errors: 0 });
  const dropped = (proxies ?? []).reduce((s, p) => s + Object.values(p.metrics.dropped ?? {}).reduce((x, y) => x + y, 0), 0);
  const queue = (proxies ?? []).reduce((s, p) => s + p.metrics.queueDepth, 0);
  const capacity = (proxies ?? []).reduce((s, p) => s + p.metrics.queueCapacity, 0);
  const overhead = (proxies ?? []).filter((p) => p.metrics.mainLatencyMs.mirrored.count > 0 && p.metrics.mainLatencyMs.unmirrored.count > 0)
    .map((p) => p.metrics.mainLatencyMs.mirrored.avgMs - p.metrics.mainLatencyMs.unmirrored.avgMs);
  const active = (missions ?? []).filter((m) => m.enabled && m.mirrorEnabled && m.sampleRate > 0).length;

  const counts: Record<Filter, number> = {
    all: missions?.length ?? 0,
    attention: (missions ?? []).filter((m) => needsAttention(byId.get(m.id))).length,
    writes: (missions ?? []).filter((m) => m.mirrorMethods.some((x) => UNSAFE.includes(x))).length,
    paused: (missions ?? []).filter((m) => !m.enabled || !m.mirrorEnabled || m.sampleRate === 0).length,
  };

  return (
    <div className="page">
      <header className="page-head">
        <h1>Missions</h1>
        <span className="pill pill-pink">{active} active</span>
        <label className="search">
          <IconSearch />
          <span className="sr-only">Search missions</span>
          <input type="search" placeholder="Search missions, hosts…" value={q} onChange={(e) => setQ(e.target.value)} />
          <kbd>⌘K</kbd>
        </label>
        <button type="button" className="btn btn-primary" onClick={() => setCreating(true)}><IconPlus /> New mission</button>
      </header>

      <section className="kpis" aria-label="Fleet">
        <div className="kpi"><span className="kpi-label">Mirrored (24 h)</span><span className="kpi-value">{fmtCompact(totals.total)}</span><span className="kpi-sub">{fmtInt(totals.total / 1440)} / min on average</span></div>
        <div className="kpi"><span className="kpi-label">Mirroring cost on main path</span><span className="kpi-value ok">{overhead.length ? `+${fmtMs(Math.max(0, ...overhead))}` : "—"}</span><span className="kpi-sub">avg, mirrored vs not</span></div>
        <div className="kpi"><span className="kpi-label">Dropped mirror jobs</span><span className={`kpi-value ${dropped > 0 ? "warn" : "ok"}`}>{fmtInt(dropped)}</span><span className="kpi-sub">queue {fmtInt(queue)} / {fmtCompact(capacity)}</span></div>
        <div className="kpi"><span className="kpi-label">Dev errors (24 h)</span><span className={`kpi-value ${totals.errors > 0 ? "bad" : "ok"}`}>{fmtInt(totals.errors)}</span><span className="kpi-sub">{proxies?.length ?? 0} proxies reporting</span></div>
      </section>

      <div className="chips" role="group" aria-label="Filter">
        {(["all", "attention", "writes", "paused"] as Filter[]).map((f) => (
          <button key={f} type="button" aria-pressed={filter === f} className={`chip ${filter === f ? "chip-active" : ""}`} onClick={() => setFilter(f)}>
            {{ all: "All", attention: "Needs attention", writes: "Mirroring writes", paused: "Paused" }[f]} · {counts[f]}
          </button>
        ))}
      </div>

      <ErrorNote error={error} />
      {isLoading && <div className="skeleton-list">{[0, 1, 2].map((i) => <div key={i} className="skeleton" />)}</div>}
      {!isLoading && missions?.length === 0 && (
        <div className="welcome">
          <Kitty mood="happy" />
          <div>
            <h2>No missions yet</h2>
            <p className="muted-text">A mission puts a proxy in front of your production API, mirrors traffic to a candidate and shows every difference.</p>
            <button type="button" className="btn btn-primary" onClick={() => setCreating(true)}><IconPlus /> Create the first mission</button>
          </div>
        </div>
      )}
      {!isLoading && missions && missions.length > 0 && list.length === 0 && <Empty title="No mission matches" />}

      <section className="mission-list" aria-label="Missions">
        {list.map((m) => {
          const o = byId.get(m.id);
          const paused = !m.enabled || !m.mirrorEnabled || m.sampleRate === 0;
          const writes = m.mirrorMethods.filter((x) => UNSAFE.includes(x));
          const rate = o?.diffRate ?? null;
          const values = (o?.buckets ?? Array.from({ length: 24 }, () => ({ total: 0, mismatch: 0, errors: 0 })))
            .map((b) => (b.total ? (b.mismatch + b.errors) / b.total : 0));
          return (
            <article key={m.id} className={`mission-card ${needsAttention(o) ? "attention" : ""}`}>
              <Link to={`/missions/${m.id}`} className="mission-main">
                <div className="mission-title">
                  <span className={`dot ${paused ? "dot-off" : "dot-ok"}`} />
                  <b>{m.name}</b>
                  <span className="muted-text small">{paused ? "paused" : `created ${fmtAgo(m.createdAt)}`}</span>
                </div>
                <div className="url-line"><span className="tag tag-main">Main</span><span className="mono">{m.mainUrl}</span></div>
                <div className="url-line"><span className="tag tag-dev">Dev</span><span className="mono">{m.devUrl}</span></div>
              </Link>
              <Sparkline values={values.map((v) => v * 100)} max={100} colors={values.map((v) => (v > 0.6 ? "var(--amber)" : v > 0.15 ? "var(--lilac)" : "var(--teal)"))} label="Share of requests that differ, last 24 hours" />
              <div className="mission-rate">
                <span className={`big ${rate == null ? "" : rate > 0.6 ? "warn" : rate > 0.15 ? "lilac" : "ok"}`}>{paused && rate == null ? "—" : fmtPct(rate)}</span>
                <span className="muted-text small">differ · {fmtCompact(o?.total ?? 0)} req / 24 h</span>
              </div>
              <div className="mission-tags">
                <span className="pill pill-pink">{m.ruleCount} {m.ruleCount === 1 ? "rule" : "rules"}</span>
                <span className="pill">{m.mirrorMethods.join(",") || "—"} · {Math.round(m.sampleRate * 100)}%</span>
                {writes.length > 0 && <span className="pill pill-warn">mirrors writes</span>}
                {o && o.devError + o.devTimeout > 0 && <span className="pill pill-bad">{fmtInt(o.devError + o.devTimeout)} dev errors</span>}
              </div>
              <div className="mission-actions">
                <button type="button" className="icon-btn" aria-label={`Edit ${m.name}`} onClick={() => nav(`/missions/${m.id}/settings`)}><IconEdit /></button>
                <button type="button" className="icon-btn" aria-label={`Delete ${m.name}`} onClick={() => setDeleting(m)}><IconTrash /></button>
              </div>
            </article>
          );
        })}
      </section>

      {creating && (
        <Modal title="New mission" wide onClose={() => setCreating(false)}>
          <MissionForm
            submitLabel="Create mission"
            busy={save.isPending}
            error={save.error}
            onCancel={() => setCreating(false)}
            onSubmit={(body) => save.mutate({ body }, { onSuccess: (m) => { setCreating(false); nav(`/missions/${m.id}`); } })}
          />
        </Modal>
      )}
      {deleting && (
        <Confirm
          title={`Delete ${deleting.name}?`}
          body={<>The proxy endpoint <code>/m/{deleting.slug}/</code> stops working and the whole traffic log is dropped. This cannot be undone.</>}
          confirm="Delete mission"
          danger
          onConfirm={() => { del.mutate(deleting.id); setDeleting(null); }}
          onClose={() => setDeleting(null)}
        />
      )}
    </div>
  );
}
