import { test } from "node:test";
import { deepEqual, equal, match, ok } from "node:assert/strict";
import { createHash } from "node:crypto";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import * as ts from "typescript";
import { buildFile } from "../src/cli.js";
import { sourceTransformer } from "../src/source-transform.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
async function build(source: string) {
  const root = fs.mkdtempSync(path.join(packageRoot, ".fix-r1-transform-"));
  const entry = path.join(root, "main.tsx");
  fs.writeFileSync(entry, source);
  try { return { result: await buildFile(entry, { projectRoot: root }), root, entry }; }
  catch (error) { fs.rmSync(root, { recursive: true, force: true }); throw error; }
}

function expectedDocument(root: string, source: string, components: unknown[], slotName = "Scene") {
  return {
    schemaVersion: "1", ownership: { key: "root" }, slot: { name: slotName, key: "root" },
    components, children: [],
    authoring: { projectRoot: root, source, ownershipSource: "root-key" },
  };
}

const ambientSource = 'import {Slot} from "resoloop-jsx"; declare function unused({value}: {value:number}):void; export default <Slot key="root" name="Scene"/>;';
const ambientNamespaceSource = 'import {Slot} from "resoloop-jsx"; declare namespace Ambient { function unused({value}: {value:number}):void; } export default <Slot key="root" name="Scene"/>;';
const overloadSource = 'import {Slot} from "resoloop-jsx"; function unused({value}: {value:number}):void; function unused({value}: {value:number}) {} export default <Slot key="root" name="Scene"/>;';

for (const [label, source] of [["ambient function", ambientSource], ["ambient namespace", ambientNamespaceSource], ["overload signature", overloadSource]] as const) {
  test(`does not register a bodyless ${label}`, async () => {
    const w = await build(source);
    try {
      equal(w.result.exitCode, 0, w.result.errors.join("\n"));
      deepEqual(w.result.errors, []);
      deepEqual(w.result.warnings, []);
      deepEqual(w.result.document, expectedDocument(w.root, "main.tsx", []));
    } finally { fs.rmSync(w.root, { recursive: true, force: true }); }
  });
}

test("keeps ordinary ref.member build output compatible", async () => {
  const source = 'import {Slot,Component,ref} from "resoloop-jsx"; export default <Slot key="root" name="Scene"><Component key="state" type="Any.Type" fields={{Enabled:true}}/><Component key="reader" type="Any.Type" fields={{Target:ref.member("state","Enabled")}}/></Slot>;';
  const w = await build(source);
  try {
    equal(w.result.exitCode, 0, w.result.errors.join("\n"));
    deepEqual(w.result.errors, []);
    deepEqual(w.result.warnings, []);
    deepEqual(w.result.document, expectedDocument(w.root, "main.tsx", [
      { type: "Any.Type", key: "state", fields: { Enabled: true } },
      { type: "Any.Type", key: "reader", fields: { Target: "$member:state.Enabled" } },
    ]));
  } finally { fs.rmSync(w.root, { recursive: true, force: true }); }
});

test("retains source registration for a function declaration with a body", () => {
  const root = fs.mkdtempSync(path.join(packageRoot, ".fix-r1-function-body-"));
  const entry = path.join(root, "main.tsx");
  const source = 'import { Slot, Component } from "resoloop-jsx";\nfunction View({label}: {label:string}) { return <Component key="label" type="Any.Type" fields={{Text:label}} />; }\nexport default <Slot key="root" name="Scene"><View label="Lamp" /></Slot>;\n';
  fs.writeFileSync(entry, source);
  try {
    const program = ts.createProgram([entry], { module: ts.ModuleKind.NodeNext, moduleResolution: ts.ModuleResolutionKind.NodeNext,
      target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, jsxImportSource: "resoloop-jsx", rootDir: root,
      outDir: path.join(root, "dist"), strict: true, skipLibCheck: true });
    deepEqual(ts.getPreEmitDiagnostics(program).map(diagnostic => diagnostic.code), []);
    const sha256 = createHash("sha256").update(source, "utf8").digest("hex");
    let emitted = "";
    const result = program.emit(undefined, (_file, text) => { emitted += text; }, undefined, false,
      sourceTransformer(program, [{ path: entry, sha256, role: "source" }]));
    equal(result.emitSkipped, false);
    ok(/sourceFunction\(View,/.test(emitted), emitted);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test("does not wrap an unrelated exported ObjectDefinition.instance method", async () => {
  const root = fs.mkdtempSync(path.join(packageRoot, ".fix-r1-instance-collision-"));
  const entry = path.join(root, "main.tsx");
  fs.writeFileSync(path.join(root, "helper.ts"), [
    "export interface ObjectDefinition { readonly prefix: string; instance(id: string): string }",
    'export const helper: ObjectDefinition = { prefix: "Scene:", instance(id) { return this.prefix + id; } };',
    "",
  ].join("\n"));
  fs.writeFileSync(entry, 'import { Slot } from "resoloop-jsx";\nimport { helper } from "./helper.js";\nexport default <Slot key="root" name={helper.instance("ordinary")}/>;\n');
  try {
    const result = await buildFile(entry, { projectRoot: root });
    equal(result.exitCode, 0, result.errors.join("\n"));
    deepEqual(result.errors, []);
    deepEqual(result.warnings, []);
    deepEqual(result.document, expectedDocument(root, "main.tsx", [], "Scene:ordinary"));
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test("attributes missing catalog diagnostics for draft componentFields imports", async () => {
  const source = 'import { Slot, componentFields } from "resoloop-jsx/draft";\ncomponentFields("Unknown" as never, null as any);\nexport default <Slot key="root" name="Scene"/>;';
  const w = await build(source);
  try {
    equal(w.result.exitCode, 1);
    match(w.result.errors[0], new RegExp(`APPLY_CATALOG_UNAVAILABLE: .*; TSX ${w.entry.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}:2:1$`));
    deepEqual(w.result.warnings, []);
  } finally { fs.rmSync(w.root, { recursive: true, force: true }); }
});
