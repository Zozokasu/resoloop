// CLI-pipeline tests: run buildFile (the same code path as the real CLI)
// against every fixture and compare emitted documents structurally.

import { test, describe } from "node:test";
import { deepEqual, equal, fail, match, ok } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile } from "../src/cli.js";

const here = path.dirname(fileURLToPath(import.meta.url)); // dist/test
const packageRoot = path.resolve(here, "..", "..");
const fixturesDir = path.join(packageRoot, "test", "fixtures");
const fixture = (name: string) => path.join(fixturesDir, name);

const expectedJson = (name: string) =>
  ({ ...JSON.parse(fs.readFileSync(fixture(name), { encoding: "utf8" })), authoring: {
    projectRoot: fixturesDir, source: name.replace(".expected.json", ".tsx"), ownershipSource: "root-key",
  } });

const SUCCESS_FIXTURES = [
  "nesting",
  "reference",
  "composition",
  "repeat",
  "conditional",
  "key-stability-base",
  "key-stability-inserted",
];

describe("fixtures compile and evaluate", () => {
  for (const name of SUCCESS_FIXTURES) {
    test(`${name}.tsx produces the expected document`, async () => {
      const result = await buildFile(fixture(`${name}.tsx`));
      deepEqual(result.errors, []);
      equal(result.exitCode, 0);
      deepEqual(result.document, expectedJson(`${name}.expected.json`));
    });
  }

  test("reference.tsx emits verbatim $-prefixed selectors", async () => {
    const result = await buildFile(fixture("reference.tsx"));
    equal(result.exitCode, 0);
    const components = result.document!.children[1].components!;
    const driver = components.find((c) => c.key === "driver")!;
    const grab = components.find((c) => c.key === "grab")!;
    equal(driver.fields!.Source, "$slot-member:target.Rotation");
    equal(driver.fields!.Target, "$member:spinner._speed");
    equal(grab.fields!.TipReference, "$slot:target");
    equal(grab.fields!.Other, "$component:driver");
  });

  test("output file is written when -o is given", async () => {
    const outDir = fs.mkdtempSync(path.join(fixturesDir, ".resoloop-jsx-test-out-"));
    try {
      const out = path.join(outDir, "nested", "out.json");
      const result = await buildFile(fixture("nesting.tsx"), { output: out });
      equal(result.exitCode, 0);
      const written = JSON.parse(fs.readFileSync(out, { encoding: "utf8" }));
      deepEqual(written, expectedJson("nesting.expected.json"));
    } finally {
      fs.rmSync(outDir, { recursive: true, force: true });
    }
  });
});

describe("failure fixtures", () => {
  test("missing-key-draft.tsx without --draft exits 1 with EXPLICIT_KEY_REQUIRED", async () => {
    const result = await buildFile(fixture("missing-key-draft.tsx"));
    equal(result.exitCode, 1);
    ok(result.errors.some((m) => m.includes("EXPLICIT_KEY_REQUIRED")));
  });

  test("missing-key-draft.tsx with --draft exits 0 and warns about generated keys", async () => {
    const result = await buildFile(fixture("missing-key-draft.tsx"), { draft: true });
    equal(result.exitCode, 0);
    ok(result.warnings.length > 0);
    match(result.warnings.join("\n"), /generated key "root\/nokey#0" for slot "NoKey" \(--draft\)/);
    deepEqual(result.document, expectedJson("missing-key-draft.expected.json"));
  });

  test("missing-key-normal-entry.tsx exits 2 with TypeScript diagnostics", async () => {
    const result = await buildFile(fixture("missing-key-normal-entry.tsx"));
    equal(result.exitCode, 2);
    ok(result.errors.length > 0);
    const text = result.errors.join("\n");
    match(text, /error TS\d+/);
    match(text, /'key'/);
  });

  test("duplicate-sibling-name.tsx exits 1 with DUPLICATE_SIBLING_NAME", async () => {
    const result = await buildFile(fixture("duplicate-sibling-name.tsx"));
    equal(result.exitCode, 1);
    ok(result.errors.some((m) => m.includes("DUPLICATE_SIBLING_NAME")));
  });

  test("type-error.tsx exits 2 with TypeScript diagnostics", async () => {
    const result = await buildFile(fixture("type-error.tsx"));
    equal(result.exitCode, 2);
    ok(result.errors.length > 0);
    match(result.errors.join("\n"), /error TS\d+/);
  });

  test("missing entry file exits 1", async () => {
    const result = await buildFile(fixture("does-not-exist.tsx"));
    equal(result.exitCode, 1);
    match(result.errors.join("\n"), /entry file not found/);
  });
});

describe("key stability across tree edits", () => {
  // key-stability-inserted.tsx adds a new sibling <Slot> at the front, which
  // shifts the array indices of every shared slot. Their stable keys — and
  // the content attached to each key — must remain byte-identical.
  interface KeyInfo {
    keyPath: string; // position expressed through stable keys, not indices
    indexPath: string; // position expressed through array indices
    payload: any; // the node's own content (slot spec + components)
  }

  function collect(doc: any): Map<string, KeyInfo> {
    const map = new Map<string, KeyInfo>();
    const visitNode = (node: any, parentKeyPath: string, indexPath: string) => {
      const key = node.slot.key;
      const keyPath = `${parentKeyPath}/${key}`;
      map.set(`slot:${key}`, {
        keyPath,
        indexPath,
        payload: { slot: node.slot, components: node.components ?? [] },
      });
      (node.components ?? []).forEach((c: any, i: number) => {
        map.set(`component:${c.key}`, {
          keyPath: `${keyPath}/${c.key}`,
          indexPath: `${indexPath}.components[${i}]`,
          payload: c,
        });
      });
      (node.children ?? []).forEach((child: any, i: number) => {
        visitNode(child, keyPath, `${indexPath}.children[${i}]`);
      });
    };
    map.set(`slot:${doc.slot.key}`, {
      keyPath: doc.slot.key,
      indexPath: "$",
      payload: { slot: doc.slot, components: doc.components ?? [] },
    });
    (doc.components ?? []).forEach((c: any, i: number) => {
      map.set(`component:${c.key}`, {
        keyPath: `${doc.slot.key}/${c.key}`,
        indexPath: `$.components[${i}]`,
        payload: c,
      });
    });
    (doc.children ?? []).forEach((child: any, i: number) => {
      visitNode(child, doc.slot.key, `$.children[${i}]`);
    });
    return map;
  }

  test("shared keys are position-independent", async () => {
    const base = await buildFile(fixture("key-stability-base.tsx"));
    const inserted = await buildFile(fixture("key-stability-inserted.tsx"));
    equal(base.exitCode, 0);
    equal(inserted.exitCode, 0);

    const baseKeys = collect(base.document!);
    const insertedKeys = collect(inserted.document!);

    // Every key in the base tree exists in the inserted tree, at the same
    // stable position, with byte-identical attached content.
    for (const [id, info] of baseKeys) {
      const other = insertedKeys.get(id);
      if (other === undefined) fail(`key ${id} missing from inserted document`);
      equal(other.keyPath, info.keyPath, `key ${id} moved in stable-key space`);
      deepEqual(other.payload, info.payload, `key ${id} content changed`);
    }

    // The insertion shifted index paths, proving "same stable position" is
    // not the same thing as "same array index".
    ok(
      baseKeys.get("slot:header")!.indexPath !==
        insertedKeys.get("slot:header")!.indexPath,
      "expected header's index path to shift after front insertion"
    );
    ok(insertedKeys.has("slot:sidebar"), "inserted document missing new key");
  });
});
