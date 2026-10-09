import { test } from "node:test";
import { equal, match } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { createHash } from "node:crypto";
import { buildFile } from "../src/cli.js";
import { objectTestRuntimeTable } from "./object-test-helpers.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const hash = (text: string) => createHash("sha256").update(text, "utf8").digest("hex");
const registry = 'type SyntheticRegistry = { membersComplete: false; members: { Enabled: boolean; Count: number; Label: string; Weight: number; Target: string | null; Uncertain: unknown; UnconfirmedTarget: unknown }; memberMetadata: { Enabled: { kind: "field"; confirmed: true; scalar: "boolean"; fieldInterface: string }; Count: { kind: "field"; confirmed: true; scalar: "int32"; fieldInterface: string }; Label: { kind: "field"; confirmed: true; scalar: "string"; fieldInterface: string }; Weight: { kind: "field"; confirmed: true; scalar: "single"; fieldInterface: string }; Target: { kind: "reference"; confirmed: true; scalar: null; fieldInterface: null }; Uncertain: { kind: "other"; confirmed: false; scalar: null; fieldInterface: null }; UnconfirmedTarget: { kind: "reference"; confirmed: false; scalar: null; fieldInterface: null } } };\ndeclare module "resoloop-jsx" { interface CatalogComponentRegistry { "Synthetic.Device": SyntheticRegistry; "Synthetic.Other": SyntheticRegistry; "Missing.Type": SyntheticRegistry } }';

function workspace(source: string) {
  const root = fs.mkdtempSync(path.join(packageRoot, ".object-diagnostics-"));
  const base = objectTestRuntimeTable();
  const runtimeTable = { ...base, types: { ...base.types,
    "Synthetic.Other": { ...base.types["Synthetic.Device"], canonicalType: "Synthetic.Other" } } };
  const table = JSON.stringify(runtimeTable);
  const runtime = "// resoloop-catalog-fields: {\"formatVersion\":\"1\",\"contentHash\":\"handwritten-runtime-hash\",\"generatorVersion\":\"3\"}\nimport type { CatalogRuntimeTable } from \"resoloop-jsx\";\nexport const catalog = " + table + " as const satisfies CatalogRuntimeTable;\n";
  const declaration = "// resoloop-catalog-types: {\"formatVersion\":\"1\",\"contentHash\":\"handwritten-runtime-hash\",\"generatorVersion\":\"3\",\"runtimeFile\":\"catalog-fields.ts\",\"runtimeSha256\":\"" + hash(runtime) + "\"}\nexport {};\n";
  fs.mkdirSync(path.join(root, ".resoloop"));
  fs.writeFileSync(path.join(root, ".resoloop/catalog-fields.ts"), runtime);
  fs.writeFileSync(path.join(root, ".resoloop/catalog-types.d.ts"), declaration);
  const entry = path.join(root, "main.tsx");
  const [imports, ...rest] = source.split("\n");
  fs.writeFileSync(entry, [imports, registry, ...rest].join("\n"));
  return { root, entry, declarationPath: path.join(root, ".resoloop/catalog-types.d.ts"), cleanup: () => fs.rmSync(root, { recursive: true, force: true }) };
}

const head = "import { Slot, Component, defineObject, componentFields, ref } from \"resoloop-jsx\";\n" +
  "import { catalog } from \"./.resoloop/catalog-fields.js\";\n" +
  "const fields = componentFields(\"Synthetic.Device\", catalog);\n";

async function diagnostic(source: string, code: string, message: string, line: number, column: number) {
  const w = workspace(source);
  try {
    const result = await buildFile(w.entry, { projectRoot: w.root });
    equal(result.exitCode, 1, result.errors.join("\n"));
    equal(result.errors[0], code + ": " + message + "; TSX " + w.entry + ":" + line + ":" + column);
  } finally { w.cleanup(); }
}

test("regular build keeps fixed Object diagnostic codes, messages, and handwritten locations", async () => {
  const cases: Array<[string, string, string, number, number]> = [
    [head + "const Def = defineObject<{}>()({\n  exports: {},\n  body: () => null,\n});\nexport default <Slot key=\"root\" name=\"Scene\"><Def handle={Def.instance(\"desk\")} /></Slot>;\n",
      "APPLY_OBJECT_INVALID", "Object 'desk': body must contain exactly one root Slot.", 8, 9],
    [head + "const Def = defineObject<{}>()({ exports: { root: \"$slot:body\" }, body: () => <Slot key=\"body\" name=\"Body\" /> });\nconst handle = Def.instance(\"desk\");\nexport default <Slot key=\"root\" name=\"Scene\"><Def handle={handle} /><Def handle={handle} /></Slot>;\n",
      "APPLY_OBJECT_DUPLICATE", "Object instance 'desk' occurs twice.", 8, 82],
    [head + "const Def = defineObject<{}>()({ exports: { bad: ref.slot(\"root\") }, body: () => <Slot key=\"body\" name=\"Body\" /> });\nexport default <Slot key=\"root\" name=\"Scene\"><Def handle={Def.instance(\"desk\")} /></Slot>;\n",
      "APPLY_OBJECT_EXPORT_INVALID", "Object 'desk' export 'bad': target is not a declared internal Slot, Component or Field.", 6, 50],
    [head + "const First = defineObject<{}>()({ exports: { root: \"$slot:body\" }, body: () => <Slot key=\"body\" name=\"Body\" /> });\nconst Second = defineObject<{}>()({ exports: { root: \"$slot:other\" }, body: () => <Slot key=\"other\" name=\"Other\" /> });\nconst handle = First.instance(\"desk\");\nexport default <Slot key=\"root\" name=\"Scene\"><First handle={handle} /><Component key=\"reader\" type=\"Synthetic.Device\" fields={{ Link: ref.export(Second.instance(\"desk\"), \"root\") }} /></Slot>;\n",
      "APPLY_OBJECT_EXPORT_UNKNOWN", "Unknown Object export 'desk.root'.", 9, 135],
    [head + "const Def = defineObject<{}>()({ exports: { enabled: fields.ref(\"lamp\", \"Enabled\") }, body: () => <Slot key=\"body\" name=\"Body\"><Component key=\"lamp\" type=\"Synthetic.Device\" fields={{ Enabled: true }} /></Slot> });\nconst handle = Def.instance(\"desk\");\nexport default <Slot key=\"root\" name=\"Scene\"><Def handle={handle} /><Component key=\"reader\" type=\"Synthetic.Device\" fields={{ Label: ref.export(handle, \"enabled\") }} /></Slot>;\n",
      "APPLY_OBJECT_TYPE_MISMATCH", "Object reference type mismatch: expected 'value:string', observed 'field:boolean'.", 8, 134],
    [head + "const Def = defineObject<{}>()({ exports: {}, body: () => <Slot key=\"body\" name=\"Body\"><Component key=\"lamp\" type=\"Synthetic.Device\" /></Slot> });\nexport default <Slot key=\"root\" name=\"Scene\"><Def handle={Def.instance(\"desk\")} /><Component key=\"reader\" type=\"Synthetic.Device\" fields={{ Link: ref.slot(\"desk::ghost\") }} /></Slot>;\n",
      "APPLY_OBJECT_PRIVATE_REFERENCE", "Private Object target '$slot:desk::ghost'; use ref.export(handle, name).", 7, 147],
    [head + "const fields2 = componentFields(\"Synthetic.Device\", catalog);\nexport default <Slot key=\"root\" name=\"Scene\"><Component key=\"parent\" type=\"Synthetic.Other\"><fields2.Field name=\"Enabled\" value={true} /></Component></Slot>;\n",
      "APPLY_FIELD_TYPE_MISMATCH", "Bound Field requires Component 'Synthetic.Device', observed 'Synthetic.Other'.", 7, 113],
  ];
  for (const [source, code, message, line, column] of cases) await diagnostic(source, code, message, line, column);
});

test("module-load helper failures are located and catalog mismatch points to file start", async () => {
  const source = head + "const stale = { ...catalog, formatVersion: \"2\" as any };\n" +
    "const fields2 = componentFields(\"Synthetic.Device\", stale);\n" +
    "export default <Slot key=\"root\" name=\"Scene\"><Component key=\"x\" type=\"Synthetic.Device\"><fields2.Field name=\"Enabled\" value={true} /></Component></Slot>;\n";
  const w = workspace(source);
  try {
    const result = await buildFile(w.entry, { projectRoot: w.root });
    equal(result.exitCode, 1);
    equal(result.errors[0], "APPLY_CATALOG_TYPES_HASH_MISMATCH: Catalog runtime table does not match format/generator/descriptor shape; regenerate catalog types.; TSX " + w.entry + ":7:17");
  } finally { w.cleanup(); }
  const broken = workspace(head + "export default <Slot key=\"root\" name=\"Scene\" />;\n");
  try {
    fs.writeFileSync(broken.declarationPath, "// resoloop-catalog-types: {broken}\nexport {};\n");
    const result = await buildFile(broken.entry, { projectRoot: broken.root });
    equal(result.exitCode, 1);
    equal(result.errors[0], "APPLY_CATALOG_TYPES_HASH_MISMATCH: Catalog declarations/runtime at '" + broken.declarationPath + "' do not match format/generator/contentHash/runtimeSha256; regenerate catalog types.; TSX " + broken.declarationPath + ":1:1");
  } finally { broken.cleanup(); }
});

test("missing runtime catalog is reported at the imported componentFields call", async () => {
  const w = workspace(head + "const missing = componentFields(\"Missing.Type\", catalog);\nexport default <Slot key=\"root\" name=\"Scene\" />;\n");
  try {
    const result = await buildFile(w.entry, { projectRoot: w.root });
    equal(result.exitCode, 1);
    equal(result.errors[0], "APPLY_CATALOG_UNAVAILABLE: Type 'Missing.Type', member '<type>': runtime catalog evidence is missing or unconfirmed.; TSX " + w.entry + ":6:17");
  } finally { w.cleanup(); }
});

test("an existing unknown source remains unknown through module helper forwarding", async () => {
  const w = workspace("import { BuildError } from \"resoloop-jsx\";\nthrow new BuildError(\"APPLY_OBJECT_INVALID\", \"Object 'desk': invalid instance ID.\");\n");
  try {
    const result = await buildFile(w.entry, { projectRoot: w.root });
    equal(result.exitCode, 1);
    equal(result.errors[0], "APPLY_OBJECT_INVALID: Object 'desk': invalid instance ID.; TSX unknown");
  } finally { w.cleanup(); }
});

test("legacy non-Object evaluation keeps its original failure code and message", async () => {
  const w = workspace("import { evaluate } from \"resoloop-jsx\";\nevaluate(null);\nexport default null;\n");
  try {
    const result = await buildFile(w.entry, { projectRoot: w.root });
    equal(result.exitCode, 1);
    equal(result.errors[0], "EVAL_FAILED: failed to load compiled module: The entry module's default export must evaluate to exactly one <Slot> element; got none");
  } finally { w.cleanup(); }
});
