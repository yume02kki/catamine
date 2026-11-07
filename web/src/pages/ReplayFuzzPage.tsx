import { useState, type FormEvent } from "react";
import { useParams } from "react-router-dom";
import { exportUrl } from "../api/client";
import { useMission, useStartJob } from "../api/queries";
import type { Mission } from "../api/types";
import { ErrorNote } from "../components/Bits";
import { JobList } from "../components/JobList";
import { MissionHeader } from "../components/MissionHeader";

const MUTATIONS = ["null", "empty", "huge_string", "boundary_number", "wrong_type", "missing", "extra_field", "empty_array", "special_chars"];
const UNSAFE = ["POST", "PUT", "PATCH", "DELETE"];

export function ReplayFuzzPage() {
  const { id = "" } = useParams();
  const { data: mission } = useMission(id);
  if (!mission) return <div className="page"><div className="skeleton" /></div>;
  return (
    <div className="page">
      <MissionHeader mission={mission} title="Replay & fuzz" back={`/missions/${id}`} />
      <div className="cards-3">
        <ReplayCard mission={mission} />
        <FuzzCard mission={mission} />
        <ExportCard mission={mission} />
      </div>
      <h2 className="section-title">Jobs for this mission</h2>
      <JobList missionId={id} />
    </div>
  );
}

function ReplayCard({ mission }: { mission: Mission }) {
  const start = useStartJob(mission.id);
  const [verdict, setVerdict] = useState("mismatch");
  const [path, setPath] = useState("");
  const [rate, setRate] = useState(10);
  const [limit, setLimit] = useState(1000);
  const [unsafe, setUnsafe] = useState(false);
  const submit = (e: FormEvent) => {
    e.preventDefault();
    start.mutate({ type: "replay", body: { filter: { verdict: verdict || undefined, path: path || undefined, source: "live" }, ratePerSecond: rate, limit, allowUnsafeMethods: unsafe } });
  };
  return (
    <form className="card stack" onSubmit={submit}>
      <h2>Replay logged traffic</h2>
      <p className="muted-text small">Re-sends logged requests to <b>both</b> Main and Dev and compares each result with the original verdict. Runs in the background, rate-limited, cancellable.</p>
      <label className="field"><span>Which requests</span>
        <select className="select" value={verdict} onChange={(e) => setVerdict(e.target.value)}>
          <option value="mismatch">Those that differed</option><option value="dev_error,dev_timeout">Dev errors and timeouts</option><option value="">Everything</option>
        </select></label>
      <label className="field"><span>Path prefix (optional)</span><input className="input mono" placeholder="/v1/search" value={path} onChange={(e) => setPath(e.target.value)} /></label>
      <div className="form-grid">
        <label className="field"><span>Rate (req/s)</span><input className="input" type="number" min={0.1} step={0.1} value={rate} onChange={(e) => setRate(Number(e.target.value))} /></label>
        <label className="field"><span>At most</span><input className="input" type="number" min={1} value={limit} onChange={(e) => setLimit(Number(e.target.value))} /></label>
      </div>
      <label className="check"><input type="checkbox" checked={unsafe} onChange={(e) => setUnsafe(e.target.checked)} /> Include write methods (POST/PUT/PATCH/DELETE)</label>
      {unsafe && <div className="note note-warn">Write requests are re-sent to <b>Main (production)</b> as well as Dev.</div>}
      <ErrorNote error={start.error} />
      <button type="submit" className="btn btn-primary" disabled={start.isPending}>Start replay</button>
    </form>
  );
}

function FuzzCard({ mission }: { mission: Mission }) {
  const start = useStartJob(mission.id);
  const [seedLogId, setSeedLogId] = useState("");
  const [method, setMethod] = useState("GET");
  const [path, setPath] = useState("/");
  const [query, setQuery] = useState("");
  const [body, setBody] = useState("");
  const [volume, setVolume] = useState(500);
  const [rate, setRate] = useState(20);
  const [mutations, setMutations] = useState<string[]>([]);
  const [unsafe, setUnsafe] = useState(false);
  const isUnsafe = !seedLogId && UNSAFE.includes(method);
  const submit = (e: FormEvent) => {
    e.preventDefault();
    start.mutate({
      type: "fuzz",
      body: {
        seedLogId: seedLogId || undefined,
        sample: seedLogId ? undefined : { method, path, query: query || undefined, body: body || undefined, headers: body ? { "content-type": "application/json" } : undefined },
        volume, ratePerSecond: rate, mutations: mutations.length ? mutations : undefined, hugeStringLength: 65536, allowUnsafeMethods: unsafe,
      },
    });
  };
  return (
    <form className="card stack" onSubmit={submit}>
      <h2>Fuzz</h2>
      <p className="muted-text small">Varies every field and query parameter (nulls, wrong types, boundary numbers, huge or empty strings, missing and extra fields) and sends each variant to both sides.</p>
      <label className="field"><span>Seed from a logged request id (or describe one below)</span><input className="input mono" placeholder="01a1b680-32b0-…" value={seedLogId} onChange={(e) => setSeedLogId(e.target.value.trim())} /></label>
      {!seedLogId && (
        <>
          <div className="form-grid">
            <label className="field"><span>Method</span><select className="select" value={method} onChange={(e) => setMethod(e.target.value)}>{["GET", "POST", "PUT", "PATCH", "DELETE"].map((m) => <option key={m}>{m}</option>)}</select></label>
            <label className="field"><span>Path</span><input className="input mono" value={path} onChange={(e) => setPath(e.target.value)} /></label>
          </div>
          <label className="field"><span>Query</span><input className="input mono" placeholder="q=lamp&page=2" value={query} onChange={(e) => setQuery(e.target.value)} /></label>
          <label className="field"><span>JSON body</span><textarea className="input mono" rows={4} placeholder='{"customer": {"id": 42}}' value={body} onChange={(e) => setBody(e.target.value)} /></label>
        </>
      )}
      <div className="form-grid">
        <label className="field"><span>Variants</span><input className="input" type="number" min={1} value={volume} onChange={(e) => setVolume(Number(e.target.value))} /></label>
        <label className="field"><span>Rate (req/s)</span><input className="input" type="number" min={0.1} step={0.1} value={rate} onChange={(e) => setRate(Number(e.target.value))} /></label>
      </div>
      <div className="field"><span>Mutations (none selected = all)</span>
        <div className="chips">{MUTATIONS.map((m) => (
          <button key={m} type="button" aria-pressed={mutations.includes(m)} className={`chip mono ${mutations.includes(m) ? "chip-on" : ""}`}
            onClick={() => setMutations(mutations.includes(m) ? mutations.filter((x) => x !== m) : [...mutations, m])}>{m.replace("_", " ")}</button>
        ))}</div></div>
      {(isUnsafe || seedLogId) && <label className="check"><input type="checkbox" checked={unsafe} onChange={(e) => setUnsafe(e.target.checked)} /> I understand write variants hit Main and Dev</label>}
      {isUnsafe && <div className="note note-warn">{method} fuzzing sends many synthetic writes to <b>Main (production)</b> and Dev.</div>}
      <ErrorNote error={start.error} />
      <button type="submit" className="btn btn-primary" disabled={start.isPending || (isUnsafe && !unsafe)}>Start fuzzing</button>
    </form>
  );
}

function ExportCard({ mission }: { mission: Mission }) {
  const start = useStartJob(mission.id);
  return (
    <div className="card stack">
      <h2>Export</h2>
      <p className="muted-text small">The mission's config, rules and full traffic log as gzip-compressed NDJSON. Streams, so size doesn't matter. Import it on the Jobs page of any KittyParty.</p>
      <a className="btn" href={exportUrl(mission.id)} download>Download now (streamed)</a>
      <button type="button" className="btn btn-primary" disabled={start.isPending} onClick={() => start.mutate({ type: "export" })}>Export as a background job</button>
      <ErrorNote error={start.error} />
      <h2 className="mt">Re-diff</h2>
      <p className="muted-text small">Recompute every stored diff with the current rules (v{mission.rulesVersion}) without re-sending requests.</p>
      <button type="button" className="btn" disabled={start.isPending} onClick={() => start.mutate({ type: "rediff", body: {} })}>Re-diff all logs</button>
    </div>
  );
}
