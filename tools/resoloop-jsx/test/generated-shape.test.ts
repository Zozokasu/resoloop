import { test } from "node:test";
import { deepEqual, equal, throws } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile } from "../src/cli.js";
import { evaluate, BuildError } from "../src/evaluate.js";
import { SLOT_SCALAR_PROPS, COMPONENT_SCALAR_PROPS } from "../src/generated/apply-copy.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const fixtures = path.join(packageRoot, "test", "fixtures");

test("TSX build matches handwritten shape oracle including order, omission, false, null, root/child and scope", async () => {
  const result = await buildFile(path.join(fixtures, "generated-shape-oracle.tsx"));
  equal(result.exitCode, 0);
  deepEqual(result.errors, []);
  const expected = JSON.parse(fs.readFileSync(path.join(fixtures, "generated-shape-oracle.expected.json"), { encoding: "utf8" }));
  expected.authoring = { projectRoot: fixtures, source: "generated-shape-oracle.tsx", ownershipSource: "root-key" };
  // Structural AND insertion-order comparison. Only machine-local authoring context is substituted.
  deepEqual(result.document, expected);
  equal(JSON.stringify(result.document), JSON.stringify(expected));
});

test("generated copies every declared scalar using independently chosen values", () => {
  const slotValues: Record<string, any> = {
    position: [9, 8, 7], rotation: [0, 1, 0, 0], scale: [3, 2, 1],
    managedFields: ["scale"], preserveWorldTransform: false, migrateFrom: "earlier",
    relocationTransform: "world", runtimeRelocatable: false,
  };
  const componentValues: Record<string, any> = {
    fields: { Enabled: false, Empty: null }, migrateFrom: "previous", initialFields: { Seed: 23 }, identityFields: ["Seed"],
  };
  // The reflection manifest checks coverage; it is never used to construct expected values.
  const manifest = JSON.parse(fs.readFileSync(path.join(packageRoot, "src/generated/apply-shape.json"), { encoding: "utf8" }));
  for (const [type, values, scalars] of [
    ["ApplySlotSpec", slotValues, SLOT_SCALAR_PROPS],
    ["ApplyComponentSpec", componentValues, COMPONENT_SCALAR_PROPS],
  ] as const) {
    const declared = manifest.records.find((r: any) => r.name === type).properties
      .filter((p: any) => p.copy !== "Identity" && p.copy !== "RootOnly").map((p: any) => p.name);
    deepEqual([...scalars], declared);
    deepEqual(Object.keys(values).sort(), declared.slice().sort());
  }
  const result = evaluate({ kind: "slot", props: { name: "All", key: "all", ...slotValues,
    children: { kind: "component", props: { type: "Oracle", key: "component", ...componentValues } },
  } }).document;
  deepEqual(result.slot, { name: "All", key: "all", ...slotValues });
  deepEqual(result.components, [{ type: "Oracle", key: "component", ...componentValues }]);
});

test("generated copies preserve omission, explicit false and null without inserting C# defaults", () => {
  const document = evaluate({ kind: "slot", props: { name: "Minimal", key: "minimal", position: undefined,
    preserveWorldTransform: false, migrateFrom: null,
    children: { kind: "component", props: { type: "Oracle", key: "c", fields: null, initialFields: undefined } },
  } }).document;
  equal(JSON.stringify(document), '{"schemaVersion":"1","ownership":{"key":"minimal"},"slot":{"name":"Minimal","key":"minimal","preserveWorldTransform":false,"migrateFrom":null},"components":[{"type":"Oracle","key":"c","fields":null}],"children":[]}');
});

test("generated copy boundary preserves BuildError codes and number paths", () => {
  for (const [prop, value] of [["position", [1, Infinity, 3]], ["scale", [NaN, 1, 1]]] as const)
    throws(() => evaluate({ kind: "slot", props: { name: "Bad", key: "bad", [prop]: value } }),
      (e: unknown) => e instanceof BuildError && e.code === "NON_FINITE_NUMBER" && e.message.includes(`$.${prop}`));
  for (const prop of ["fields", "initialFields"])
    throws(() => evaluate({ kind: "slot", props: { name: "Root", key: "root", children: {
      kind: "component", props: { type: "Oracle", key: "bad", [prop]: { Value: Infinity } },
    } } }), (e: unknown) => e instanceof BuildError && e.code === "NON_FINITE_NUMBER" && e.message.includes(`$.components[0].${prop}.Value`));
  for (const [root, code] of [
    [{ kind: "slot", props: { key: "missing-name" } }, "SLOT_NAME_MISSING"],
    [{ kind: "slot", props: { name: "Root", key: "root", children: { kind: "component", props: { key: "missing-type" } } } }, "COMPONENT_TYPE_MISSING"],
    [{ kind: "slot", props: { name: "NoKey" } }, "EXPLICIT_KEY_REQUIRED"],
  ] as const)
    throws(() => evaluate(root), (e: unknown) => e instanceof BuildError && e.code === code);
});
