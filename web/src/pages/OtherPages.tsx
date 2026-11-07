import { useRef, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import { useQueryClient } from "@tanstack/react-query";
import { api } from "../api/client";
import { useDeleteMission, useMission, useProxies, useSaveMission } from "../api/queries";
import { Confirm, Empty, ErrorNote } from "../components/Bits";
import { JobList } from "../components/JobList";
import { Kitty } from "../components/Kitty";
import { MissionForm } from "../components/MissionForm";
import { MissionHeader, ProxyBar } from "../components/MissionHeader";
import { fmtAgo, fmtBytes, fmtInt, fmtMs } from "../lib/format";

export function SettingsPage() {
  const { id = "" } = useParams();
  const { data: mission } = useMission(id);
  const save = useSaveMission();
  const del = useDeleteMission();
  const nav = useNavigate();
  const [confirm, setConfirm] = useState(false);
  const [saved, setSaved] = useState(false);
  if (!mission) return <div className="page"><div className="skeleton" /></div>;
  return (
    <div className="page narrow">
      <MissionHeader mission={mission} title="Settings" back={`/missions/${id}`} />
      <ProxyBar mission={mission} />
      {mission.warnings.map((w) => <div key={w} className="note note-warn">{w}</div>)}
      {saved && <div className="note note-ok">Saved. Proxies pick up the change within a few seconds.</div>}
      <section className="card">
        <MissionForm key={mission.updatedAt} initial={mission} submitLabel="Save changes" busy={save.isPending} error={save.error}
          onSubmit={(body) => save.mutate({ id, body }, { onSuccess: () => setSaved(true) })} />
      </section>
      <section className="card danger-zone">
        <h2>Delete mission</h2>
        <p className="muted-text">The proxy endpoint stops working at once and the whole traffic log is dropped.</p>
        <button type="button" className="btn btn-danger" onClick={() => setConfirm(true)}>Delete {mission.name}</button>
      </section>
      {confirm && (
        <Confirm title={`Delete ${mission.name}?`} body="This cannot be undone." confirm="Delete mission" danger
          onConfirm={() => del.mutate(id, { onSuccess: () => nav("/") })} onClose={() => setConfirm(false)} />
      )}
    </div>
  );
}

export function JobsPage() {
  const fileRef = useRef<HTMLInputElement>(null);
  const [slug, setSlug] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<unknown>(null);
  const qc = useQueryClient();
  const upload = async (file: File) => {
    setBusy(true);
    setError(null);
    try {
      await api.importFile(file, slug || undefined);
      qc.invalidateQueries({ queryKey: ["jobs"] });
    } catch (e) {
      setError(e);
    } finally {
      setBusy(false);
      if (fileRef.current) fileRef.current.value = "";
    }
  };
  return (
    <div className="page">
      <header className="page-head"><h1>Jobs</h1><span className="muted-text">re-diffs, replays, fuzz runs, exports and imports, stored in Postgres; they survive restarts</span></header>
      <section className="card import-card">
        <h2>Import a mission</h2>
        <p className="muted-text small">Upload a <code>.ndjson.gz</code> export. The file streams to disk and is imported in the background as a new (disabled) mission.</p>
        <div className="row">
          <label className="field"><span>New slug (optional)</span><input className="input mono" value={slug} onChange={(e) => setSlug(e.target.value)} placeholder="orders-copy" /></label>
          <label className="btn btn-primary file-btn">{busy ? "Uploading…" : "Choose file…"}
            <input ref={fileRef} type="file" accept=".gz,.ndjson.gz,application/gzip" className="sr-only" disabled={busy} onChange={(e) => e.target.files?.[0] && upload(e.target.files[0])} />
          </label>
        </div>
        <ErrorNote error={error} />
      </section>
      <JobList />
    </div>
  );
}

export function FleetPage() {
  const { data: proxies, isLoading, error } = useProxies();
  return (
    <div className="page">
      <header className="page-head"><h1>Proxy fleet</h1><span className="muted-text">every proxy instance reports here every 10 s; none of this is on the request path</span></header>
      <ErrorNote error={error} />
      {isLoading && <div className="skeleton" />}
      {proxies && proxies.length === 0 && (
        <div className="welcome"><Kitty mood="sleepy" /><div><h2>No proxy heartbeat</h2><p className="muted-text">Start a proxy with the same Postgres connection string and it appears here within 10 seconds.</p></div></div>
      )}
      <div className="fleet">
        {proxies?.map((p) => {
          const m = p.metrics;
          const dropped = Object.values(m.dropped ?? {}).reduce((a, b) => a + b, 0);
          const logDropped = Object.values(m.logDropped ?? {}).reduce((a, b) => a + b, 0);
          const fill = m.queueCapacity ? (m.queueDepth / m.queueCapacity) * 100 : 0;
          return (
            <article key={p.instanceId} className="card proxy-card">
              <div className="panel-title"><span className="dot dot-ok" /><h2 className="mono">{p.instanceId}</h2><span className="muted-text small">seen {fmtAgo(p.lastSeen)} · up since {fmtAgo(p.startedAt)}</span></div>
              <div className="kv"><span>Mirror queue</span><span className="meter"><span style={{ width: `${Math.max(1, fill)}%` }} /></span><b className="mono">{fmtInt(m.queueDepth)} / {fmtInt(m.queueCapacity)}</b></div>
              <div className="proxy-grid">
                <div><span>Enqueued</span><b>{fmtInt(m.enqueued)}</b></div>
                <div><span>Dropped</span><b className={dropped ? "c-amber" : "c-teal"}>{fmtInt(dropped)}</b></div>
                <div><span>Logs written</span><b>{fmtInt(m.logWritten)}</b></div>
                <div><span>Logs dropped</span><b className={logDropped ? "c-amber" : "c-teal"}>{fmtInt(logDropped)}</b></div>
                <div><span>Main avg (mirrored)</span><b className="mono">{fmtMs(m.mainLatencyMs.mirrored.avgMs)}</b></div>
                <div><span>Main avg (not)</span><b className="mono">{fmtMs(m.mainLatencyMs.unmirrored.avgMs)}</b></div>
                <div><span>Main p99 (mirrored)</span><b className="mono">≤ {fmtMs(m.mainLatencyMs.mirrored.p99Ms)}</b></div>
                <div><span>Memory</span><b className="mono">{fmtBytes(m.workingSetBytes)}</b></div>
              </div>
              {Object.keys(m.dropped ?? {}).length > 0 && <div className="muted-text small">drops: {Object.entries(m.dropped).map(([k, v]) => `${k.replace("_", " ")} ${fmtInt(v)}`).join(" · ")}</div>}
              <div className="chips">{Object.entries(m.circuits ?? {}).map(([k, v]) => <span key={k} className={`chip static ${v === "closed" ? "chip-on" : "chip-warn"}`}>{k}: {v}</span>)}</div>
              <div className="muted-text small">config from {m.configSource ?? "—"} · {fmtAgo(m.configLoadedAt)}</div>
            </article>
          );
        })}
      </div>
    </div>
  );
}

export function NotFound() {
  return <div className="page"><Empty title="Nothing here"><Kitty mood="sleepy" size="sm" /></Empty></div>;
}
