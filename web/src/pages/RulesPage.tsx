import { useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { Link, useBlocker, useParams, useSearchParams } from "react-router-dom";
import { api } from "../api/client";
import { useMission, useRules, useRuleTypes, useSaveRules } from "../api/queries";
import type { Mission, Rule, RuleInput } from "../api/types";
import { Confirm, Empty, ErrorNote, useDebounced } from "../components/Bits";
import { IconDown, IconPlus, IconUp } from "../components/Icons";
import { Kitty } from "../components/Kitty";
import { MissionHeader } from "../components/MissionHeader";
import { fmtInt } from "../lib/format";
import { decodeDraft } from "../lib/suggest";

interface Draft extends RuleInput { key: string }

const TYPE_META: Record<string, { label: string; color: string }> = {
  PrimaryKey: { label: "PRIMARY KEY", color: "c-blue" },
  IgnoreField: { label: "IGNORE FIELD", color: "c-pink" },
  SearchAndReplace: { label: "SEARCH & REPLACE", color: "c-amber" },
  IgnoreOrder: { label: "IGNORE ORDER", color: "c-teal" },
  NumericTolerance: { label: "NUMERIC TOLERANCE", color: "c-teal" },
  IgnoreHeader: { label: "IGNORE HEADER", color: "c-blue" },
  TypeCoercion: { label: "TYPE COERCION", color: "c-lilac" },
};

let keySeq = 0;
const toDraft = (r: Rule | RuleInput): Draft => ({ key: `k${++keySeq}`, id: r.id, type: r.type, name: r.name ?? null, enabled: r.enabled, config: r.config });
const strip = (d: Draft[]): RuleInput[] => d.map(({ key: _k, ...r }) => r);

function target(r: RuleInput): string {
  const c = r.config as Record<string, unknown>;
  const list = (x: unknown) => (Array.isArray(x) ? x.join(" · ") : "");
  switch (r.type) {
    case "PrimaryKey": return `${c.path ?? "?"}  key: ${c.keys ? list(c.keys) : c.key ?? "?"}`;
    case "IgnoreField": return [list(c.paths), c.path].filter(Boolean).join(" · ") || "?";
    case "SearchAndReplace": return `${c.scope === "field" ? c.path : "whole body"}: ${c.regex ? "/" + c.search + "/" : `"${c.search}"`} → "${c.replace ?? ""}"`;
    case "IgnoreOrder": return String(c.path ?? "?");
    case "NumericTolerance": return `${c.path} ± ${c.tolerance}${c.mode === "relative" ? " (relative)" : ""}`;
    case "IgnoreHeader": return list(c.headers);
    case "TypeCoercion": return String(c.path || "everywhere");
    default: return JSON.stringify(c);
  }
}

export function RulesPage() {
  const { id = "" } = useParams();
  const { data: mission } = useMission(id);
  const { data: rules, error } = useRules(id);
  if (error) return <div className="page"><ErrorNote error={error} /></div>;
  if (!mission || !rules) return <div className="page"><div className="skeleton" /></div>;
  return <RulesEditor mission={mission} saved={rules} />;
}

function RulesEditor({ mission, saved }: { mission: Mission; saved: Rule[] }) {
  const [params, setParams] = useSearchParams();
  const [draft, setDraft] = useState<Draft[]>(() => saved.map(toDraft));
  const [sel, setSel] = useState<string | null>(null);
  const [asJson, setAsJson] = useState(false);
  const [rediff, setRediff] = useState(true);
  const [dragKey, setDragKey] = useState<string | null>(null);
  const { data: types } = useRuleTypes();
  const save = useSaveRules(mission.id);

  // A rule suggested elsewhere (diff view, causes panel) arrives as ?add=…
  useEffect(() => {
    const add = decodeDraft(params.get("add"));
    if (!add) return;
    const d = toDraft(add);
    setDraft((x) => [...x, d]);
    setSel(d.key);
    params.delete("add");
    setParams(params, { replace: true });
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const dirty = JSON.stringify(strip(draft)) !== JSON.stringify(saved.map((r) => ({ id: r.id, type: r.type, name: r.name ?? null, enabled: r.enabled, config: r.config })));
  const blocker = useBlocker(({ currentLocation, nextLocation }) => dirty && !save.isPending && currentLocation.pathname !== nextLocation.pathname);

  const debounced = useDebounced(JSON.stringify(strip(draft)), 600);
  const preview = useQuery({
    queryKey: ["rule-preview", mission.id, debounced],
    queryFn: ({ signal }) => api.previewRules(mission.id, JSON.parse(debounced), 200, signal),
    retry: false,
  });

  const update = (key: string, patch: Partial<RuleInput>) => setDraft((d) => d.map((r) => (r.key === key ? { ...r, ...patch } : r)));
  const move = (key: string, delta: number) => setDraft((d) => {
    const i = d.findIndex((r) => r.key === key);
    const j = i + delta;
    if (i < 0 || j < 0 || j >= d.length) return d;
    const copy = d.slice();
    [copy[i], copy[j]] = [copy[j], copy[i]];
    return copy;
  });
  const dropOn = (key: string) => {
    if (!dragKey || dragKey === key) return;
    setDraft((d) => {
      const from = d.findIndex((r) => r.key === dragKey);
      const to = d.findIndex((r) => r.key === key);
      const copy = d.slice();
      const [item] = copy.splice(from, 1);
      copy.splice(to, 0, item);
      return copy;
    });
  };
  const addType = (type: string) => {
    const t = types?.find((x) => x.type === type);
    const d = toDraft({ type, enabled: true, config: structuredClone(t?.example ?? {}) });
    setDraft((x) => [...x, d]);
    setSel(d.key);
  };
  const selected = draft.find((r) => r.key === sel) ?? null;
  const p = preview.data;

  return (
    <div className="page">
      <MissionHeader mission={mission} title={<>Rules <span className="pill pill-pink">v{mission.rulesVersion} · {saved.length} saved</span></>} back={`/missions/${mission.id}`}>
        {dirty && <span className="pill pill-warn">unsaved changes</span>}
        <button type="button" className="btn" onClick={() => setAsJson((x) => !x)}>{asJson ? "Edit as list" : "View as JSON"}</button>
        <label className="check"><input type="checkbox" checked={rediff} onChange={(e) => setRediff(e.target.checked)} /> re-diff existing logs</label>
        <button type="button" className="btn btn-primary" disabled={!dirty || save.isPending}
          onClick={() => save.mutate({ rules: strip(draft), rediff }, { onSuccess: (r) => { setDraft(r.rules.map(toDraft)); setSel(null); } })}>
          {save.isPending ? "Saving…" : rediff ? "Save & re-diff" : "Save"}
        </button>
      </MissionHeader>
      <p className="muted-text">Rules run top to bottom on both responses before they are compared. Changes apply to new traffic at once; tick “re-diff” to recompute existing logs in the background without re-sending anything.</p>
      <ErrorNote error={save.error} />
      {save.data?.job && <div className="note note-ok">Saved as v{save.data.rulesVersion}. Re-diff job started — <Link to={`/missions/${mission.id}/replay`}>follow progress</Link>.</div>}

      <div className="rules-layout">
        <section className="rules-list" aria-label="Rules">
          {asJson ? (
            <JsonEditor draft={draft} onApply={(rules) => { setDraft(rules.map(toDraft)); setAsJson(false); }} />
          ) : (
            <>
              {draft.length === 0 && (
                <div className="calm"><Kitty mood="idle" size="sm" /><p>No rules yet: Main and Dev are compared byte-for-byte. Add a rule below, or use the suggestions on the traffic and diff pages.</p></div>
              )}
              {draft.map((r, i) => {
                const meta = TYPE_META[r.type] ?? { label: r.type.toUpperCase(), color: "" };
                return (
                  <div key={r.key} className={`rule-row ${sel === r.key ? "selected" : ""} ${r.enabled ? "" : "disabled"}`}
                    draggable onDragStart={() => setDragKey(r.key)} onDragOver={(e) => e.preventDefault()} onDrop={() => dropOn(r.key)} onDragEnd={() => setDragKey(null)}>
                    <span className="drag" aria-hidden="true">⠿</span>
                    <span className="mono dim">{i + 1}</span>
                    <button type="button" className="rule-main" onClick={() => setSel(r.key)}>
                      <span className={`rule-type ${meta.color}`}>{meta.label}</span>
                      <span className="mono ellipsis">{target(r)}</span>
                    </button>
                    <span className="rule-tools">
                      <button type="button" className="icon-btn" aria-label="Move up" disabled={i === 0} onClick={() => move(r.key, -1)}><IconUp /></button>
                      <button type="button" className="icon-btn" aria-label="Move down" disabled={i === draft.length - 1} onClick={() => move(r.key, 1)}><IconDown /></button>
                      <label className="switch"><span className="sr-only">Enabled</span><input type="checkbox" checked={r.enabled} onChange={(e) => update(r.key, { enabled: e.target.checked })} /></label>
                    </span>
                  </div>
                );
              })}
              <div className="add-rule" aria-label="Add a rule">
                <span className="muted-text small">Add a rule</span>
                <div className="chips">
                  {(types ?? []).map((t) => (
                    <button key={t.type} type="button" className="chip" title={t.description} onClick={() => addType(t.type)}><IconPlus width={12} height={12} /> {TYPE_META[t.type]?.label.toLowerCase() ?? t.type}</button>
                  ))}
                </div>
              </div>
            </>
          )}
          <MirroringCard mission={mission} />
        </section>

        <aside className="panel rule-side" aria-label="Rule editor and preview">
          {selected ? (
            <RuleForm key={selected.key} rule={selected} description={types?.find((t) => t.type === selected.type)?.description}
              onChange={(c) => update(selected.key, { config: c })}
              onDelete={() => { setDraft((d) => d.filter((r) => r.key !== selected.key)); setSel(null); }}
              onDuplicate={() => { const d = toDraft({ ...selected, id: undefined }); setDraft((x) => [...x, d]); setSel(d.key); }} />
          ) : (
            <Empty title="Select a rule to edit it">Changes are previewed on recent traffic as you type.</Empty>
          )}
          <section className="preview" aria-label="Preview">
            <div className="panel-title"><h2>Preview</h2><span className="muted-text small">{preview.isFetching ? "computing…" : p ? `last ${fmtInt(p.sampled)} compared requests` : ""}</span></div>
            {preview.error ? <ErrorNote error={preview.error} /> : p && (
              <>
                <div className="preview-nums">
                  <div><b className="c-amber">{fmtInt(p.mismatchesBefore)}</b><span>differ with saved rules</span></div>
                  <div><b className="c-teal">{fmtInt(p.mismatchesAfter)}</b><span>differ with this draft</span></div>
                  <div><b>{p.nowMatching > 0 ? `−${fmtInt(p.nowMatching)}` : "0"}</b><span>newly matching</span></div>
                </div>
                {p.nowMismatching > 0 && <div className="note note-warn">{fmtInt(p.nowMismatching)} requests that matched before would differ with this draft.</div>}
                {p.ruleErrors.map((e, i) => <div key={i} className="note note-error">{e.rule}: {e.error}</div>)}
                {p.examples.length > 0 && (
                  <ul className="examples">
                    {p.examples.map((x) => (
                      <li key={x.logId}><Link to={`/missions/${mission.id}/logs/${x.logId}`} className="mono">{x.path}</Link> <span className="dim">{fmtInt(x.before)} → {fmtInt(x.after)} differences</span></li>
                    ))}
                  </ul>
                )}
                {p.sampled === 0 && <p className="muted-text small">No compared traffic yet: the preview fills in once requests are mirrored.</p>}
              </>
            )}
          </section>
        </aside>
      </div>

      {blocker.state === "blocked" && (
        <Confirm title="Leave without saving?" body="Your rule changes are not saved." confirm="Discard changes" danger
          onConfirm={() => blocker.proceed()} onClose={() => blocker.reset()} />
      )}
    </div>
  );
}

function MirroringCard({ mission }: { mission: Mission }) {
  const unsafe = mission.mirrorMethods.filter((m) => ["POST", "PUT", "PATCH", "DELETE"].includes(m));
  return (
    <section className="card mirroring" aria-label="Mirroring">
      <div className="panel-title"><h2>Mirroring</h2><Link to={`/missions/${mission.id}/settings`} className="small">Edit</Link></div>
      <div className="kv"><span>Methods</span><span className="chips">{["GET", "HEAD", "OPTIONS", "POST", "PUT", "PATCH", "DELETE"].map((m) => (
        <span key={m} className={`chip mono static ${mission.mirrorMethods.includes(m) ? (unsafe.includes(m) ? "chip-warn" : "chip-on") : "chip-off"}`}>{m}</span>
      ))}</span></div>
      {unsafe.length > 0 && <div className="note note-warn">{unsafe.join(", ")} mirrored: these requests are sent to Dev for real. Dev must use its own datastore.</div>}
      <div className="kv"><span>Sample</span><span className="meter"><span style={{ width: `${mission.sampleRate * 100}%` }} /></span><b className="mono">{Math.round(mission.sampleRate * 100)}%</b></div>
      <div className="kv"><span>Headers</span><span className="mono small">strip {mission.headerPolicy.strip.join(", ") || "—"}{Object.keys(mission.headerPolicy.set).length ? ` · set ${Object.keys(mission.headerPolicy.set).join(", ")}` : ""}</span></div>
      <div className="kv"><span>Compared</span><span className="mono small">{mission.comparedHeaders.join(", ") || "—"}</span></div>
    </section>
  );
}

function JsonEditor({ draft, onApply }: { draft: Draft[]; onApply: (r: RuleInput[]) => void }) {
  const [text, setText] = useState(() => JSON.stringify(strip(draft).map(({ id: _id, ...r }) => r), null, 2));
  const [err, setErr] = useState<string | null>(null);
  return (
    <div className="stack">
      <label className="sr-only" htmlFor="rules-json">Rules as JSON</label>
      <textarea id="rules-json" className="input mono json-edit" rows={22} value={text} onChange={(e) => setText(e.target.value)} spellCheck={false} />
      {err && <div className="note note-error">{err}</div>}
      <div className="row-end"><button type="button" className="btn btn-primary" onClick={() => {
        try {
          const parsed = JSON.parse(text);
          if (!Array.isArray(parsed)) throw new Error("expected an array of rules");
          onApply(parsed.map((r) => ({ type: String(r.type), enabled: r.enabled !== false, name: r.name ?? null, config: r.config ?? {} })));
        } catch (e) { setErr(String(e)); }
      }}>Apply</button></div>
    </div>
  );
}

function RuleForm({ rule, description, onChange, onDelete, onDuplicate }: {
  rule: Draft; description?: string; onChange: (c: Record<string, unknown>) => void; onDelete: () => void; onDuplicate: () => void;
}) {
  const c = rule.config as Record<string, unknown>;
  const set = (k: string, v: unknown) => {
    const next = { ...c, [k]: v };
    if (v === "" || v === undefined) delete next[k];
    onChange(next);
  };
  const debounced = useDebounced(JSON.stringify(c), 400);
  const validation = useQuery({ queryKey: ["validate", rule.type, debounced], queryFn: ({ signal }) => api.validateRule(rule.type, JSON.parse(debounced), signal) });
  const lines = (v: unknown) => (Array.isArray(v) ? v.join("\n") : "");
  const toList = (s: string) => s.split("\n").map((x) => x.trim()).filter(Boolean);
  const meta = TYPE_META[rule.type];

  const field = (key: string, label: string, opts: { mono?: boolean; placeholder?: string; type?: string } = {}) => (
    <label className="field">
      <span>{label}</span>
      <input className={`input ${opts.mono !== false ? "mono" : ""}`} type={opts.type ?? "text"} placeholder={opts.placeholder}
        value={c[key] == null ? "" : String(c[key])}
        onChange={(e) => set(key, opts.type === "number" ? (e.target.value === "" ? "" : Number(e.target.value)) : e.target.value)} />
    </label>
  );

  return (
    <div className="rule-form">
      <div className="panel-title"><h2 className={meta?.color}>{meta?.label ?? rule.type}</h2></div>
      {description && <p className="muted-text small">{description}</p>}
      {rule.type === "PrimaryKey" && (<>
        {field("path", "Array path", { placeholder: "$.items  or  $.orders[*].lines" })}
        {field("key", "Key field (relative to each item)", { placeholder: "id  or  meta.id" })}
        <label className="field"><span>…or composite key (one field per line)</span><textarea className="input mono" rows={2} value={lines(c.keys)} onChange={(e) => set("keys", toList(e.target.value).length ? toList(e.target.value) : undefined)} /></label>
      </>)}
      {rule.type === "IgnoreField" && (
        <label className="field"><span>Paths to ignore, one per line. Wildcards: <code>[*]</code> <code>.*</code> <code>..name</code></span>
          <textarea className="input mono" rows={5} value={lines(c.paths) || (c.path ? String(c.path) : "")}
            onChange={(e) => { const next: Record<string, unknown> = { ...c, paths: toList(e.target.value) }; delete next.path; onChange(next); }} /></label>
      )}
      {rule.type === "SearchAndReplace" && (<>
        <div className="seg-control" role="radiogroup" aria-label="Scope">
          {["body", "field"].map((s) => <button key={s} type="button" role="radio" aria-checked={(c.scope ?? "body") === s} className={(c.scope ?? "body") === s ? "on" : ""} onClick={() => set("scope", s)}>{s === "body" ? "Whole response" : "One field"}</button>)}
        </div>
        {c.scope === "field" && field("path", "Field path (and everything below it)", { placeholder: "$.items[*].url" })}
        {field("search", c.regex ? "Search (regex)" : "Search (plain text)")}
        {field("replace", "Replace with")}
        <label className="check"><input type="checkbox" checked={!!c.regex} onChange={(e) => set("regex", e.target.checked)} /> Regular expression</label>
        <label className="check"><input type="checkbox" checked={!!c.ignoreCase} onChange={(e) => set("ignoreCase", e.target.checked)} /> Ignore case</label>
      </>)}
      {rule.type === "IgnoreOrder" && field("path", "Array path compared as a set", { placeholder: "$.tags" })}
      {rule.type === "NumericTolerance" && (<>
        {field("path", "Number path", { placeholder: "$..price" })}
        {field("tolerance", c.mode === "relative" ? "Tolerance (fraction, 0.01 = 1%)" : "Tolerance (±)", { type: "number", mono: false })}
        <div className="seg-control" role="radiogroup" aria-label="Mode">
          {["absolute", "relative"].map((m) => <button key={m} type="button" role="radio" aria-checked={(c.mode ?? "absolute") === m} className={(c.mode ?? "absolute") === m ? "on" : ""} onClick={() => set("mode", m)}>{m}</button>)}
        </div>
      </>)}
      {rule.type === "IgnoreHeader" && (
        <label className="field"><span>Headers, one per line</span><textarea className="input mono" rows={4} value={lines(c.headers)} onChange={(e) => set("headers", toList(e.target.value))} /></label>
      )}
      {rule.type === "TypeCoercion" && field("path", "Path (empty = everywhere)", { placeholder: "$.total" })}
      {!meta && (
        <label className="field"><span>Config (JSON)</span><textarea className="input mono" rows={8} defaultValue={JSON.stringify(c, null, 2)}
          onBlur={(e) => { try { onChange(JSON.parse(e.target.value)); } catch { /* keep */ } }} /></label>
      )}
      {validation.data && !validation.data.valid && validation.data.errors.map((e, i) => <div key={i} className="note note-error">{e}</div>)}
      {validation.data?.valid && <div className="small c-teal">✓ valid</div>}
      <div className="row-end">
        <button type="button" className="btn" onClick={onDuplicate}>Duplicate</button>
        <button type="button" className="btn btn-danger-soft" onClick={onDelete}>Delete</button>
      </div>
    </div>
  );
}

