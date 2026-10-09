import { test } from "node:test";
import { deepEqual, equal } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile } from "../src/cli.js";
import { defineObject } from "../src/object-definition.js";
import { exportReference, isReferenceToken } from "../src/object-refs.js";
import { Component, Slot } from "../src/elements.js";
import { jsx } from "../src/jsx-runtime.js";
import { expandObjects } from "../src/object-expand.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

function workspace(source: string) {
  const root = fs.mkdtempSync(path.join(packageRoot, ".fix-r1-token-"));
  const entry = path.join(root, "main.tsx");
  fs.writeFileSync(entry, source);
  return { root, entry, cleanup: () => fs.rmSync(root, { recursive: true, force: true }) };
}

test("prefix-like ordinary fields keep the legacy document, warnings, and exit code", async () => {
  for (const [literal, text] of [["$export:hello", "export"], ["$object-target:hello", "target"]]) {
    const source = `import {Slot,Component} from "resoloop-jsx"; export default <Slot key="root" name="Scene"><Component key="label" type="Any.Type" fields={{Text:${JSON.stringify(literal)}}} /></Slot>;`;
    const w = workspace(source);
    try {
      const result = await buildFile(w.entry, { projectRoot: w.root });
      equal(result.exitCode, 0, result.errors.join("\n"));
      deepEqual(result.warnings, []);
      const document: any = { ...result.document!, authoring: { ...result.document!.authoring } };
      delete document.authoring.projectRoot;
      deepEqual(document, {
        schemaVersion: "1",
        ownership: { key: "root" },
        slot: { name: "Scene", key: "root" },
        components: [{ type: "Any.Type", key: "label", fields: { Text: literal } }],
        children: [],
        authoring: { source: "main.tsx", ownershipSource: "root-key" },
      }, text);
    } finally { w.cleanup(); }
  }
});

test("only complete API token shapes activate expansion; foreign definition tokens still fail", () => {
  equal(isReferenceToken("$export:hello"), false);
  equal(isReferenceToken("$object-target:hello"), false);
  const First = defineObject<{}>()({ exports: { root: "$slot:body" }, body: () => jsx(Slot, { key: "body", name: "Body" }) });
  const Other = defineObject<{}>()({ exports: { root: "$slot:other" }, body: () => jsx(Slot, { key: "other", name: "Other" }) });
  const token = exportReference(Other.instance("desk"), "root");
  equal(isReferenceToken(token), true);

  const root = jsx(Slot, { key: "root", name: "Scene", children: [
    First({ handle: First.instance("desk") }),
    jsx(Component, { key: "reader", type: "Any.Type", fields: { Target: token } }),
  ] });
  let failure: unknown;
  try { expandObjects(root, []); } catch (error) { failure = error; }
  equal((failure as { code?: string } | undefined)?.code, "APPLY_OBJECT_EXPORT_UNKNOWN");
});
