import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useMissions } from "../api/queries";
import { Modal } from "./Bits";

interface Item { label: string; hint: string; to: string }

/** ⌘K / Ctrl+K: jump to a mission (or one of its pages), jobs or the fleet. */
export function CommandPalette() {
  const [open, setOpen] = useState(false);
  const [q, setQ] = useState("");
  const [sel, setSel] = useState(0);
  const nav = useNavigate();
  const { data: missions } = useMissions();

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        setOpen((o) => !o);
        setQ("");
        setSel(0);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const items = useMemo<Item[]>(() => {
    const all: Item[] = [
      { label: "Missions", hint: "page", to: "/" },
      { label: "Jobs", hint: "page", to: "/jobs" },
      { label: "Proxy fleet", hint: "page", to: "/fleet" },
    ];
    for (const m of missions ?? []) {
      all.push({ label: m.name, hint: "traffic", to: `/missions/${m.id}` });
      all.push({ label: `${m.name} · rules`, hint: "rules", to: `/missions/${m.id}/rules` });
      all.push({ label: `${m.name} · replay & fuzz`, hint: "jobs", to: `/missions/${m.id}/replay` });
      all.push({ label: `${m.name} · settings`, hint: "settings", to: `/missions/${m.id}/settings` });
    }
    const t = q.trim().toLowerCase();
    return (t ? all.filter((i) => i.label.toLowerCase().includes(t) || i.hint.includes(t)) : all).slice(0, 12);
  }, [missions, q]);

  if (!open) return null;
  const go = (i: Item | undefined) => {
    if (!i) return;
    setOpen(false);
    nav(i.to);
  };
  return (
    <Modal title="Jump to" onClose={() => setOpen(false)}>
      <div className="palette">
        <label className="field">
          <span className="sr-only">Search</span>
          <input
            data-autofocus
            className="input"
            placeholder="Mission, page…"
            value={q}
            onChange={(e) => { setQ(e.target.value); setSel(0); }}
            onKeyDown={(e) => {
              if (e.key === "ArrowDown") { e.preventDefault(); setSel((s) => Math.min(items.length - 1, s + 1)); }
              if (e.key === "ArrowUp") { e.preventDefault(); setSel((s) => Math.max(0, s - 1)); }
              if (e.key === "Enter") go(items[sel]);
            }}
          />
        </label>
        <ul className="palette-list" role="listbox" aria-label="Results">
          {items.map((i, n) => (
            <li key={i.to} role="option" aria-selected={n === sel}>
              <button type="button" className={`palette-item ${n === sel ? "sel" : ""}`} onClick={() => go(i)} onMouseEnter={() => setSel(n)}>
                <span>{i.label}</span><span className="muted-text">{i.hint}</span>
              </button>
            </li>
          ))}
        </ul>
      </div>
    </Modal>
  );
}
