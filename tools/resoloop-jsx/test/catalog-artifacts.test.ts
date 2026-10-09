import { test } from "node:test";
import { deepEqual, equal, match, ok } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { createHash } from "node:crypto";
import * as ts from "typescript";
import { buildFile } from "../src/cli.js";
import { catalogRuntimeTables } from "../src/catalog-declarations.js";
import { buildErrorLocation } from "../src/source-map.js";

const hash = (text: string): string => createHash("sha256").update(text.replace(/\r\n/g, "\n"), "utf8").digest("hex");
const empty = '{"formatVersion":"1","generatorVersion":"3","contentHash":"handwritten-hash","types":{}}';
const dir = () => fs.mkdtempSync(path.join(process.cwd(), ".catalog-artifacts-"));
function pair(body = empty, runtimeName = "catalog-fields.ts", declarationName = "catalog-types.d.ts") {
  const root = dir();
  const runtime = `// resoloop-catalog-fields: {"formatVersion":"1","contentHash":"handwritten-hash","generatorVersion":"3"}\nimport type { CatalogRuntimeTable } from "resoloop-jsx";\nexport const catalog = ${body} as const satisfies CatalogRuntimeTable;\n`;
  const declaration = `// resoloop-catalog-types: {"formatVersion":"1","contentHash":"handwritten-hash","generatorVersion":"3","runtimeFile":"${runtimeName}","runtimeSha256":"${hash(runtime)}"}\nexport {};\n`;
  const runtimePath = path.join(root, runtimeName), declarationPath = path.join(root, declarationName);
  fs.writeFileSync(runtimePath, runtime); fs.writeFileSync(declarationPath, declaration);
  return { root, runtimePath, declarationPath, runtime, declaration, cleanup: () => fs.rmSync(root, { recursive: true, force: true }) };
}
function rewriteRuntime(p: ReturnType<typeof pair>, runtime: string) {
  p.runtime = runtime;
  p.declaration = p.declaration.replace(/("runtimeSha256":")[0-9a-f]{64}/, `$1${hash(runtime)}`);
  fs.writeFileSync(p.runtimePath, runtime); fs.writeFileSync(p.declarationPath, p.declaration);
}
function program(files: string[]) {
  return ts.createProgram(files, { target: ts.ScriptTarget.ES2022, module: ts.ModuleKind.NodeNext,
    moduleResolution: ts.ModuleResolutionKind.NodeNext, strict: true, skipLibCheck: true, noEmit: true });
}
function inspect(files: string[], catalog?: string) { return catalogRuntimeTables(program(files), catalog); }
function rejects(files: string[], catalog?: string, file = files[0]) {
  try { inspect(files, catalog); throw new Error("expected catalog artifact mismatch"); }
  catch (error) {
    if (error instanceof Error && error.message === "expected catalog artifact mismatch") throw error;
    equal((error as any).code, "APPLY_CATALOG_TYPES_HASH_MISMATCH");
    equal(buildErrorLocation(error as object).replace(/\\/g, "/"), `${file}:1:1`.replace(/\\/g, "/"));
  }
}

test("accepts LF and equivalent CRLF pairs plus a declaration-only header", () => {
  const p = pair();
  try {
    equal(inspect([p.runtimePath, p.declarationPath]).length, 1);
    fs.writeFileSync(p.runtimePath, p.runtime.replace(/\n/g, "\r\n"));
    equal(fs.readFileSync(p.declarationPath, { encoding: "utf8" }), p.declaration);
    equal(inspect([p.runtimePath, p.declarationPath]).length, 1);
    const standalone = path.join(p.root, "only.d.ts");
    fs.writeFileSync(standalone, '// resoloop-catalog-types: {"formatVersion":"1","contentHash":"handwritten-hash","generatorVersion":"3","runtimeFile":"missing.ts","runtimeSha256":"' + "a".repeat(64) + '"}\nexport {};\n');
    deepEqual(inspect([standalone]), []);
    fs.writeFileSync(standalone, '// resoloop-catalog-types: {"formatVersion":"1","contentHash":"handwritten-hash","generatorVersion":"3"}\nexport {};\n');
    deepEqual(inspect([standalone]), []);
  } finally { p.cleanup(); }
});

test("preserves computed __proto__ type, alias and member keys and literal string content", () => {
  const member = '{"kind":"other","catalogKind":"__proto__","confirmed":true,"memberType":"literal \\"__proto__\\": text","valueType":null,"targetType":null,"scalar":null,"fieldInterface":null,"fieldTarget":null}';
  const entry = `{"canonicalType":"__proto__","confirmed":true,"membersComplete":true,"members":{["__proto__"]:${member}}}`;
  const body = `{"formatVersion":"1","generatorVersion":"3","contentHash":"handwritten-hash","types":{["__proto__"]:${entry},"ProtoAlias":${entry}}}`;
  const p = pair(body);
  try {
    const [table] = inspect([p.runtimePath, p.declarationPath]);
    ok(Object.hasOwn(table.types, "__proto__")); ok(Object.hasOwn(table.types["__proto__"].members, "__proto__"));
    equal(table.types["__proto__"].canonicalType, "__proto__");
    equal(table.types.ProtoAlias.canonicalType, "__proto__");
    equal(table.types["__proto__"].members["__proto__"].memberType, 'literal "__proto__": text');
  } finally { p.cleanup(); }
  const canonicalEntry = (canonicalType: string) => `{"canonicalType":"${canonicalType}","confirmed":true,"membersComplete":true,"members":{["__proto__"]:${member}}}`;
  const aliasBody = `{"formatVersion":"1","generatorVersion":"3","contentHash":"handwritten-hash","types":{"ProtoCanonical":${canonicalEntry("ProtoCanonical")},["__proto__"]:${canonicalEntry("ProtoCanonical")}}}`;
  const alias = pair(aliasBody);
  try {
    const [table] = inspect([alias.runtimePath, alias.declarationPath]);
    ok(Object.hasOwn(table.types, "__proto__")); ok(Object.hasOwn(table.types["__proto__"].members, "__proto__"));
    equal(table.types["__proto__"].canonicalType, "ProtoCanonical");
  } finally { alias.cleanup(); }
});

for (const [label, body] of [
  ["computed other key", empty.replace('"types":{}', '"types":{["other"]:{}}')],
  ["computed identifier", empty.replace('"types":{}', '"types":{[name]:{}}')],
  ["spread", empty.replace('"types":{}', '"types":{...extra}')],
  ["duplicate object key", empty.replace('"types":{}', '"types":{},"types":{}')],
] as const) test(`rejects non-JSON literal: ${label}`, () => {
  const p = pair(body); try { rejects([p.runtimePath, p.declarationPath], undefined, p.runtimePath); } finally { p.cleanup(); }
});

for (const [label, edit] of [
  ["call expression", (s: string) => s.replace(empty, "makeTable()")],
  ["side-effect import", (s: string) => s.replace('import type { CatalogRuntimeTable } from "resoloop-jsx";', 'import "resoloop-jsx";')],
  ["extra executable statement", (s: string) => s.replace("export const catalog", "void 0;\nexport const catalog")],
  ["dynamic getter", (s: string) => s.replace('"types":{}', '"types":{get value(){return 1}}')],
  ["runtime import binding", (s: string) => s.replace("import type { CatalogRuntimeTable }", "import { CatalogRuntimeTable }")],
  ["forged const wrapper", (s: string) => s.replace(" as const satisfies", " as { } satisfies")],
] as const) test(`rejects AST whitelist violation with matching byte hash: ${label}`, () => {
  const p = pair();
  try { const changed = edit(p.runtime); ok(changed !== p.runtime); rewriteRuntime(p, changed); rejects([p.runtimePath, p.declarationPath], undefined, p.runtimePath); }
  finally { p.cleanup(); }
});

test("rejects one-byte edits, BOM, lone CR, malformed body identity and catalog identity", () => {
  const p = pair();
  const baseline = p.runtime;
  try {
    fs.writeFileSync(p.runtimePath, baseline.replace("handwritten-hash", "handwritxen-hash"));
    rejects([p.runtimePath, p.declarationPath], undefined, p.runtimePath);
    fs.writeFileSync(p.runtimePath, "\uFEFF" + baseline); rejects([p.runtimePath, p.declarationPath], undefined, p.runtimePath);
    fs.writeFileSync(p.runtimePath, baseline.replace("\n", "\r")); rejects([p.runtimePath, p.declarationPath], undefined, p.runtimePath);
    rewriteRuntime(p, baseline);
    const bodyOnly = baseline.replace(`export const catalog = ${empty}`, `export const catalog = ${empty.replace('"contentHash":"handwritten-hash"', '"contentHash":"stale-hash"')}`);
    ok(bodyOnly !== baseline); equal(bodyOnly.split("\n", 1)[0], baseline.split("\n", 1)[0]);
    ok(bodyOnly.includes('"contentHash":"stale-hash"'));
    rewriteRuntime(p, bodyOnly);
    rejects([p.runtimePath, p.declarationPath], undefined, p.runtimePath);
    rewriteRuntime(p, baseline); rejects([p.runtimePath, p.declarationPath], '{"contentHash":"other"}', p.declarationPath);
  } finally { p.cleanup(); }
});

test("rejects stale companion alongside good one and accepts multiple complete tables", () => {
  const p = pair();
  try {
    const stale = path.join(p.root, "stale.d.ts");
    fs.writeFileSync(stale, p.declaration.replace(hash(p.runtime), "a".repeat(64)));
    rejects([p.runtimePath, p.declarationPath, stale], undefined, p.runtimePath);
    fs.rmSync(stale);
    const q = pair();
    try { deepEqual(inspect([p.runtimePath, p.declarationPath, q.runtimePath, q.declarationPath]).length, 2); }
    finally { q.cleanup(); }
  } finally { p.cleanup(); }
});

for (const version of ["2", "future"] as const) test(`rejects generated declaration version ${version}`, () => {
  const p = pair();
  try {
    fs.writeFileSync(p.declarationPath, p.declaration.replace('"generatorVersion":"3"', `"generatorVersion":"${version}"`));
    rejects([p.declarationPath], undefined, p.declarationPath);
  } finally { p.cleanup(); }
});

test("binds Program source bytes and validates optional companion pairs", () => {
  const p = pair();
  try {
    const observed = program([p.runtimePath, p.declarationPath]);
    const newer = p.runtime + "// harmless comment after observed Program\n";
    rewriteRuntime(p, newer);
    equal(inspect([p.runtimePath, p.declarationPath]).length, 1);
    rejectsProgram(observed, p.runtimePath);
    fs.writeFileSync(p.declarationPath, p.declaration.replace(',"runtimeSha256":"' + hash(p.runtime) + '"', ""));
    rejects([p.runtimePath, p.declarationPath], undefined, p.declarationPath);
    fs.writeFileSync(p.declarationPath, "// resoloop-catalog-types:{broken}\n" + p.declaration);
    rejects([p.runtimePath, p.declarationPath], undefined, p.declarationPath);
  } finally { p.cleanup(); }
});
function rejectsProgram(observed: ts.Program, file: string) {
  try { catalogRuntimeTables(observed); throw new Error("expected catalog artifact mismatch"); }
  catch (error) {
    if (error instanceof Error && error.message === "expected catalog artifact mismatch") throw error;
    equal((error as any).code, "APPLY_CATALOG_TYPES_HASH_MISMATCH");
    equal(buildErrorLocation(error as object).replace(/\\/g, "/"), `${file}:1:1`.replace(/\\/g, "/"));
  }
}

test("requires a matching same-directory declaration and keeps marker-free legacy declarations", () => {
  const p = pair();
  try {
    rejects([p.runtimePath], undefined, p.runtimePath);
    fs.writeFileSync(p.declarationPath, p.declaration.replace('"runtimeFile":"catalog-fields.ts"', '"runtimeFile":"wrong.ts"'));
    rejects([p.runtimePath, p.declarationPath], undefined, p.runtimePath);
    const movedDir = path.join(p.root, "other"); fs.mkdirSync(movedDir);
    const moved = path.join(movedDir, "catalog-types.d.ts"); fs.writeFileSync(moved, p.declaration);
    rejects([p.runtimePath, moved], undefined, p.runtimePath);
    fs.writeFileSync(moved, "// moved resoloop-catalog-types: marker\n" + p.declaration);
    rejects([moved], undefined, moved);
    const legacy = path.join(p.root, "legacy.d.ts"); fs.writeFileSync(legacy, "declare module 'resoloop-jsx' { interface CatalogComponentRegistry {} }\n");
    equal(inspect([legacy]).length, 0);
  } finally { p.cleanup(); }
});

test("accepts the unit-04 fixture through a Program without copying helpers", () => {
  const root = path.resolve(process.cwd(), "../../tests/fixtures/catalog-scalars-v3"), temp = dir();
  try {
    const runtime = path.join(temp, "catalog-fields.ts"), declaration = path.join(temp, "declaration.handwritten.d.ts");
    fs.writeFileSync(runtime, fs.readFileSync(path.join(root, "runtime.handwritten.ts"), { encoding: "utf8" }));
    fs.writeFileSync(declaration, fs.readFileSync(path.join(root, "declaration.handwritten.d.ts"), { encoding: "utf8" }));
    const catalog = fs.readFileSync(path.join(root, "catalog.handwritten.json"), { encoding: "utf8" });
    deepEqual(inspect([runtime, declaration], catalog).length, 1);
  } finally { fs.rmSync(temp, { recursive: true, force: true }); }
});

test("CLI rejects generated declarations before author code and never imports runtime text", async () => {
  const p = pair(), entry = path.join(p.root, "main.tsx");
  try {
    const malicious = p.runtime.replace(empty, "(globalThis as any).__catalogRuntimeExecuted = true");
    rewriteRuntime(p, malicious);
    fs.writeFileSync(entry, '/// <reference path="./catalog-types.d.ts" />\nimport { catalog } from "./catalog-fields.js";\nvoid catalog;\n(globalThis as any).__catalogAuthorExecuted = true;\nexport default null;\n');
    const result = await buildFile(entry);
    equal(result.exitCode, 1); match(result.errors.join("\n"), /APPLY_CATALOG_TYPES_HASH_MISMATCH/);
    equal((globalThis as any).__catalogRuntimeExecuted, undefined); equal((globalThis as any).__catalogAuthorExecuted, undefined);
  } finally { delete (globalThis as any).__catalogRuntimeExecuted; delete (globalThis as any).__catalogAuthorExecuted; p.cleanup(); }
});

test("CLI rejects old custom and malformed default headers before author code", async () => {
  const root = dir(), marker = "__catalogHeaderAuthorExecuted";
  try {
    const custom = path.join(root, "custom.d.ts"), entry = path.join(root, "main.tsx");
    fs.writeFileSync(custom, '// resoloop-catalog-types: {"formatVersion":"1","contentHash":"x","generatorVersion":"2"}\nexport {};\n');
    fs.writeFileSync(entry, `/// <reference path="./custom.d.ts" />\n(globalThis as any).${marker} = true;\nexport default null;\n`);
    const old = await buildFile(entry); equal(old.exitCode, 1); match(old.errors.join("\n"), /APPLY_CATALOG_TYPES_HASH_MISMATCH/);
    equal((globalThis as any)[marker], undefined);
    fs.mkdirSync(path.join(root, ".resoloop"));
    fs.writeFileSync(path.join(root, ".resoloop", "catalog-types.d.ts"), "// resoloop-catalog-types: {broken}\nexport {};\n");
    const malformed = await buildFile(entry); equal(malformed.exitCode, 1);
    match(malformed.errors.join("\n"), /APPLY_CATALOG_TYPES_HASH_MISMATCH/); equal((globalThis as any)[marker], undefined);
    const defaultPath = path.join(root, ".resoloop", "catalog-types.d.ts").replace(/\\/g, "/");
    ok(malformed.errors.join("\n").replace(/\\/g, "/").includes(defaultPath));
  } finally { delete (globalThis as any)[marker]; fs.rmSync(root, { recursive: true, force: true }); }
});
