import { test } from "node:test";
import { deepEqual, equal, ok } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import * as ts from "typescript";
import { createHash } from "node:crypto";
import { fileURLToPath, pathToFileURL } from "node:url";
import { evaluate, BuildError } from "../src/evaluate.js";
import { buildErrorOrigin, originOfIr } from "../src/source-map.js";
import { sourceTransformer } from "../src/source-transform.js";
import { objectTestRuntimeTable } from "./object-test-helpers.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const baseText = [
  'import { Slot, Component, defineObject, ref } from "resoloop-jsx";',
  'const Other = defineObject<{}>()({ exports: {}, body: () => <Slot key="root" name="Other" /> });',
  'const other = Other.instance("other");',
  'const Part = defineObject<{ target: string }>()({',
  '  exports: { root: ref.slot("root") },',
  '  body: ({ target }) => {',
  '    ;',
  '    return (',
  '      <Slot key="root" name="Part">',
  '        <Component',
  '          key="sink"',
  '          type="Synthetic.Holder"',
  '        fields={{ Target: target }}',
  '        />',
  '      </Slot>',
  '    );',
  '  },',
  '});',
  'const handle = Part.instance("desk");',
  'export default <Slot key="scene" name="Scene">',
  '  <Other handle={other} />',
  '  <Part',
  '    handle={handle}',
  '    target={ref.slot("other::ghost")}',
  '  />',
  '</Slot>;',
].join("\n") + "\n";

function workspace(text: string) {
  const root = fs.mkdtempSync(path.join(packageRoot, ".s2-12-source-"));
  const entry = path.join(root, "main.tsx"), outDir = path.join(root, "dist");
  fs.writeFileSync(entry, text);
  const options: ts.CompilerOptions = { module: ts.ModuleKind.NodeNext, moduleResolution: ts.ModuleResolutionKind.NodeNext,
    target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX, jsxImportSource: "resoloop-jsx", rootDir: root, outDir,
    strict: true, skipLibCheck: true, esModuleInterop: true };
  const program = ts.createProgram([entry], options);
  deepEqual(ts.getPreEmitDiagnostics(program).map(diagnostic => diagnostic.code), []);
  const sha256 = createHash("sha256").update(text, "utf8").digest("hex");
  const emitted = program.emit(undefined, undefined, undefined, false,
    sourceTransformer(program, [{ path: entry, sha256, role: "source" }]));
  equal(emitted.emitSkipped, false);
  return { root, entry, out: path.join(outDir, "main.js"), text, sha256,
    load: async () => import(pathToFileURL(path.join(outDir, "main.js")).href),
    cleanup: () => fs.rmSync(root, { recursive: true, force: true }) };
}

function point(text: string, line: number, column: number) {
  const lines = text.split("\n");
  const offset = lines.slice(0, line - 1).reduce((total, value) => total + value.length + 1, 0) + column - 1;
  return { offset, line, column };
}
function expectedSource(file: string, sha256: string, text: string, line: number, start: number, end: number) {
  return { status: "known", file, sha256, range: { start: point(text, line, start), end: point(text, line, end) } };
}
function hasSource(values: unknown[] | undefined, expected: unknown): boolean {
  return !!values?.some(value => JSON.stringify(value) === JSON.stringify(expected));
}
function privateFailure(root: unknown) {
  try { evaluate(root, { catalogs: [objectTestRuntimeTable()] }); }
  catch (error) { if (error instanceof BuildError) return error; throw error; }
  throw new Error("Expected private-reference BuildError");
}
function variant(parameter: string, statement: string, expression: string,
  caller = 'target={ref.slot("other::ghost")}') {
  return baseText.replace("body: ({ target })", "body: (" + parameter + ")")
    .replace("    ;", "    " + statement)
    .replace("fields={{ Target: target }}", "fields={{ Target: " + expression + " }}")
    .replace('target={ref.slot("other::ghost")}', caller);
}

test("real emit and evaluation retain destructured Object forwarding diagnostics", async () => {
  const w = workspace(baseText);
  try {
    const module = await w.load();
    const before = JSON.stringify(module.default);
    const error = privateFailure(module.default);
    equal(error.code, "APPLY_OBJECT_PRIVATE_REFERENCE");
    equal(error.message, "Private Object target '$slot:other::ghost'; use ref.export(handle, name)." );
    const origin = buildErrorOrigin(error)!;
    deepEqual(origin.source, expectedSource(w.entry, w.sha256, baseText, 24, 13, 37));
    ok(hasSource(origin.related, expectedSource(w.entry, w.sha256, baseText, 13, 27, 33)));
    equal(JSON.stringify(module.default), before);
  } finally { w.cleanup(); }
});

test("successful forwarded legacy Slot reference keeps source through IR clone", async () => {
  const text = baseText
    .replace('target={ref.slot("other::ghost")}', 'target={ref.slot("outside::root")}')
    .replace('  <Other handle={other} />', '  <Other handle={other} /><Slot key="outside::root" name="Outside" />');
  const w = workspace(text);
  try {
    const module = await w.load();
    const before = JSON.stringify(module.default);
    const result = evaluate(module.default, { catalogs: [objectTestRuntimeTable()] });
    const part = result.document.children.find(child => child.slot.name === "Part")!;
    const sink = part.components![0];
    equal(sink.fields?.Target, "$slot:outside::root");
    deepEqual(originOfIr(sink)!.attributes.fields.children!.Target.valueSource,
      expectedSource(w.entry, w.sha256, text, 24, 13, 38));
    equal(JSON.stringify(module.default), before);
  } finally { w.cleanup(); }
});

test("first-parameter direct props and renamed destructuring preserve source", async () => {
  for (const [parameter, statement, expression, start, end] of [
    ["props", ";", "props.target", 27, 39], ["{ target: alias }", ";", "alias", 27, 32],
  ] as const) {
    const text = variant(parameter, statement, expression), w = workspace(text);
    try {
      const error = privateFailure((await w.load()).default);
      const origin = buildErrorOrigin(error)!;
      deepEqual(origin.source, expectedSource(w.entry, w.sha256, text, 24, 13, 37));
      ok(hasSource(origin.related, expectedSource(w.entry, w.sha256, text, 13, start, end)));
    } finally { w.cleanup(); }
  }
});

test("writes, copies, computed access, spreads and processing deny forwarding", async () => {
  const cases = [
    ["{ target }", "const copy = target;", "copy", 27, 31],
    ["{ target }", "target = target;", "target", 27, 33],
    ["props", ";", 'props["target"]', 27, 42],
    ["props", "const copy = { ...props };", "copy.target", 27, 38],
    ["{ target }", ";", 'target + ""', 27, 38],
    ["{ target }", ";", String.fromCharCode(96) + "$" + "{target}" + String.fromCharCode(96), 27, 38],
    ["props", "props.target = props.target;", "props.target", 27, 39],
    ["props", 'props["target"] = props.target;', "props.target", 27, 39],
    ["props", "(props as any).target = props.target;", "props.target", 27, 39],
    ["props", "(props).target = props.target;", "props.target", 27, 39],
    ["props", '(props as any)["target"] = props.target;', "props.target", 27, 39],
    ["props", "(props satisfies { target: string }).target = props.target;", "props.target", 27, 39],
    ["props", "(props.target as string) = props.target;", "props.target", 27, 39],
    ["props", "props.target! = props.target;", "props.target", 27, 39],
    ["{ target }", "target! = target;", "target", 27, 33],
  ] as const;
  for (const [parameter, statement, expression, start, end] of cases) {
    const text = variant(parameter, statement, expression), w = workspace(text);
    try {
      const error = privateFailure((await w.load()).default);
      const origin = buildErrorOrigin(error)!;
      deepEqual(origin.source, { status: "unknown" });
      ok(hasSource(origin.related, expectedSource(w.entry, w.sha256, text, 13, start, end)));
    } finally { w.cleanup(); }
  }
});

test("default parameters forward only when their caller snapshot matches", async () => {
  const text = variant('{ target = "$slot:other::ghost" }', ";", "target", "")
    .replace("defineObject<{ target: string }>()", "defineObject<{ target?: string }>()");
  const w = workspace(text);
  try {
    const error = privateFailure((await w.load()).default);
    deepEqual(buildErrorOrigin(error)?.source, { status: "unknown" });
    ok(hasSource(buildErrorOrigin(error)?.related, expectedSource(w.entry, w.sha256, text, 13, 27, 33)));
  } finally { w.cleanup(); }
  const explicit = variant('{ target = "$slot:other::ghost" }', ";", "target");
  const explicitWorkspace = workspace(explicit);
  try {
    const error = privateFailure((await explicitWorkspace.load()).default);
    deepEqual(buildErrorOrigin(error)?.source,
      expectedSource(explicitWorkspace.entry, explicitWorkspace.sha256, explicit, 24, 13, 37));
  } finally { explicitWorkspace.cleanup(); }
});

test("original-source offsets use UTF-16 and SHA-256 of emitted input", async () => {
  const text = baseText.replace("    ;", '    const note = "日本語😀";');
  const w = workspace(text);
  try {
    const error = privateFailure((await w.load()).default);
    const source = buildErrorOrigin(error)!.source;
    deepEqual(source, expectedSource(w.entry, createHash("sha256").update(text, "utf8").digest("hex"), text, 24, 13, 37));
    equal(text.slice(source.status === "known" ? source.range.start.offset : 0,
      source.status === "known" ? source.range.end.offset : 0), 'ref.slot("other::ghost")');
  } finally { w.cleanup(); }
});

test("helper destructuring cannot borrow the caller Object identity", async () => {
  const text = [
    'import { Slot, Component, defineObject, ref } from "resoloop-jsx";',
    'const Other = defineObject<{}>()({ exports: {}, body: () => <Slot key="root" name="Other" /> });',
    'const other = Other.instance("other");',
    'function Helper({ target }: { target: string }) {',
    '  return <Component key="helper" type="Synthetic.Holder" fields={{ Target: target }} />;',
    '}',
    'const Part = defineObject<{ target: string }>()({',
    '  exports: { root: ref.slot("root") },',
    '  body: ({ target }) => <Slot key="root" name="Part">{Helper({ target })}</Slot>,',
    '});',
    'const handle = Part.instance("desk");',
    'export default <Slot key="scene" name="Scene"><Other handle={other} /><Part handle={handle} target={ref.slot("other::ghost")} /></Slot>;',
  ].join("\n") + "\n";
  const w = workspace(text);
  try {
    const error = privateFailure((await w.load()).default);
    deepEqual(buildErrorOrigin(error)?.source, { status: "unknown" });
    ok(hasSource(buildErrorOrigin(error)?.related, expectedSource(w.entry, w.sha256, text, 5, 76, 82)));
  } finally { w.cleanup(); }
});

test("sourceFunction preserves this, arguments, defaults, async await, and evaluation order", async () => {
  const text = [
    'const order: string[] = [];',
    'const marker = { tag: "marker" };',
    'export const sync = function(this: typeof marker, { value = (order.push("default"), 7) }: { value?: number }, extra: number) {',
    '  order.push("body");',
    '  return [this === marker, arguments.length, value, extra];',
    '};',
    'export const syncResult = sync.call(marker, {}, 9);',
    'export const asyncFn = async ({ value = (order.push("async-default"), 4) }: { value?: number }) => {',
    '  order.push("async-body");',
    '  await Promise.resolve();',
    '  order.push("async-after");',
    '  return value;',
    '};',
    'export const orderSnapshot = order;',
    'export const asyncResult = asyncFn({});',
  ].join("\n") + "\n";
  const w = workspace(text);
  try {
    const module = await w.load();
    deepEqual(module.syncResult, [true, 2, 7, 9]);
    equal(await module.asyncResult, 4);
    deepEqual(module.orderSnapshot, ["default", "body", "async-default", "async-body", "async-after"]);
  } finally { w.cleanup(); }
});

test("hoisted declaration runs before its source annotation without borrowing caller origin", async () => {
  const text = [
    'import { Component } from "resoloop-jsx";',
    'import { callComponent, originOfElement, sourceElement } from "resoloop-jsx/source-runtime";',
    'const parent = { source: { status: "unknown" as const }, attributes: {} };',
    'export const root = sourceElement(() => callComponent(Helper, { target: "$slot:outside::root" }), parent);',
    'export const origin = originOfElement(root);',
    'function Helper({ target }: { target: string }) {',
    '  return <Component key="sink" type="Synthetic.Holder" fields={{ Target: target }} />;',
    '}',
  ].join("\n") + "\n";
  const w = workspace(text);
  try {
    const module = await w.load();
    deepEqual(module.origin?.attributes.fields.children?.Target.valueSource, { status: "unknown" });
  } finally { w.cleanup(); }
});

test("class field function names follow native static and dynamic key naming", async () => {
  const text = [
    'let keyCalls = 0;',
    'function getKey(): "dynamic" { keyCalls++; return "dynamic"; }',
    'const keys = { get name(): "dynamic" { return getKey(); } };',
    'class Holder {',
    '  ["body"] = ({ target }: { target: string }) => target;',
    '  #privateBody = ({ target }: { target: string }) => target;',
    '  get privateName() { return this.#privateBody.name; }',
    '  runPrivate(target: string) { return this.#privateBody({ target }); }',
    '  [keys.name] = ({ target }: { target: string }) => target;',
    '}',
    'export const instance = new Holder();',
    'export { keyCalls };',
  ].join("\n") + "\n";
  const w = workspace(text);
  try {
    const module = await w.load();
    deepEqual([module.instance.body.name, module.instance.privateName, module.instance.dynamic.name],
      ["body", "#privateBody", "dynamic"]);
    equal(module.keyCalls, 1);
    equal(module.instance.body({ target: "body-value" }), "body-value");
    equal(module.instance.runPrivate("private-value"), "private-value");
    equal(module.instance.dynamic({ target: "dynamic-value" }), "dynamic-value");
  } finally { w.cleanup(); }
});
