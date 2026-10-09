import { test } from "node:test";
import { deepEqual, equal, match } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile } from "../src/cli.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
function workspace(source: string) {
  const root = fs.mkdtempSync(path.join(packageRoot, ".object-build-context-"));
  const entry = path.join(root, "main.tsx");
  fs.writeFileSync(entry, source);
  return { root, entry, write: (text: string) => fs.writeFileSync(entry, text),
    cleanup: () => fs.rmSync(root, { recursive: true, force: true }) };
}
const success = "import { Slot, defineObject } from \"resoloop-jsx\";\n" +
  "const Def = defineObject<{}>()({ exports: {}, body: () => <Slot key=\"body\" name=\"Body\" /> });\n" +
  "export default <Slot key=\"root\" name=\"Scene\"><Def handle={Def.instance(\"desk\")} /></Slot>;\n";
const stale = "import { Slot, Component, defineObject, ref } from \"resoloop-jsx\";\n" +
  "const First = defineObject<{}>()({ exports: { root: \"$slot:body\" }, body: () => <Slot key=\"body\" name=\"Body\" /> });\n" +
  "const Second = defineObject<{}>()({ exports: { root: \"$slot:other\" }, body: () => <Slot key=\"other\" name=\"Other\" /> });\n" +
  "const handle = First.instance(\"desk\");\n" +
  "export default <Slot key=\"root\" name=\"Scene\"><First handle={handle} /><Component key=\"reader\" type=\"Synthetic.Device\" fields={{ Link: ref.export(Second.instance(\"desk\"), \"root\") }} /></Slot>;\n";

test("buildFile has no cross-call Object state and recovers after failure", async () => {
  const w = workspace("import { Slot, defineObject } from \"resoloop-jsx\";\n" +
    "const Def = defineObject<{}>()({ exports: {}, body: () => <Slot key=\"body\" name=\"Body\" /> });\n" +
    "const handle = Def.instance(\"desk\");\n" +
    "export default <Slot key=\"root\" name=\"Scene\"><Def handle={handle} /><Def handle={handle} /></Slot>;\n");
  try {
    const failed = await buildFile(w.entry, { projectRoot: w.root });
    equal(failed.exitCode, 1);
    match(failed.errors[0], /^APPLY_OBJECT_DUPLICATE: Object instance 'desk' occurs twice\.; TSX /);
    w.write(success);
    const expected = {
      schemaVersion: "1",
      ownership: { key: "root" },
      slot: { name: "Scene", key: "root" },
      components: [],
      children: [{ slot: { name: "Body", key: "desk::body" } }],
      authoring: { projectRoot: w.root, source: "main.tsx", ownershipSource: "root-key" },
    };
    for (let index = 0; index < 100; index++) {
      const result = await buildFile(w.entry, { projectRoot: w.root });
      equal(result.exitCode, 0, result.errors.join("\n"));
      deepEqual(result.document, expected);
      equal(JSON.stringify(result.document).includes("$typed"), false);
      equal(JSON.stringify(result.document).includes("$export:"), false);
    }
  } finally { w.cleanup(); }
});

test("same-ID foreign definitions remain unknown in a regular build", async () => {
  const w = workspace(stale);
  try {
    const result = await buildFile(w.entry, { projectRoot: w.root });
    equal(result.exitCode, 1);
    match(result.errors[0], /^APPLY_OBJECT_EXPORT_UNKNOWN: Unknown Object export 'desk\.root'\.; TSX /);
  } finally { w.cleanup(); }
});

test("a token saved by one build is unknown to a same-ID different definition in the next build", async () => {
  const property = "__resoloopObjectPreviousBuildTokenUnit14";
  const w = workspace(`import { Slot, defineObject, ref } from "resoloop-jsx";
const First = defineObject<{}>()({ exports: { root: "$slot:body" }, body: () => <Slot key="body" name="Body" /> });
const handle = First.instance("desk");
(globalThis as any)["${property}"] = ref.export(handle, "root");
export default <Slot key="root" name="Scene"><First handle={handle} /></Slot>;
`);
  try {
    const first = await buildFile(w.entry, { projectRoot: w.root });
    equal(first.exitCode, 0, first.errors.join("\n"));
    deepEqual(first.document, {
      schemaVersion: "1", ownership: { key: "root" }, slot: { key: "root", name: "Scene" }, components: [],
      children: [{ slot: { key: "desk::body", name: "Body" } }],
      authoring: { projectRoot: w.root, source: "main.tsx", ownershipSource: "root-key" },
    });
    const token = (globalThis as any)[property];
    match(token, /^\$export:/);
    w.write(`import { Slot, Component, defineObject } from "resoloop-jsx";
const Second = defineObject<{}>()({ exports: { root: "$slot:other" }, body: () => <Slot key="other" name="Other" /> });
const handle = Second.instance("desk");
const previousBuildToken = (globalThis as any)["${property}"];
export default <Slot key="root" name="Scene"><Second handle={handle} /><Component key="reader" type="Synthetic.Device" fields={{ Target: previousBuildToken }} /></Slot>;
`);
    const second = await buildFile(w.entry, { projectRoot: w.root });
    equal(second.exitCode, 1);
    match(second.errors[0], /^APPLY_OBJECT_EXPORT_UNKNOWN: Unknown Object export 'desk\.root'\.; TSX /);
  } finally { delete (globalThis as any)[property]; w.cleanup(); }
});

test("parallel buildFile calls keep their programs and output paths independent", async () => {
  const a = workspace(success.replace("\"Body\"", "\"Alpha\""));
  const b = workspace(success.replace("\"Body\"", "\"Beta\"").replace("\"desk\"", "\"ceiling\""));
  try {
    const [left, right] = await Promise.all([
      buildFile(a.entry, { projectRoot: a.root }), buildFile(b.entry, { projectRoot: b.root }),
    ]);
    equal(left.exitCode, 0, left.errors.join("\n"));
    equal(right.exitCode, 0, right.errors.join("\n"));
    equal(left.document?.children?.[0]?.slot.name, "Alpha");
    equal(right.document?.children?.[0]?.slot.name, "Beta");
    equal(left.document?.children?.[0]?.slot.key, "desk::body");
    equal(right.document?.children?.[0]?.slot.key, "ceiling::body");
  } finally { a.cleanup(); b.cleanup(); }
});
