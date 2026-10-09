import { test } from "node:test";
import { deepEqual, equal, match, ok } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { createHash } from "node:crypto";
import { buildFile } from "../src/cli.js";
import { objectTestRuntimeTable } from "./object-test-helpers.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const sha = (text: string) => createHash("sha256").update(text, "utf8").digest("hex");
const registry = 'declare module "resoloop-jsx" { interface CatalogComponentRegistry { "Synthetic.Device": { membersComplete: false; members: { Enabled: boolean; Count: number; Label: string; Weight: number; Target: import("resoloop-jsx").FieldRef<boolean, "boolean"> | import("resoloop-jsx").UncheckedReference | null; Uncertain: import("resoloop-jsx").JsonValue; UnconfirmedTarget: string | null }; memberMetadata: { Enabled: { kind: "field"; confirmed: true; scalar: "boolean"; fieldInterface: string }; Count: { kind: "field"; confirmed: true; scalar: "int32"; fieldInterface: string }; Label: { kind: "field"; confirmed: true; scalar: "string"; fieldInterface: string }; Weight: { kind: "field"; confirmed: true; scalar: "single"; fieldInterface: string }; Target: { kind: "reference"; confirmed: true; scalar: null; fieldInterface: null }; Uncertain: { kind: "other"; confirmed: false; scalar: null; fieldInterface: null }; UnconfirmedTarget: { kind: "reference"; confirmed: false; scalar: null; fieldInterface: null } } } } }';

function workspace(source: string) {
  const root = fs.mkdtempSync(path.join(packageRoot, ".object-build-"));
  const catalog = JSON.stringify(objectTestRuntimeTable());
  const runtime = `// resoloop-catalog-fields: {"formatVersion":"1","contentHash":"handwritten-runtime-hash","generatorVersion":"3"}\nimport type { CatalogRuntimeTable } from "resoloop-jsx";\nexport const catalog = ${catalog} as const satisfies CatalogRuntimeTable;\n`;
  const declaration = `// resoloop-catalog-types: {"formatVersion":"1","contentHash":"handwritten-runtime-hash","generatorVersion":"3","runtimeFile":"catalog-fields.ts","runtimeSha256":"${sha(runtime)}"}\nexport {};\n`;
  fs.mkdirSync(path.join(root, ".resoloop"));
  fs.writeFileSync(path.join(root, ".resoloop/catalog-fields.ts"), runtime);
  fs.writeFileSync(path.join(root, ".resoloop/catalog-types.d.ts"), declaration);
  const entry = path.join(root, "main.tsx");
  const [imports, ...rest] = source.split("\n");
  fs.writeFileSync(entry, [imports, registry, ...rest].join("\n"));
  return { root, entry, cleanup: () => fs.rmSync(root, { recursive: true, force: true }) };
}

function outsideWorkspace(source: string) {
  const root = fs.mkdtempSync(path.join(packageRoot, ".object-build-outside-"));
  const projectRoot = path.join(root, "other");
  const entry = path.join(root, "project", "content", "main.tsx");
  const catalogDir = path.join(projectRoot, ".resoloop");
  const runtime = `// resoloop-catalog-fields: {"formatVersion":"1","contentHash":"handwritten-runtime-hash","generatorVersion":"3"}\nimport type { CatalogRuntimeTable } from "resoloop-jsx";\nexport const catalog = ${JSON.stringify(objectTestRuntimeTable())} as const satisfies CatalogRuntimeTable;\n`;
  const declaration = `// resoloop-catalog-types: {"formatVersion":"1","contentHash":"handwritten-runtime-hash","generatorVersion":"3","runtimeFile":"catalog-fields.ts","runtimeSha256":"${sha(runtime)}"}\nexport {};\n`;
  fs.mkdirSync(catalogDir, { recursive: true });
  fs.mkdirSync(path.dirname(entry), { recursive: true });
  fs.writeFileSync(path.join(catalogDir, "catalog-fields.ts"), runtime);
  fs.writeFileSync(path.join(catalogDir, "catalog-types.d.ts"), declaration);
  fs.writeFileSync(entry, source);
  return { root, projectRoot, entry, cleanup: () => fs.rmSync(root, { recursive: true, force: true }) };
}

const source = `import { Slot, Component, defineObject, componentFields, ref } from "resoloop-jsx";
import { catalog } from "./.resoloop/catalog-fields.js";
const fields = componentFields("Synthetic.Device", catalog);
const Def = defineObject<{}>()({
  exports: { enabled: fields.ref("lamp", "Enabled") },
  body: () => <Slot key="body" name="Body"><Component key="lamp" type="Synthetic.Device" fields={{ Enabled: true }} /></Slot>,
});
const handle = Def.instance("desk");
export default <Slot key="root" name="Scene"><Def handle={handle} /><Component key="reader" type="Synthetic.Device" fields={{ Target: ref.export(handle, "enabled") }} /></Slot>;
`;

test("normal CLI build uses project catalog and expands a typed Object", async () => {
  const w = workspace(source);
  try {
    const result = await buildFile(w.entry, { projectRoot: w.root });
    equal(result.exitCode, 0, result.errors.join("\n"));
    deepEqual(result.document, {
      schemaVersion: "1", ownership: { key: "root" }, slot: { name: "Scene", key: "root" },
      components: [{ type: "Synthetic.Device", key: "reader", fields: { Target: "$member:desk::lamp.Enabled" } }],
      children: [{ slot: { name: "Body", key: "desk::body" }, components: [
        { type: "Synthetic.Device", key: "desk::lamp", fields: { Enabled: true } },
      ] }],
      authoring: { projectRoot: w.root, source: "main.tsx", ownershipSource: "root-key" },
    });
  } finally { w.cleanup(); }
});

test("normal emitted entry uses project-relative output paths when imports widen the program", async () => {
  const w = workspace(source.replace('"./.resoloop/catalog-fields.js"', '"../.resoloop/catalog-fields.js"'));
  try {
    fs.mkdirSync(path.join(w.root, "content"));
    const nested = path.join(w.root, "content/main.tsx");
    fs.renameSync(w.entry, nested);
    const result = await buildFile(nested, { projectRoot: w.root });
    equal(result.exitCode, 0, result.errors.join("\n"));
    ok(result.document?.components?.[0]?.fields?.Target === "$member:desk::lamp.Enabled");
    deepEqual(result.document?.authoring, { projectRoot: w.root, source: "content/main.tsx", ownershipSource: "root-key" });
  } finally { w.cleanup(); }
});

test("Object bundle snapshots the imported catalog pair and retains a handwritten TSX source location", async () => {
  const w = workspace(`import { Slot, Component, defineObject, componentFields } from "resoloop-jsx";
import { catalog } from "./.resoloop/catalog-fields.js";
const fields = componentFields("Synthetic.Device", catalog);
const Def = defineObject<{}>()({
  exports: { enabled: fields.ref("lamp", "Enabled") },
  body: () => <Slot key="body" name="Body">
    <Component key="lamp" type="Synthetic.Device" fields={{ Enabled: true }} />
  </Slot>,
});
export default <Slot key="root" name="Scene"><Def handle={Def.instance("desk")} /></Slot>;
`);
  const catalogFile = path.join(w.root, "catalog.json");
  const catalogText = '{"contentHash":"handwritten-runtime-hash"}\n';
  fs.writeFileSync(catalogFile, catalogText);
  const output = path.join(w.root, "R1", "bundle.data");
  try {
    const result = await buildFile(w.entry, { bundle: true, catalog: catalogFile, buildId: "R1", output });
    equal(result.exitCode, 0, result.errors.join("\n"));
    const bundle = JSON.parse(fs.readFileSync(output, { encoding: "utf8" }));
    deepEqual(JSON.parse(bundle.ir.text), {
      schemaVersion: "1", ownership: { key: "root" }, slot: { key: "root", name: "Scene" }, components: [],
      children: [{ slot: { key: "desk::body", name: "Body" }, components: [
        { key: "desk::lamp", type: "Synthetic.Device", fields: { Enabled: true } },
      ] }],
      authoring: { projectRoot: w.root, source: "main.tsx", ownershipSource: "root-key" },
    });
    equal(bundle.catalog.text, catalogText);
    const catalogInput = bundle.inputs.files.find((row: any) => row.role === "catalog");
    equal(catalogInput.path, catalogFile); equal(catalogInput.sha256, sha(catalogText));
    const runtimeFile = path.join(w.root, ".resoloop", "catalog-fields.ts");
    const declarationFile = path.join(w.root, ".resoloop", "catalog-types.d.ts");
    const runtime = fs.readFileSync(runtimeFile, { encoding: "utf8" }), declaration = fs.readFileSync(declarationFile, { encoding: "utf8" });
    const runtimeHeader = JSON.parse(runtime.split("\n", 1)[0].slice("// resoloop-catalog-fields: ".length));
    const declarationHeader = JSON.parse(declaration.split("\n", 1)[0].slice("// resoloop-catalog-types: ".length));
    equal(runtimeHeader.contentHash, "handwritten-runtime-hash");
    equal(declarationHeader.contentHash, "handwritten-runtime-hash");
    equal(declarationHeader.runtimeFile, "catalog-fields.ts"); equal(declarationHeader.runtimeSha256, sha(runtime));
    for (const [file, text] of [[runtimeFile, runtime], [declarationFile, declaration]] as const) {
      const input = bundle.inputs.files.find((row: any) => row.path === file);
      ok(input); equal(input.role, "source"); equal(input.sha256, sha(text));
      const mapped = JSON.parse(bundle.map.text).sources.find((row: any) => row.path === file);
      ok(mapped); equal(mapped.sha256, sha(text));
    }
    const map = JSON.parse(bundle.map.text);
    const field = map.entries.find((row: any) => row.jsonPath === '$.children[0].components[0].fields["Enabled"]');
    ok(field); equal(field.valueSource.status, "known"); equal(field.valueSource.file, w.entry);
    equal(field.valueSource.range.start.line, 8); equal(field.valueSource.range.start.column, 70);
  } finally { w.cleanup(); }
});

test("explicit outside projectRoot keeps legacy authoring paths and external catalog imports retain TS6059", async () => {
  const success = outsideWorkspace('import { Slot } from "resoloop-jsx";\nexport default <Slot key="root" name="Scene" />;\n');
  try {
    const result = await buildFile(success.entry, { projectRoot: success.projectRoot });
    equal(result.exitCode, 0, result.errors.join("\n"));
    deepEqual(result.document, {
      schemaVersion: "1", ownership: { key: "root" }, slot: { name: "Scene", key: "root" },
      components: [], children: [],
      authoring: { projectRoot: success.projectRoot, source: "../project/content/main.tsx", ownershipSource: "root-key" },
    });
  } finally { success.cleanup(); }

  const importing = outsideWorkspace('import { Slot } from "resoloop-jsx";\nimport { catalog } from "../../other/.resoloop/catalog-fields.js";\nvoid catalog;\nexport default <Slot key="root" name="Scene" />;\n');
  try {
    const result = await buildFile(importing.entry, { projectRoot: importing.projectRoot });
    equal(result.exitCode, 2);
    match(result.errors[0], /TS6059/);
    match(result.errors[0], /catalog-fields\.ts/);
  } finally { importing.cleanup(); }
});
