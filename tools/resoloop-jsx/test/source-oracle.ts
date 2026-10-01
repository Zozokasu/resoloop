import * as fs from "node:fs";
import * as path from "node:path";
import { deepEqual, equal, ok } from "node:assert/strict";
import { hashText } from "../src/input-snapshot.js";

export function checkSourceOracle(packageRoot: string, bundle: any): any[] {
  const dir = path.join(packageRoot, "test/fixtures/source-v11");
  const expectedIr = JSON.parse(fs.readFileSync(path.join(dir, "ir.handwritten.json")));
  const ir = JSON.parse(bundle.ir.text); delete ir.authoring;
  deepEqual(ir, expectedIr);
  const oracle = JSON.parse(fs.readFileSync(path.join(dir, "locations.handwritten.json")));
  const map = JSON.parse(bundle.map.text);
  const source = path.join(dir, "main.tsx");
  const text = fs.readFileSync(source, { encoding: "utf8" });
  const lines = text.split("\n");
  const point = (line: number, column: number) => ({ line, column, offset: lines.slice(0, line - 1).reduce((n, s) => n + s.length + 1, 0) + column - 1 });
  function location(start: number[], end: number[]) {
    return { status: "known", file: source, sha256: hashText(text), range: { start: point(start[0], start[1]), end: point(end[0], end[1]) } };
  }
  for (const row of oracle) {
    const entry = map.entries.find((e: any) => e.jsonPath === row.path);
    ok(entry, row.path); equal(entry.key, row.key); equal(entry.member, row.member);
    row.expectedSource = row.source === "unknown" ? { status: "unknown" } : location(row.start, row.end);
    deepEqual(row.value ? entry.valueSource : entry.source, row.expectedSource, row.path);
    if (row.related) {
      row.expectedRelated = row.related.map((r: number[]) => location(r.slice(0, 2), r.slice(2)));
      deepEqual(entry.related, row.expectedRelated, row.path + " related");
    }
  }
  return oracle;
}
