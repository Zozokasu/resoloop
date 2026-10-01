import { test } from "node:test";
import { equal, deepEqual, ok, match } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile, parseArgs } from "../src/cli.js";
import { hashText } from "../src/input-snapshot.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const originalCatalog = path.join(packageRoot, "test/fixtures/catalog-v11/catalog.synthetic.json");
const entryText = `import { Slot, Component } from "resoloop-jsx";
import { amount } from "./config.js";
export default <Slot key="root" name="Synthetic"><Component key="holder" type="Synthetic.Holder" fields={{ Amount: amount }} /></Slot>;`;
function workspace() {
  const root = fs.mkdtempSync(path.join(packageRoot, ".s2-4-test-"));
  const entry = path.join(root, "main.tsx"), imported = path.join(root, "config.ts"), catalog = path.join(root, "catalog.json");
  fs.writeFileSync(entry, entryText); fs.writeFileSync(imported, "export const amount = 1;\n");
  fs.writeFileSync(catalog, fs.readFileSync(originalCatalog, { encoding: "utf8" }));
  return { root, entry, imported, catalog, output: path.join(root, "R1/bundle.data"),
    cleanup: () => fs.rmSync(root, { recursive: true, force: true }) };
}

test("bundle publishes committed payloads only after success, with handwritten IR/type/map expectations", async () => {
  const w = workspace();
  try {
    const result = await buildFile(w.entry, { bundle: true, catalog: w.catalog, buildId: "R1", output: w.output });
    equal(result.exitCode, 0, result.errors.join("\n"));
    const bundle = JSON.parse(fs.readFileSync(w.output));
    equal(bundle.kind, "resoloop-build-bundle"); equal(bundle.bundleVersion, "1"); equal(bundle.buildId, "R1"); equal(bundle.completion, "committed");
    deepEqual(bundle.buildStages, { typecheck: "passed", emit: "passed", evaluate: "passed", inputs: "passed" });
    deepEqual(bundle.usedTypes, ["Synthetic.Holder"]);
    deepEqual(JSON.parse(bundle.ir.text), { schemaVersion: "1", ownership: { key: "root" },
      slot: { key: "root", name: "Synthetic" }, components: [{ key: "holder", type: "Synthetic.Holder", fields: { Amount: 1 } }], children: [],
      authoring: { projectRoot: w.root, source: "main.tsx", ownershipSource: "root-key" } });
    for (const name of ["ir", "map", "catalog"]) equal(bundle[name].sha256, hashText(bundle[name].text));
    equal(bundle.catalog.text, fs.readFileSync(w.catalog, { encoding: "utf8" }));
    const map = JSON.parse(bundle.map.text);
    equal(map.version, "1"); equal(map.buildId, "R1"); equal(map.irSha256, bundle.ir.sha256);
    deepEqual(map.entries.map((entry: any) => ({ jsonPath: entry.jsonPath })), [
      { jsonPath: "$.slot" },
      { jsonPath: '$.slot["key"]' },
      { jsonPath: '$.slot["name"]' },
      { jsonPath: "$.components[0]" },
      { jsonPath: "$.components[0].type" },
      { jsonPath: '$.components[0].fields["Amount"]' },
    ]);
    equal(bundle.inputs.status, "complete");
    for (const file of [w.entry, w.imported, w.catalog]) {
      const record = bundle.inputs.files.find((f: any) => f.path === file);
      ok(record); equal(record.sha256, hashText(fs.readFileSync(file, { encoding: "utf8" })));
    }
    ok(map.sources.some((f: any) => f.path === w.imported));
    deepEqual(fs.readdirSync(path.dirname(w.output)), ["bundle.data"]);
  } finally { w.cleanup(); }
});

test("failed rebuild R2 cannot publish or reuse R1", async () => {
  const w = workspace();
  try {
    const success = await buildFile(w.entry, { bundle: true, catalog: w.catalog, buildId: "R1", output: w.output });
    equal(success.exitCode, 0, success.errors.join("\n")); const old = fs.readFileSync(w.output, { encoding: "utf8" });
    fs.writeFileSync(w.entry, "export default 123;");
    const output2 = path.join(w.root, "R2/bundle.data");
    const fail = await buildFile(w.entry, { bundle: true, catalog: w.catalog, buildId: "R2", output: output2 });
    equal(fail.exitCode, 1); ok(!fs.existsSync(output2)); equal(fs.readFileSync(w.output, { encoding: "utf8" }), old);
    const reuse = await buildFile(w.entry, { bundle: true, catalog: w.catalog, buildId: "R2", output: w.output });
    equal(reuse.exitCode, 1); match(reuse.errors.join("\n"), /cannot be reused/); equal(fs.readFileSync(w.output, { encoding: "utf8" }), old);
  } finally { w.cleanup(); }
});

test("bundle refuses an existing empty output directory", async () => {
  const w = workspace();
  try {
    fs.mkdirSync(path.dirname(w.output));
    const result = await buildFile(w.entry, { bundle: true, catalog: w.catalog, buildId: "R1", output: w.output });
    equal(result.exitCode, 1); ok(!fs.existsSync(w.output));
  } finally { w.cleanup(); }
});

for (const [name, code] of [
  ["dynamic import", 'void import("./config.js");'],
  ["Node builtin", 'import * as fs from "fs";'],
  ["node prefix", 'import * as fs from "node:fs";'],
  ["external package", 'import * as ts from "typescript";'],
  ["external package through relative path", 'import * as ts from "../node_modules/typescript/lib/typescript.js";'],
  ["require", 'declare const require: any; require("fs");'],
] as const) test(`unknown inputs never commit: ${name}`, async () => {
  const w = workspace();
  try {
    fs.writeFileSync(w.imported, code + "\nexport const amount = 1;\n");
    const result = await buildFile(w.entry, { bundle: true, catalog: w.catalog, buildId: "R1", output: w.output });
    equal(result.exitCode, 1); match(result.errors.join("\n"), /inputUnknown/); ok(!fs.existsSync(w.output));
  } finally { w.cleanup(); }
});

for (const target of ["entry", "import", "catalog"] as const) test(`input changed during evaluation never commits: ${target}`, async () => {
  const w = workspace();
  try {
    const file = target === "entry" ? w.entry : target === "import" ? w.imported : w.catalog;
    // Injection only: E3 does not analyze calls or environmental dependencies.
    (globalThis as any).__s24_mutate = () => fs.writeFileSync(file, "changed");
    fs.writeFileSync(w.entry, 'declare const __s24_mutate: () => void; __s24_mutate();\n' + entryText);
    const result = await buildFile(w.entry, { bundle: true, catalog: w.catalog, buildId: "R1", output: w.output });
    equal(result.exitCode, 1); match(result.errors.join("\n"), /inputChanged/); ok(!fs.existsSync(w.output));
    deepEqual(fs.readdirSync(path.dirname(w.output)), []);
  } finally { delete (globalThis as any).__s24_mutate; w.cleanup(); }
});

test("bundle TypeScript failure publishes no bundle and preserves legacy exit 2", async () => {
  const w = workspace();
  try {
    fs.writeFileSync(w.imported, 'export const amount: number = "wrong";');
    const result = await buildFile(w.entry, { bundle: true, catalog: w.catalog, buildId: "R1", output: w.output });
    equal(result.exitCode, 2); ok(!fs.existsSync(w.output));
  } finally { w.cleanup(); }
});

test("bundle arguments require catalog/request and stay opt-in", () => {
  for (const flags of [["--bundle"], ["--bundle", "--catalog", "c"], ["--build-id", "R"], ["--catalog", "c"], ["--bundle", "--catalog=c", "--build-id="]])
    ok(parseArgs(["build", "x.tsx", "-o", "out", ...flags]).error);
  deepEqual(parseArgs(["build", "x.tsx", "-o", "out", "--bundle", "--catalog=c", "--build-id=R"]).args,
    { draft: false, entry: "x.tsx", output: "out", bundle: true, catalog: "c", buildId: "R" });
});

test("Windows rename failure does not publish and removes its temporary file", { skip: path.sep !== "\\" }, async () => {
  const w = workspace();
  try {
    const output = path.join(w.root, "R1", "invalid:name.json");
    const result = await buildFile(w.entry, { bundle: true, catalog: w.catalog, buildId: "R1", output });
    equal(result.exitCode, 1); ok(!fs.existsSync(output));
    deepEqual(fs.readdirSync(path.dirname(output)), []);
  } finally { w.cleanup(); }
});
