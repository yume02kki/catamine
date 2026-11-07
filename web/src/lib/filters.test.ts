import { describe, expect, it } from "vitest";
import type { LogItem } from "../api/types";
import { matches, toFilters } from "./filters";
import { suggestRule, wildcardPath } from "./suggest";

const view = { q: "", verdict: "", sort: "newest" as const, range: "all" as const, sig: "" };

describe("search syntax", () => {
  it("parses tokens and free text", () => {
    const f = toFilters({ ...view, q: "path:/v1 method:post status:502 kind:added job:j1" });
    expect(f).toMatchObject({ path: "/v1", pathMode: "prefix", method: "POST", devStatus: 502, diffKind: "added", jobId: "j1" });
    expect(toFilters({ ...view, q: "search" })).toMatchObject({ path: "search", pathMode: "contains" });
    expect(toFilters({ ...view, range: "1h" }, 3_600_000 * 10).from).toBe(new Date(3_600_000 * 9).toISOString());
  });

  it("matches live rows against the active filters", () => {
    const item = { verdict: "mismatch", method: "GET", path: "/v1/search", devStatus: 200, mainStatus: 200, diffKinds: ["added"], source: "live", signature: "5", jobId: null } as unknown as LogItem;
    expect(matches(item, toFilters({ ...view, q: "path:/v1", verdict: "mismatch" }))).toBe(true);
    expect(matches(item, toFilters({ ...view, verdict: "dev_error,dev_timeout" }))).toBe(false);
    expect(matches(item, toFilters({ ...view, q: "kind:removed" }))).toBe(false);
  });
});

describe("rule suggestions", () => {
  it("turns concrete paths into patterns and picks a sensible rule", () => {
    expect(wildcardPath("$.items[sku=A1].price[3].x")).toBe("$.items[*].price[*].x");
    expect(suggestRule({ k: "type_changed", p: "$.total", m: "1", d: '"1"' })?.rule.type).toBe("TypeCoercion");
    expect(suggestRule({ k: "changed", p: "$.a[2].price", m: "49.99", d: "49.9" })?.rule).toMatchObject({ type: "NumericTolerance", config: { path: "$.a[*].price" } });
    expect(suggestRule({ k: "added", p: "$.trace", m: null, d: "{}" })?.rule.type).toBe("IgnoreField");
    expect(suggestRule({ k: "header", p: "header:cache-control", m: "a", d: "b" })?.rule.config).toEqual({ headers: ["cache-control"] });
  });
});
