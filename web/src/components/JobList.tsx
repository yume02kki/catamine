import { useState } from "react";
import { Link } from "react-router-dom";
import { downloadUrl } from "../api/client";
import { useCancelJob, useJobs, useMissions } from "../api/queries";
import type { Job } from "../api/types";
import { fmtAgo, fmtInt } from "../lib/format";
import { Empty } from "./Bits";

const TYPE_LABEL: Record<Job["type"], string> = { rediff: "Re-diff", replay: "Replay", fuzz: "Fuzz", export: "Export", import: "Import" };

/** Jobs with live progress (polls fast while any job is running). */
export function JobList({ missionId }: { missionId?: string }) {
  const { data, isLoading } = useJobs(missionId);
  const { data: missions } = useMissions();
  const cancel = useCancelJob();
  const [open, setOpen] = useState<string | null>(null);
  const name = (id: string | null) => missions?.find((m) => m.id === id)?.name;

  if (isLoading) return <div className="skeleton" />;
  if (!data || data.items.length === 0) return <Empty title="No jobs yet">Re-diffs, replays, fuzz runs, exports and imports appear here with their progress.</Empty>;

  return (
    <div className="jobs">
      {data.items.map((j) => {
        const pct = j.total ? Math.min(100, (j.done / j.total) * 100) : j.status === "succeeded" ? 100 : null;
        const active = j.status === "running" || j.status === "queued";
        return (
          <article key={j.id} className={`job job-${j.status}`}>
            <div className="job-head">
              <b>{TYPE_LABEL[j.type]}</b>
              {!missionId && j.missionId && <Link to={`/missions/${j.missionId}`} className="small">{name(j.missionId) ?? "mission"}</Link>}
              <span className={`pill job-status-${j.status}`}>{j.cancelRequested && active ? "cancelling" : j.status}</span>
              <span className="muted-text small">{fmtAgo(j.createdAt)}</span>
              <span className="grow" />
              {active && <button type="button" className="btn btn-sm" onClick={() => cancel.mutate(j.id)} disabled={j.cancelRequested}>Cancel</button>}
              {j.type === "export" && j.status === "succeeded" && <a className="btn btn-sm btn-primary" href={downloadUrl(j.id)}>Download</a>}
              {j.type === "import" && j.status === "succeeded" && typeof j.result?.missionId === "string" && <Link className="btn btn-sm btn-primary" to={`/missions/${j.result.missionId}`}>Open mission</Link>}
              {(j.type === "replay" || j.type === "fuzz") && j.missionId && <Link className="btn btn-sm" to={`/missions/${j.missionId}?range=all&q=${encodeURIComponent(`job:${j.id}`)}`}>View results</Link>}
              <button type="button" className="btn btn-sm btn-ghost" aria-expanded={open === j.id} onClick={() => setOpen(open === j.id ? null : j.id)}>Details</button>
            </div>
            <div className="progress" role="progressbar" aria-valuemin={0} aria-valuemax={100} aria-valuenow={pct ?? undefined} aria-label="Progress">
              <span className={pct == null && active ? "indeterminate" : ""} style={{ width: pct == null ? (active ? "30%" : "0%") : `${pct}%` }} />
            </div>
            <div className="muted-text small">{fmtInt(j.done)}{j.total != null ? ` / ${fmtInt(j.total)}` : ""} processed{j.error && j.status !== "cancelled" ? ` · ${j.error}` : ""}</div>
            {open === j.id && (
              <div className="job-details">
                {j.result && <ResultSummary job={j} />}
                <pre className="code-block small">{JSON.stringify({ params: j.params, result: j.result }, null, 2)}</pre>
              </div>
            )}
          </article>
        );
      })}
    </div>
  );
}

function ResultSummary({ job }: { job: Job }) {
  const r = job.result as Record<string, unknown>;
  const map = (k: string) => (r[k] && typeof r[k] === "object" ? Object.entries(r[k] as Record<string, number>) : []);
  const blocks = [["verdicts", "Verdicts"], ["changes", "Changed verdicts"], ["transitions", "Compared with the original"], ["mismatchesByMutation", "Mismatches by mutation"]] as const;
  return (
    <div className="result-grid">
      {blocks.map(([k, label]) => map(k).length > 0 && (
        <div key={k}><span className="muted-text small">{label}</span>
          <ul className="kv-list">{map(k).map(([a, b]) => <li key={a}><span className="mono">{a.replace(/_/g, " ")}</span><b>{fmtInt(b)}</b></li>)}</ul>
        </div>
      ))}
    </div>
  );
}
