import { useState, type FormEvent } from "react";
import { ApiError } from "../api/client";
import type { Mission, MissionUpsert } from "../api/types";
import { Confirm } from "./Bits";

const METHODS = ["GET", "HEAD", "OPTIONS", "POST", "PUT", "PATCH", "DELETE"];
const UNSAFE = ["POST", "PUT", "PATCH", "DELETE"];

interface Props {
  initial?: Mission;
  submitLabel: string;
  busy?: boolean;
  error?: unknown;
  onSubmit: (body: MissionUpsert) => void;
  onCancel?: () => void;
}

const slugify = (s: string) => s.toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 63);
const parseList = (s: string) => s.split(/[,\n]/).map((x) => x.trim()).filter(Boolean);
const parseSet = (s: string) =>
  Object.fromEntries(s.split("\n").map((l) => l.trim()).filter(Boolean).map((l) => {
    const i = l.indexOf(":");
    return i < 0 ? [l, ""] : [l.slice(0, i).trim(), l.slice(i + 1).trim()];
  }));

export function MissionForm({ initial, submitLabel, busy, error, onSubmit, onCancel }: Props) {
  const [name, setName] = useState(initial?.name ?? "");
  const [slug, setSlug] = useState(initial?.slug ?? "");
  const [slugTouched, setSlugTouched] = useState(!!initial);
  const [mainUrl, setMainUrl] = useState(initial?.mainUrl ?? "");
  const [devUrl, setDevUrl] = useState(initial?.devUrl ?? "");
  const [enabled, setEnabled] = useState(initial?.enabled ?? true);
  const [mirrorEnabled, setMirrorEnabled] = useState(initial?.mirrorEnabled ?? true);
  const [sample, setSample] = useState(Math.round((initial?.sampleRate ?? 1) * 100));
  const [methods, setMethods] = useState<string[]>(initial?.mirrorMethods ?? ["GET", "HEAD"]);
  const [ack, setAck] = useState(false);
  const [pendingUnsafe, setPendingUnsafe] = useState<string | null>(null);
  const [maxKb, setMaxKb] = useState(Math.round((initial?.maxMirrorBodyBytes ?? 262144) / 1024));
  const [timeout, setTimeoutMs] = useState(initial?.devTimeoutMs ?? 2000);
  const [concurrency, setConcurrency] = useState(initial?.devMaxConcurrency ?? 64);
  const [strip, setStrip] = useState((initial?.headerPolicy.strip ?? ["authorization", "cookie", "proxy-authorization"]).join(", "));
  const [setHeaders, setSetHeaders] = useState(Object.entries(initial?.headerPolicy.set ?? {}).map(([k, v]) => `${k}: ${v}`).join("\n"));
  const [compared, setCompared] = useState((initial?.comparedHeaders ?? ["content-type"]).join(", "));
  const [retention, setRetention] = useState(initial?.retentionDays ?? 14);
  const [maxMb, setMaxMb] = useState(initial?.maxStorageMb ?? 0);

  const fields = error instanceof ApiError ? error.fields ?? {} : {};
  const fieldErr = (k: string) => fields[k]?.[0];

  const toggle = (m: string) => {
    if (methods.includes(m)) setMethods(methods.filter((x) => x !== m));
    else if (UNSAFE.includes(m) && !(initial?.mirrorMethods.includes(m))) setPendingUnsafe(m);
    else setMethods([...methods, m]);
  };

  const submit = (e: FormEvent) => {
    e.preventDefault();
    onSubmit({
      name, slug, mainUrl, devUrl, enabled, mirrorEnabled,
      sampleRate: sample / 100,
      mirrorMethods: methods,
      acknowledgeUnsafeMethods: ack,
      maxMirrorBodyBytes: maxKb * 1024,
      devTimeoutMs: timeout,
      devMaxConcurrency: concurrency,
      headerPolicy: { strip: parseList(strip), set: parseSet(setHeaders) },
      comparedHeaders: parseList(compared),
      retentionDays: retention,
      maxStorageMb: maxMb,
    });
  };

  const unsafeOn = methods.filter((m) => UNSAFE.includes(m));

  return (
    <form className="form" onSubmit={submit}>
      <div className="form-grid">
        <label className="field">
          <span>Name</span>
          <input className="input" required value={name} onChange={(e) => { setName(e.target.value); if (!slugTouched) setSlug(slugify(e.target.value)); }} />
        </label>
        <label className="field">
          <span>Slug <em>proxy endpoint /m/{slug || "…"}/</em></span>
          <input className="input mono" required value={slug} pattern="[a-z0-9][a-z0-9\-]{0,62}" onChange={(e) => { setSlugTouched(true); setSlug(e.target.value); }} />
          {fieldErr("slug") && <small className="field-error">{fieldErr("slug")}</small>}
        </label>
        <label className="field span-2">
          <span><b className="tag tag-main">Main</b> production URL (source of truth)</span>
          <input className="input mono" required type="url" placeholder="https://api.prod.internal" value={mainUrl} onChange={(e) => setMainUrl(e.target.value)} />
          {fieldErr("mainUrl") && <small className="field-error">{fieldErr("mainUrl")}</small>}
        </label>
        <label className="field span-2">
          <span><b className="tag tag-dev">Dev</b> candidate URL</span>
          <input className="input mono" required type="url" placeholder="https://api-next.dev.internal" value={devUrl} onChange={(e) => setDevUrl(e.target.value)} />
          {fieldErr("devUrl") && <small className="field-error">{fieldErr("devUrl")}</small>}
        </label>
      </div>

      <fieldset className="fieldset">
        <legend>Mirroring</legend>
        <div className="form-grid">
          <label className="check"><input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} /> Proxy endpoint enabled</label>
          <label className="check"><input type="checkbox" checked={mirrorEnabled} onChange={(e) => setMirrorEnabled(e.target.checked)} /> Mirror to Dev</label>
          <label className="field span-2">
            <span>Sample rate <b className="mono">{sample}%</b> of eligible requests</span>
            <input type="range" min={0} max={100} value={sample} onChange={(e) => setSample(Number(e.target.value))} />
          </label>
          <div className="field span-2">
            <span>Methods mirrored</span>
            <div className="chips">
              {METHODS.map((m) => (
                <button key={m} type="button" aria-pressed={methods.includes(m)}
                  className={`chip mono ${methods.includes(m) ? (UNSAFE.includes(m) ? "chip-warn" : "chip-on") : ""}`} onClick={() => toggle(m)}>{m}</button>
              ))}
            </div>
            {unsafeOn.length > 0 && (
              <div className="note note-warn">Mirroring {unsafeOn.join(", ")} sends real write requests to Dev. Make sure Dev uses an isolated datastore and cannot call production services.</div>
            )}
            {fieldErr("mirrorMethods") && <small className="field-error">{fieldErr("mirrorMethods")}</small>}
          </div>
          <label className="field"><span>Max mirrored request body (KB)</span><input className="input" type="number" min={0} value={maxKb} onChange={(e) => setMaxKb(Number(e.target.value))} /></label>
          <label className="field"><span>Dev timeout (ms)</span><input className="input" type="number" min={10} value={timeout} onChange={(e) => setTimeoutMs(Number(e.target.value))} /></label>
          <label className="field"><span>Max concurrent Dev calls</span><input className="input" type="number" min={1} value={concurrency} onChange={(e) => setConcurrency(Number(e.target.value))} /></label>
        </div>
      </fieldset>

      <fieldset className="fieldset">
        <legend>Headers</legend>
        <div className="form-grid">
          <label className="field"><span>Strip before sending to Dev</span><input className="input mono" value={strip} onChange={(e) => setStrip(e.target.value)} /></label>
          <label className="field"><span>Compared response headers</span><input className="input mono" value={compared} onChange={(e) => setCompared(e.target.value)} /></label>
          <label className="field span-2"><span>Set / override on Dev requests (one <code>name: value</code> per line)</span><textarea className="input mono" rows={3} value={setHeaders} onChange={(e) => setSetHeaders(e.target.value)} placeholder="x-api-key: dev-key" /></label>
        </div>
      </fieldset>

      <fieldset className="fieldset">
        <legend>Storage</legend>
        <div className="form-grid">
          <label className="field"><span>Keep logs for (days)</span><input className="input" type="number" min={1} max={3650} value={retention} onChange={(e) => setRetention(Number(e.target.value))} /></label>
          <label className="field"><span>Size limit (MB, 0 = none)</span><input className="input" type="number" min={0} value={maxMb} onChange={(e) => setMaxMb(Number(e.target.value))} /></label>
        </div>
      </fieldset>

      {error && !Object.keys(fields).length ? <div className="note note-error">{(error as Error).message}</div> : null}
      <div className="row-end">
        {onCancel && <button type="button" className="btn" onClick={onCancel}>Cancel</button>}
        <button type="submit" className="btn btn-primary" disabled={busy}>{busy ? "Saving…" : submitLabel}</button>
      </div>

      {pendingUnsafe && (
        <Confirm
          title={`Mirror ${pendingUnsafe} requests?`}
          body={<>Every {pendingUnsafe} that reaches Main will also be <b>sent to Dev for real</b>. Only do this if Dev writes to its own isolated datastore and cannot reach production services.</>}
          confirm={`Yes, mirror ${pendingUnsafe}`}
          danger
          onConfirm={() => { setMethods([...methods, pendingUnsafe]); setAck(true); setPendingUnsafe(null); }}
          onClose={() => setPendingUnsafe(null)}
        />
      )}
    </form>
  );
}
