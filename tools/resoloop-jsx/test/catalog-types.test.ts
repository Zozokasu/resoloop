import { test } from "node:test";
import { deepEqual, equal, match, ok } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import * as ts from "typescript";
import { buildFile, parseArgs } from "../src/cli.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const fixtures = path.join(packageRoot, "test/fixtures/catalog-types");
const declarationText = fs.readFileSync(path.join(fixtures, "catalog-types.handwritten.d.ts"), { encoding: "utf8" });
function workspace(source = "valid.tsx", declaration = true) {
  const root = fs.mkdtempSync(path.join(packageRoot, ".catalog-types-test-"));
  const entry = path.join(root, "main.tsx");
  fs.writeFileSync(entry, fs.readFileSync(path.join(fixtures, source), { encoding: "utf8" }));
  const types = path.join(root, ".resoloop/catalog-types.d.ts");
  if (declaration) { fs.mkdirSync(path.dirname(types)); fs.writeFileSync(types, declarationText); }
  const catalog = path.join(root, "catalog.json");
  // The TS build compares only identity: this deliberately has no valid catalog semantics.
  fs.writeFileSync(catalog, JSON.stringify({ contentHash: "handwritten-catalog-hash" }));
  return { root, entry, types, catalog, output: path.join(root, "request/bundle.json"),
    cleanup: () => fs.rmSync(root, { recursive: true, force: true }) };
}
function diagnostics(entry: string, types?: string) {
  const program = ts.createProgram(types ? [entry, types] : [entry], {
    jsx: ts.JsxEmit.ReactJSX, jsxImportSource: "resoloop-jsx", module: ts.ModuleKind.NodeNext,
    moduleResolution: ts.ModuleResolutionKind.NodeNext, target: ts.ScriptTarget.ES2022,
    strict: true, skipLibCheck: true, noEmit: true, rootDir: path.dirname(entry),
  });
  return ts.getPreEmitDiagnostics(program);
}

test("optional root declaration types literals, nullable/tuple/enum/reference values and keeps open/wide/unknown fallback", async () => {
  const w = workspace();
  try {
    const result = await buildFile(w.entry);
    equal(result.exitCode, 0, result.errors.join("\n"));
    deepEqual(result.document!.components[0].initialFields, { Amount: 3 });
    equal(result.document!.components[0].fields!.Target, "$slot:root");
    equal(result.document!.components[3].fields!.Amount, "legacy string");
  } finally { w.cleanup(); }
});

test("handwritten error oracle rejects known literal names/values without widening, at exact source positions", () => {
  const w = workspace("invalid.tsx");
  try {
    const errors = diagnostics(w.entry, w.types);
    deepEqual(errors.map(d => ({ code: d.code, ...d.file!.getLineAndCharacterOfPosition(d.start!) })), [
      { code: 2353, line: 3, character: 4 },
      { code: 2322, line: 8, character: 4 },
      { code: 2322, line: 13, character: 4 },
      { code: 2322, line: 18, character: 4 },
      { code: 2322, line: 23, character: 4 },
      { code: 2322, line: 28, character: 4 },
      { code: 2322, line: 33, character: 4 },
      { code: 2322, line: 38, character: 4 },
    ]);
    match(ts.flattenDiagnosticMessageText(errors[0].messageText, "\n"), /Missing/);
    match(ts.flattenDiagnosticMessageText(errors[1].messageText, "\n"), /number/);
  } finally { w.cleanup(); }
});

test("without generated declarations current arbitrary JSON fields stay accepted", () => {
  const w = workspace("invalid.tsx", false);
  try { deepEqual(diagnostics(w.entry), []); } finally { w.cleanup(); }
});

test("draft marker retains known literal value checks while allowing an omitted key", () => {
  const w = workspace();
  try {
    fs.writeFileSync(w.entry, 'import { Component } from "resoloop-jsx/draft";\nexport const wrong = (\n  <Component type="Synthetic.Strict" fields={{\n    Amount: "wrong",\n  }} />\n);\n');
    const errors = diagnostics(w.entry, w.types);
    deepEqual(errors.map(d => ({ code: d.code, ...d.file!.getLineAndCharacterOfPosition(d.start!) })),
      [{ code: 2322, line: 3, character: 4 }]);
  } finally { w.cleanup(); }
});

test("extra keys in a variable follow ordinary TypeScript structural assignability", () => {
  const w = workspace();
  try {
    fs.writeFileSync(w.entry, 'import { Component } from "resoloop-jsx";\nconst values = { Amount: 1, Extra: true };\nexport const structural = <Component key="structural" type="Synthetic.Strict" fields={values} />;\n');
    deepEqual(diagnostics(w.entry, w.types), []);
  } finally { w.cleanup(); }
});

test("matched hash includes declaration in bundle source snapshot without catalog semantics", async () => {
  const w = workspace();
  try {
    const result = await buildFile(w.entry, { bundle: true, catalog: w.catalog, buildId: "request", output: w.output });
    equal(result.exitCode, 0, result.errors.join("\n"));
    const bundle = JSON.parse(fs.readFileSync(w.output, { encoding: "utf8" }));
    ok(bundle.inputs.files.some((f: any) => f.path === w.types && f.role === "source"));
  } finally { w.cleanup(); }
});

test("without declarations bundle identity comparison stays opt-in", async () => {
  const w = workspace("valid.tsx", false);
  try {
    fs.writeFileSync(w.catalog, "{}");
    const result = await buildFile(w.entry, { bundle: true, catalog: w.catalog, buildId: "request", output: w.output });
    equal(result.exitCode, 0, result.errors.join("\n"));
  } finally { w.cleanup(); }
});

test("declaration outside entry directory loads from explicit project root and only contentHash is compared", async () => {
  const w = workspace();
  try {
    fs.mkdirSync(path.join(w.root, "source"));
    const nestedEntry = path.join(w.root, "source/main.tsx");
    fs.writeFileSync(nestedEntry, fs.readFileSync(w.entry, { encoding: "utf8" }));
    fs.writeFileSync(w.types, declarationText.replace('"formatVersion":"1"', '"formatVersion":"future"')
      .replace('"generatorVersion":"1"', '"generatorVersion":"future"'));
    const result = await buildFile(nestedEntry, { projectRoot: w.root, bundle: true,
      catalog: w.catalog, buildId: "request", output: w.output });
    equal(result.exitCode, 0, result.errors.join("\n"));
  } finally { w.cleanup(); }
});

for (const [name, header, catalog] of [
  ["mismatch", declarationText.replace("handwritten-catalog-hash", "stale"), '{"contentHash":"handwritten-catalog-hash"}'],
  ["missing header", declarationText.replace(/^.*\n/, ""), '{"contentHash":"handwritten-catalog-hash"}'],
  ["malformed header", declarationText.replace(/^.*\n/, "// resoloop-catalog-types: {broken}\n"), '{"contentHash":"handwritten-catalog-hash"}'],
  ["nonstring catalog hash", declarationText, '{"contentHash":12}'],
] as const) test(`hash ${name} fails before output reservation/publication`, async () => {
  const w = workspace();
  try {
    fs.writeFileSync(w.types, header); fs.writeFileSync(w.catalog, catalog);
    const result = await buildFile(w.entry, { bundle: true, catalog: w.catalog, buildId: "request", output: w.output });
    equal(result.exitCode, 1); match(result.errors.join("\n"), /APPLY_CATALOG_TYPES_HASH_MISMATCH/);
    ok(!fs.existsSync(path.dirname(w.output)));
  } finally { w.cleanup(); }
});

test("UTF-8 BOM before catalog JSON still matches the declaration contentHash", async () => {
  const w = workspace();
  try {
    fs.writeFileSync(w.catalog, "\uFEFF" + fs.readFileSync(w.catalog, { encoding: "utf8" }));
    const result = await buildFile(w.entry, { bundle: true, catalog: w.catalog, buildId: "request", output: w.output });
    equal(result.exitCode, 0, result.errors.join("\n"));
  } finally { w.cleanup(); }
});

test("custom reference declarations participate in hash checks and conflicting files fail", async () => {
  const w = workspace();
  try {
    const custom = path.join(w.root, "custom-types.d.ts");
    fs.writeFileSync(custom, declarationText.replace("handwritten-catalog-hash", "conflicting"));
    fs.writeFileSync(w.entry, '/// <reference path="./custom-types.d.ts" />\n' + fs.readFileSync(w.entry, { encoding: "utf8" }));
    const result = await buildFile(w.entry, { bundle: true, catalog: w.catalog, buildId: "request", output: w.output });
    equal(result.exitCode, 1); match(result.errors.join("\n"), /APPLY_CATALOG_TYPES_HASH_MISMATCH/);
    ok(!fs.existsSync(path.dirname(w.output)));
    fs.rmSync(w.types);
    fs.writeFileSync(custom, declarationText);
    const matched = await buildFile(w.entry, { bundle: true, catalog: w.catalog, buildId: "request", output: w.output });
    equal(matched.exitCode, 0, matched.errors.join("\n"));
    const bundle = JSON.parse(fs.readFileSync(w.output, { encoding: "utf8" }));
    ok(bundle.inputs.files.some((f: any) => f.path === custom));
  } finally { w.cleanup(); }
});

test("catalog/build-id arguments retain bundle-only behavior", () => {
  ok(parseArgs(["build", "main.tsx", "-o", "out.json", "--catalog", "catalog.json"]).error);
});

test("400 additional catalog types and literal uses typecheck without exponential mapped-union expansion", () => {
  const w = workspace();
  try {
    const entries = Array.from({ length: 400 }, (_, i) => `"Synthetic.Perf${i}": { members: { Amount: number; Label: string; }; membersComplete: true; };`).join("\n");
    fs.writeFileSync(w.types, declarationText + '\ndeclare module "resoloop-jsx" { interface CatalogComponentRegistry {\n' + entries + "\n} }\n");
    fs.writeFileSync(w.entry, fs.readFileSync(w.entry, { encoding: "utf8" }) +
      Array.from({ length: 400 }, (_, i) => `\nexport const probe${i} = <Component key="perf${i}" type="Synthetic.Perf${i}" fields={{ Amount: ${i}, Label: "value" }} />;`).join("\n"));
    const start = Date.now();
    deepEqual(diagnostics(w.entry, w.types), []);
    process.stdout.write(`catalog-types performance: 403 types, 400 literal uses, ${Date.now() - start}ms\n`);
  } finally { w.cleanup(); }
});

test("<Field> stays loosely typed: a nonexistent member name and an unrelated value type pass tsc (the catalog check in C# decides)", () => {
  const w = workspace();
  try {
    fs.writeFileSync(w.entry, 'import { Component, Field } from "resoloop-jsx";\nexport const loose = (\n  <Component key="loose" type="Synthetic.Strict">\n    <Field name="Missing" value={2} />\n    <Field name="Amount" value="not a number" />\n  </Component>\n);\n');
    deepEqual(diagnostics(w.entry, w.types), []);
  } finally { w.cleanup(); }
});
