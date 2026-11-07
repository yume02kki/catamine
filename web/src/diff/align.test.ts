import { describe, expect, it } from "vitest";
import { align, findRow, fold, K } from "./align";

describe("align", () => {
  it("aligns identical documents as unchanged rows with matching line numbers", () => {
    const r = align('{"a":1,"b":[1,2]}', '{"b":[1,2],"a":1}');
    expect(r.json).toBe(true);
    expect(r.rows.every((x) => x.k === K.Same)).toBe(true);
    expect(r.rows[0]).toMatchObject({ l: "{", r: "{", ln: 1, rn: 1 });
  });

  it("marks changes, additions, removals and type changes with paths", () => {
    const r = align('{"name":"Ann","gone":true,"n":1}', '{"name":"Bob","n":"1","new":[1]}');
    const by = (path: string) => r.rows.filter((x) => x.path === path);
    expect(by("$.name")[0].k).toBe(K.Changed);
    expect(by("$.gone")[0]).toMatchObject({ k: K.Removed, r: null, rn: 0 });
    expect(by("$.n")[0].k).toBe(K.Type);
    expect(by("$.new").map((x) => x.k)).toEqual([K.Added, K.Added, K.Added]);
    expect(by("$.new").every((x) => x.l === null)).toBe(true);
  });

  it("produces valid pretty JSON on each side (commas included)", () => {
    const a = { x: [1, { y: 2 }], z: "s", e: {} };
    const b = { x: [1], z: "t", w: null };
    const r = align(JSON.stringify(a), JSON.stringify(b));
    const left = r.rows.filter((x) => x.l != null).map((x) => x.l).join("\n");
    const right = r.rows.filter((x) => x.r != null).map((x) => x.r).join("\n");
    expect(JSON.parse(left)).toEqual(a);
    expect(JSON.parse(right)).toEqual(b);
  });

  it("falls back to line comparison for non-JSON bodies", () => {
    const r = align("<html>\nok\n</html>", "<html>\nbad\n</html>");
    expect(r.json).toBe(false);
    expect(r.rows.map((x) => x.k)).toEqual([K.Same, K.Changed, K.Same]);
  });

  it("handles multi-megabyte documents", () => {
    const items = Array.from({ length: 40_000 }, (_, i) => ({ id: i, name: `item ${i}`, tags: ["a", "b"] }));
    const dev = items.map((x) => (x.id === 1234 ? { ...x, name: "changed" } : x));
    const t = performance.now();
    const r = align(JSON.stringify({ items }), JSON.stringify({ items: dev }));
    expect(performance.now() - t).toBeLessThan(5000);
    expect(r.rows.filter((x) => x.k !== K.Same).map((x) => x.path)).toEqual(["$.items[1234].name"]);
  });
});

describe("fold", () => {
  const rows = align(JSON.stringify({ a: Array.from({ length: 50 }, (_, i) => i), b: 1 }), JSON.stringify({ a: Array.from({ length: 50 }, (_, i) => i), b: 2 })).rows;

  it("collapses long unchanged runs, keeping context around changes", () => {
    const d = fold(rows, 3, new Set());
    expect(d.filter((x) => x.t === "fold")).toHaveLength(1);
    const changed = rows.findIndex((x) => x.k !== K.Same);
    expect(d.some((x) => x.t === "row" && x.i === changed)).toBe(true);
    expect(d.some((x) => x.t === "row" && x.i === changed - 3)).toBe(true);
  });

  it("expands a fold by its start index and can be disabled", () => {
    const f = fold(rows, 3, new Set()).find((x) => x.t === "fold")!;
    expect(fold(rows, 3, new Set([f.t === "fold" ? f.start : -1])).some((x) => x.t === "fold")).toBe(false);
    expect(fold(rows, 3, new Set(), false)).toHaveLength(rows.length);
  });
});

describe("findRow", () => {
  it("finds exact paths and falls back from keyed segments to the array", () => {
    const rows = align('{"items":[{"sku":"A","p":1}]}', '{"items":[{"sku":"A","p":2}]}').rows;
    expect(rows[findRow(rows, "$.items[0].p")].path).toBe("$.items[0].p");
    expect(findRow(rows, "$.items[sku=A].p")).toBeGreaterThanOrEqual(0);
    expect(findRow(rows, "$.nope")).toBe(-1);
  });
});

describe("primary keys", () => {
  it("aligns arrays by key so one missing item does not shift the rest", () => {
    const main = { items: [{ sku: "A", p: 1 }, { sku: "B", p: 2 }, { sku: "C", p: 3 }] };
    const dev = { items: [{ sku: "A", p: 1 }, { sku: "C", p: 3 }, { sku: "D", p: 4 }] };
    const r = align(JSON.stringify(main), JSON.stringify(dev), { keys: [{ pattern: "$.items", keys: ["sku"] }] });
    const changedPaths = [...new Set(r.rows.filter((x) => x.k !== K.Same).map((x) => x.path))];
    expect(changedPaths).toEqual(["$.items[sku=B]", "$.items[sku=D]"]);
    const left = r.rows.filter((x) => x.l != null).map((x) => x.l).join("\n");
    const right = r.rows.filter((x) => x.r != null).map((x) => x.r).join("\n");
    expect(JSON.parse(left)).toEqual(main);
    expect(JSON.parse(right).items).toHaveLength(3);
  });

  it("falls back to positions when keys are missing or duplicated", () => {
    const r = align('{"a":[{"id":1},{"x":2}]}', '{"a":[{"id":1},{"x":3}]}', { keys: [{ pattern: "a", keys: ["id"] }] });
    expect(r.rows.some((x) => x.path === "$.a[1].x" && x.k === K.Changed)).toBe(true);
  });
});
