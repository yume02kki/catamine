import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import type { DiffSummary, LogItem } from "../api/types";
import { copyText, fmtInt, verdictLabel } from "../lib/format";
import { IconClose, IconCopy } from "./Icons";

export function MethodPill({ method }: { method: string }) {
  return <span className={`method method-${method.toLowerCase()}`}>{method}</span>;
}

function statusClass(s: number | null) {
  if (s == null) return "st-none";
  if (s >= 500) return "st-5";
  if (s >= 400) return "st-4";
  if (s >= 300) return "st-3";
  return "st-2";
}

export function StatusPair({ main, dev }: { main: number; dev: number | null }) {
  return (
    <span className="status-pair">
      <span className={`status ${statusClass(main)}`}>{main || "—"}</span>
      <span className="arrow" aria-hidden="true">→</span>
      <span className={`status ${statusClass(dev)} ${dev != null && dev !== main ? "status-diff" : ""}`}>{dev ?? "—"}</span>
    </span>
  );
}

/** Composition of a row's differences: changed / added / removed / type. */
export function DiffBar({ summary }: { summary: DiffSummary | null }) {
  if (!summary) return <span className="diffbar diffbar-none" />;
  const parts = [
    ["changed", summary.changed],
    ["added", summary.added],
    ["removed", summary.removed],
    ["type", summary.typeChanged],
  ] as const;
  const total = parts.reduce((s, [, n]) => s + n, 0);
  if (total === 0) return <span className="diffbar diffbar-none" />;
  const label = parts.filter(([, n]) => n > 0).map(([k, n]) => `${fmtInt(n)} ${k}`).join(", ");
  return (
    <span className="diffbar" role="img" aria-label={label} title={label}>
      {parts.map(([k, n]) => (n > 0 ? <span key={k} className={`seg seg-${k}`} style={{ flexGrow: Math.max(n / total, 0.04) }} /> : null))}
    </span>
  );
}

export function VerdictTag({ log }: { log: Pick<LogItem, "verdict" | "diffCount"> }) {
  if (log.verdict === "mismatch") return <span className="vt vt-mismatch">{fmtInt(log.diffCount)}</span>;
  return <span className={`vt vt-${log.verdict}`}>{verdictLabel[log.verdict]}</span>;
}

export function Sparkline({ values, max, colors, label }: { values: number[]; max?: number; colors: string[]; label: string }) {
  const m = max ?? Math.max(1, ...values);
  return (
    <span className="spark" role="img" aria-label={label}>
      {values.map((v, i) => (
        <span key={i} style={{ height: `${Math.max(8, (v / m) * 100)}%`, background: colors[i] }} />
      ))}
    </span>
  );
}

export function CopyButton({ text, label = "Copy" }: { text: string; label?: string }) {
  const [done, setDone] = useState(false);
  return (
    <button type="button" className="btn btn-xs btn-ghost-light" onClick={async () => { setDone(await copyText(text)); setTimeout(() => setDone(false), 1200); }}>
      <IconCopy width={12} height={12} /> {done ? "Copied" : label}
    </button>
  );
}

export function Modal({ title, onClose, children, wide }: { title: string; onClose: () => void; children: ReactNode; wide?: boolean }) {
  const ref = useRef<HTMLDialogElement>(null);
  const id = useId();
  useEffect(() => {
    const d = ref.current;
    if (d && !d.open) {
      d.showModal();
      // showModal() focuses the first focusable element (the close button); move focus into the content instead.
      d.querySelector<HTMLElement>("[data-autofocus], .modal-body input:not([type=hidden]), .modal-body select, .modal-body textarea, .modal-body button")?.focus();
    }
    return () => d?.close();
  }, []);
  return (
    <dialog ref={ref} className={`modal ${wide ? "modal-wide" : ""}`} aria-labelledby={id} onCancel={(e) => { e.preventDefault(); onClose(); }}>
      <div className="modal-head">
        <h2 id={id}>{title}</h2>
        <button type="button" className="icon-btn" aria-label="Close" onClick={onClose}><IconClose /></button>
      </div>
      <div className="modal-body">{children}</div>
    </dialog>
  );
}

export function Confirm({ title, body, confirm, danger, onConfirm, onClose }: {
  title: string; body: ReactNode; confirm: string; danger?: boolean; onConfirm: () => void; onClose: () => void;
}) {
  return (
    <Modal title={title} onClose={onClose}>
      <div className="stack">
        <div className="muted-text">{body}</div>
        <div className="row-end">
          <button type="button" className="btn" onClick={onClose}>Cancel</button>
          <button type="button" className={`btn ${danger ? "btn-danger" : "btn-primary"}`} onClick={onConfirm}>{confirm}</button>
        </div>
      </div>
    </Modal>
  );
}

export function Empty({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="empty">
      <strong>{title}</strong>
      {children ? <div>{children}</div> : null}
    </div>
  );
}

export function ErrorNote({ error }: { error: unknown }) {
  if (!error) return null;
  return <div className="note note-error" role="alert">{error instanceof Error ? error.message : String(error)}</div>;
}

/** Debounced value. */
export function useDebounced<T>(value: T, ms: number): T {
  const [v, setV] = useState(value);
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms);
    return () => clearTimeout(t);
  }, [value, ms]);
  return v;
}
