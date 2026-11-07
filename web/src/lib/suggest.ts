import type { DiffKind, Difference, RuleInput } from "../api/types";

/** `$.items[sku=A1].price` / `$.items[3].price` → `$.items[*].price` (rule patterns use wildcards). */
export function wildcardPath(path: string): string {
  return path.replace(/\[(?:\d+|[^\]'"]+=[^\]]*)\]/g, "[*]");
}

/** Path of the enclosing array for a keyed/indexed element path, if any. */
export function arrayPathOf(path: string): string | null {
  const m = /^(.*)\[(?:\d+|\*|[^\]]+=[^\]]*)\][^[]*$/.exec(path);
  return m ? wildcardPath(m[1]) : null;
}

export interface Suggestion {
  label: string;
  rule: RuleInput;
}

/** One-click rule suggestion for a difference. */
export function suggestRule(d: Pick<Difference, "k" | "p" | "m" | "d">): Suggestion | null {
  const path = wildcardPath(d.p);
  // A whole array element added or removed: ignoring "$.items[*]" would hide every item, so offer nothing.
  if ((d.k === "added" || d.k === "removed") && /\]$/.test(d.p)) return null;
  switch (d.k as DiffKind) {
    case "header": {
      const name = d.p.replace(/^header:/, "");
      return { label: `Ignore header ${name}`, rule: { type: "IgnoreHeader", enabled: true, config: { headers: [name] } } };
    }
    case "type_changed":
      return { label: "Allow type coercion", rule: { type: "TypeCoercion", enabled: true, config: { path } } };
    case "order":
      return { label: "Compare as a set", rule: { type: "IgnoreOrder", enabled: true, config: { path } } };
    case "changed": {
      const a = Number(d.m), b = Number(d.d);
      if (d.m != null && d.d != null && !Number.isNaN(a) && !Number.isNaN(b)) {
        // Slightly above the observed gap, rounded to 2 significant digits.
        const nice = Number((Math.max(Math.abs(a - b), 1e-9) * 1.05).toPrecision(2));
        return { label: `Tolerance ± ${nice}`, rule: { type: "NumericTolerance", enabled: true, config: { path, tolerance: nice, mode: "absolute" } } };
      }
      return { label: "Ignore this field", rule: { type: "IgnoreField", enabled: true, config: { paths: [path] } } };
    }
    case "array_length": {
      return { label: "Match items by key…", rule: { type: "PrimaryKey", enabled: true, config: { path, key: "id" } } };
    }
    case "status":
    case "body":
      return null;
    default:
      return { label: "Ignore this field", rule: { type: "IgnoreField", enabled: true, config: { paths: [path] } } };
  }
}

/** Human sentence for a cause (cluster example). */
export function describe(d: Difference): string {
  const v = (s: string | null) => (s == null ? "nothing" : s.length > 40 ? s.slice(0, 40) + "…" : s);
  switch (d.k) {
    case "added": return `Dev returns a field Main never sends: ${v(d.d)}`;
    case "removed": return `Main returns it, Dev does not: ${v(d.m)}`;
    case "type_changed": return `Type differs: ${v(d.m)} on Main, ${v(d.d)} on Dev.`;
    case "changed": return `Value differs: ${v(d.m)} → ${v(d.d)}`;
    case "array_length": return `Item count differs: ${d.m} on Main, ${d.d} on Dev.`;
    case "order": return `Same items in a different order (${v(d.m)}).`;
    case "header": return `Header differs: ${v(d.m)} → ${v(d.d)}`;
    case "status": return `Status ${d.m} on Main, ${d.d} on Dev.`;
    case "body": return "Bodies differ and at least one is not JSON.";
    default: return "";
  }
}

export const encodeDraft = (r: RuleInput) => btoa(unescape(encodeURIComponent(JSON.stringify(r))));
export function decodeDraft(s: string | null): RuleInput | null {
  if (!s) return null;
  try {
    return JSON.parse(decodeURIComponent(escape(atob(s)))) as RuleInput;
  } catch {
    return null;
  }
}
