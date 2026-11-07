// Structure-aware side-by-side alignment of two JSON documents.
// Both documents are pretty-printed in lockstep while walking the trees together, so the rows line up by JSON
// path (not by text), and every row knows its path and kind. Runs inside a Web Worker for multi-MB bodies.

export const K = { Same: 0, Changed: 1, Added: 2, Removed: 3, Type: 4 } as const;
export type Kind = (typeof K)[keyof typeof K];

export interface Row {
  k: Kind;
  /** Left (Main) text, null when the line only exists on Dev. */
  l: string | null;
  /** Right (Dev) text, null when the line only exists on Main. */
  r: string | null;
  /** 1-based line numbers in each pretty-printed document; 0 when absent on that side. */
  ln: number;
  rn: number;
  path: string;
}

export interface AlignResult {
  rows: Row[];
  json: boolean;
  counts: Record<Kind, number>;
  leftLines: number;
  rightLines: number;
}

type J = null | boolean | number | string | J[] | { [k: string]: J };

const IDENT = /^[A-Za-z_$][A-Za-z0-9_$-]*$/;
const seg = (key: string) => (IDENT.test(key) ? `.${key}` : `['${key.replace(/'/g, "\\'")}']`);
const kindOf = (v: J) => (v === null ? "null" : Array.isArray(v) ? "array" : typeof v);
const PAD = "  ";

/** Arrays matching `pattern` (e.g. `$.items`, `$.orders[*].lines`) are aligned by these key fields, like the server's PrimaryKey rule. */
export interface KeySpec {
  pattern: string;
  keys: string[];
}

export interface AlignOptions {
  keys?: KeySpec[];
}

export function align(mainText: string, devText: string, options: AlignOptions = {}): AlignResult {
  const a = tryParse(mainText);
  const b = tryParse(devText);
  if (a.ok && b.ok) return alignJson(a.value, b.value, options);
  return alignText(a.ok ? JSON.stringify(a.value, null, 2) : mainText, b.ok ? JSON.stringify(b.value, null, 2) : devText);
}

function tryParse(text: string): { ok: true; value: J } | { ok: false } {
  if (!text.trim()) return { ok: false };
  try {
    return { ok: true, value: JSON.parse(text) as J };
  } catch {
    return { ok: false };
  }
}

const normalise = (p: string) => p.trim().replace(/^\$?\.?/, "$.").replace(/\$\.\[/, "$[").replace(/\[[^\]]*\]/g, "[*]").replace(/\.$/, "");

function keyValue(el: J, chains: string[][]): string | null {
  if (el === null || typeof el !== "object" || Array.isArray(el)) return null;
  const parts: string[] = [];
  for (const chain of chains) {
    let cur: J = el;
    for (const name of chain) {
      if (cur === null || typeof cur !== "object" || Array.isArray(cur) || !(name in cur)) return null;
      cur = (cur as Record<string, J>)[name];
    }
    if (cur === null || typeof cur === "object") return cur === null ? null : JSON.stringify(cur);
    parts.push(typeof cur === "string" ? cur : JSON.stringify(cur));
  }
  return parts.join("|");
}

/** Pairs of element indexes to show side by side. Keyed when a spec matches and every key is present and unique. */
function plan(x: J[], y: J[], path: string, specs: { pattern: string; chains: string[][]; label: string }[]): { pairs: [number | null, number | null, string][] } {
  const norm = normalise(path);
  const spec = specs.find((s) => s.pattern === norm);
  if (spec) {
    const kx = x.map((e) => keyValue(e, spec.chains));
    const ky = y.map((e) => keyValue(e, spec.chains));
    const unique = (k: (string | null)[]) => k.every((v) => v !== null) && new Set(k).size === k.length;
    if (unique(kx) && unique(ky)) {
      const iy = new Map(ky.map((k, j) => [k!, j]));
      const pairs: [number | null, number | null, string][] = [];
      const used = new Set<number>();
      kx.forEach((k, i) => {
        const j = iy.get(k!);
        if (j !== undefined) used.add(j);
        pairs.push([i, j ?? null, `${path}[${spec.label}=${k}]`]);
      });
      ky.forEach((k, j) => { if (!used.has(j)) pairs.push([null, j, `${path}[${spec.label}=${k}]`]); });
      return { pairs };
    }
  }
  const n = Math.max(x.length, y.length);
  return { pairs: Array.from({ length: n }, (_, i) => [i < x.length ? i : null, i < y.length ? i : null, `${path}[${i}]`] as [number | null, number | null, string]) };
}

export function alignJson(a: J, b: J, options: AlignOptions = {}): AlignResult {
  const specs = (options.keys ?? []).map((k) => ({
    pattern: normalise(k.pattern),
    chains: k.keys.map((c) => c.replace(/^\$\.?/, "").split(".").filter(Boolean)),
    label: k.keys.map((c) => c.replace(/^\$\.?/, "")).join("+"),
  }));
  const rows: Row[] = [];
  let ln = 0;
  let rn = 0;
  const counts: Record<Kind, number> = { 0: 0, 1: 0, 2: 0, 3: 0, 4: 0 };

  const both = (k: Kind, l: string, r: string, path: string) => {
    rows.push({ k, l, r, ln: ++ln, rn: ++rn, path });
    counts[k]++;
  };

  const lines = (v: J, prefix: string, depth: number, comma: string, out: string[]) => {
    const pad = PAD.repeat(depth);
    if (v !== null && typeof v === "object") {
      const isArr = Array.isArray(v);
      const entries: [string, J][] = isArr ? (v as J[]).map((x) => ["", x]) : Object.entries(v as Record<string, J>);
      if (entries.length === 0) {
        out.push(`${pad}${prefix}${isArr ? "[]" : "{}"}${comma}`);
        return;
      }
      out.push(`${pad}${prefix}${isArr ? "[" : "{"}`);
      entries.forEach(([key, child], i) =>
        lines(child, isArr ? "" : `${JSON.stringify(key)}: `, depth + 1, i < entries.length - 1 ? "," : "", out));
      out.push(`${pad}${isArr ? "]" : "}"}${comma}`);
    } else {
      out.push(`${pad}${prefix}${JSON.stringify(v)}${comma}`);
    }
  };

  const side = (which: "l" | "r", v: J, prefix: string, depth: number, comma: string, k: Kind, path: string) => {
    const out: string[] = [];
    lines(v, prefix, depth, comma, out);
    for (const text of out) {
      if (which === "l") rows.push({ k, l: text, r: null, ln: ++ln, rn: 0, path });
      else rows.push({ k, l: null, r: text, ln: 0, rn: ++rn, path });
      counts[k]++;
    }
  };

  const walk = (x: J, y: J, prefix: string, depth: number, cl: string, cr: string, path: string) => {
    const tx = kindOf(x);
    const ty = kindOf(y);
    const pad = PAD.repeat(depth);

    if (tx === "object" && ty === "object") {
      const ox = x as Record<string, J>;
      const oy = y as Record<string, J>;
      const kx = Object.keys(ox);
      const ky = Object.keys(oy);
      if (kx.length === 0 && ky.length === 0) return both(K.Same, `${pad}${prefix}{}${cl}`, `${pad}${prefix}{}${cr}`, path);
      both(K.Same, `${pad}${prefix}{`, `${pad}${prefix}{`, path);
      const ix = new Map(kx.map((k, i) => [k, i]));
      const iy = new Map(ky.map((k, i) => [k, i]));
      const union = kx.concat(ky.filter((k) => !ix.has(k)));
      for (const key of union) {
        const p = path + seg(key);
        const pre = `${JSON.stringify(key)}: `;
        const inX = ix.get(key);
        const inY = iy.get(key);
        const ccl = inX !== undefined && inX < kx.length - 1 ? "," : "";
        const ccr = inY !== undefined && inY < ky.length - 1 ? "," : "";
        if (inX !== undefined && inY !== undefined) walk(ox[key], oy[key], pre, depth + 1, ccl, ccr, p);
        else if (inX !== undefined) side("l", ox[key], pre, depth + 1, ccl, K.Removed, p);
        else side("r", oy[key], pre, depth + 1, ccr, K.Added, p);
      }
      both(K.Same, `${pad}}${cl}`, `${pad}}${cr}`, path);
      return;
    }

    if (tx === "array" && ty === "array") {
      const ax = x as J[];
      const ay = y as J[];
      if (ax.length === 0 && ay.length === 0) return both(K.Same, `${pad}${prefix}[]${cl}`, `${pad}${prefix}[]${cr}`, path);
      both(ax.length === ay.length ? K.Same : K.Changed, `${pad}${prefix}[`, `${pad}${prefix}[`, path);
      const { pairs } = plan(ax, ay, path, specs);
      const totalL = pairs.filter((p) => p[0] !== null).length;
      const totalR = pairs.filter((p) => p[1] !== null).length;
      let seenL = 0;
      let seenR = 0;
      for (const [i, j, p] of pairs) {
        const ccl = i !== null && ++seenL < totalL ? "," : "";
        const ccr = j !== null && ++seenR < totalR ? "," : "";
        if (i !== null && j !== null) walk(ax[i], ay[j], "", depth + 1, ccl, ccr, p);
        else if (i !== null) side("l", ax[i], "", depth + 1, ccl, K.Removed, p);
        else if (j !== null) side("r", ay[j], "", depth + 1, ccr, K.Added, p);
      }
      both(K.Same, `${pad}]${cl}`, `${pad}]${cr}`, path);
      return;
    }

    if (tx !== "object" && tx !== "array" && ty !== "object" && ty !== "array") {
      const sx = JSON.stringify(x);
      const sy = JSON.stringify(y);
      const k = sx === sy ? K.Same : tx === ty || (tx === "boolean" && ty === "boolean") ? K.Changed : K.Type;
      return both(k, `${pad}${prefix}${sx}${cl}`, `${pad}${prefix}${sy}${cr}`, path);
    }

    // Container vs scalar or object vs array: show both sides, unaligned, as a type change.
    side("l", x, prefix, depth, cl, K.Type, path);
    side("r", y, prefix, depth, cr, K.Type, path);
  };

  walk(a, b, "", 0, "", "", "$");
  return { rows, json: true, counts, leftLines: ln, rightLines: rn };
}

/** Fallback for non-JSON bodies: positional line comparison. */
export function alignText(mainText: string, devText: string): AlignResult {
  const l = mainText.split("\n");
  const r = devText.split("\n");
  const rows: Row[] = [];
  const counts: Record<Kind, number> = { 0: 0, 1: 0, 2: 0, 3: 0, 4: 0 };
  const n = Math.max(l.length, r.length);
  for (let i = 0; i < n; i++) {
    const hasL = i < l.length;
    const hasR = i < r.length;
    const k: Kind = hasL && hasR ? (l[i] === r[i] ? K.Same : K.Changed) : hasL ? K.Removed : K.Added;
    rows.push({ k, l: hasL ? l[i] : null, r: hasR ? r[i] : null, ln: hasL ? i + 1 : 0, rn: hasR ? i + 1 : 0, path: "body" });
    counts[k]++;
  }
  return { rows, json: false, counts, leftLines: l.length, rightLines: r.length };
}

export type DisplayItem = { t: "row"; i: number } | { t: "fold"; start: number; end: number };

/** Collapses runs of unchanged rows longer than 2×context, except folds the user expanded (keyed by fold start). */
export function fold(rows: Row[], context: number, expanded: ReadonlySet<number>, enabled = true): DisplayItem[] {
  const out: DisplayItem[] = [];
  if (!enabled) {
    for (let i = 0; i < rows.length; i++) out.push({ t: "row", i });
    return out;
  }
  let i = 0;
  while (i < rows.length) {
    if (rows[i].k !== K.Same) {
      out.push({ t: "row", i: i++ });
      continue;
    }
    let j = i;
    while (j < rows.length && rows[j].k === K.Same) j++;
    const len = j - i;
    const head = i === 0 ? 0 : context;
    const tail = j === rows.length ? 0 : context;
    if (len > head + tail + 1 && !expanded.has(i + head)) {
      for (let x = i; x < i + head; x++) out.push({ t: "row", i: x });
      out.push({ t: "fold", start: i + head, end: j - tail });
      for (let x = j - tail; x < j; x++) out.push({ t: "row", i: x });
    } else {
      for (let x = i; x < j; x++) out.push({ t: "row", i: x });
    }
    i = j;
  }
  return out;
}

/** Index of the first row for a server-reported path. Keyed segments ([sku=A]) fall back to the closest ancestor. */
export function findRow(rows: Row[], path: string): number {
  const candidates = [path];
  let p = path;
  while (/\[[^\]]*=[^\]]*\]/.test(p)) {
    p = p.replace(/\[[^\]]*=[^\]]*\][^[]*$/, "");
    candidates.push(p);
  }
  for (const c of candidates) {
    const exact = rows.findIndex((r) => r.path === c && r.k !== K.Same);
    if (exact >= 0) return exact;
    const any = rows.findIndex((r) => r.path === c || r.path.startsWith(c + ".") || r.path.startsWith(c + "["));
    if (any >= 0) return any;
  }
  return -1;
}
