import { test } from "node:test";
import { deepEqual, equal, ok, throws } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import * as ts from "typescript";
import { createHash } from "node:crypto";
import { fileURLToPath, pathToFileURL } from "node:url";
import { evaluate, BuildError } from "../src/evaluate.js";
import { buildErrorOrigin, callComponent, captureElement, originOfElement, originOfIr, sourceDefinition,
  sourceDefinitionValue, sourceElement, sourceFunction, type ElementOrigin, type Source } from "../src/source-map.js";
import { sourceTransformer } from "../src/source-transform.js";
import { objectTestRuntimeTable } from "./object-test-helpers.js";
const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
function workspace(text: string) {
  const root = fs.mkdtempSync(path.join(packageRoot, ".s2-12-definition-")), entry = path.join(root, "main.tsx"), outDir = path.join(root, "dist");
  fs.writeFileSync(entry, text);
  const options: ts.CompilerOptions = { module: ts.ModuleKind.NodeNext, moduleResolution: ts.ModuleResolutionKind.NodeNext, target: ts.ScriptTarget.ES2022,
    jsx: ts.JsxEmit.ReactJSX, jsxImportSource: "resoloop-jsx", rootDir: root, outDir, strict: true, skipLibCheck: true, esModuleInterop: true };
  const program = ts.createProgram([entry], options); deepEqual(ts.getPreEmitDiagnostics(program).map(diagnostic => diagnostic.code), []);
  const sha256 = createHash("sha256").update(text, "utf8").digest("hex"), emitted = program.emit(undefined, undefined, undefined, false,
    sourceTransformer(program, [{ path: entry, sha256, role: "source" }])); equal(emitted.emitSkipped, false);
  return { root, entry, text, sha256, load: async () => import(pathToFileURL(path.join(outDir, "main.js")).href), cleanup: () => fs.rmSync(root, { recursive: true, force: true }) };
}
function point(text: string, line: number, column: number) { const lines = text.split("\n"); return {
  offset: lines.slice(0, line - 1).reduce((total, value) => total + value.length + 1, 0) + column - 1, line, column }; }
function expectedSource(file: string, sha256: string, text: string, line: number, start: number, end: number): Source {
  return { status: "known", file, sha256, range: { start: point(text, line, start), end: point(text, line, end) } };
}
function hasSource(values: Source[] | undefined, expected: Source): boolean { return !!values?.some(value => JSON.stringify(value) === JSON.stringify(expected)); }
function exportFailure(root: unknown) {
  try { evaluate(root, { catalogs: [objectTestRuntimeTable()] }); }
  catch (error) { if (error instanceof BuildError) return error; throw error; }
  throw new Error("Expected Object export BuildError");
}
function invalidExportText(moduleName = "resoloop-jsx", importName = "defineObject", localName = "defineObject"): string {
  return `import { Slot, ${importName}${localName === importName ? "" : ` as ${localName}`}, ref } from "${moduleName}";
const Def = ${localName}<{}>()({
  exports: { bad: ref.slot("ghost") },
  body: () => <Slot key="root" name="Part" />,
});
const handle = Def.instance("desk");
export default <Slot key="scene" name="Scene"><Def handle={handle} /></Slot>;
`;
}
test("import-renamed root and draft definitions retain exact export and handle ranges", async () => {
  for (const text of [invalidExportText("resoloop-jsx", "defineObject", "make"),
    invalidExportText("resoloop-jsx/draft", "defineObject", "make")]) {
    const w = workspace(text);
    try {
      const error = exportFailure((await w.load()).default);
      equal(error.code, "APPLY_OBJECT_EXPORT_INVALID");
      equal(error.message, "Object 'desk' export 'bad': target is not a declared internal Slot, Component or Field.");
      const origin = buildErrorOrigin(error)!;
      deepEqual(origin.source, expectedSource(w.entry, w.sha256, text, 3, 19, 36));
      ok(hasSource(origin.related, expectedSource(w.entry, w.sha256, text, 7, 60, 66)));
    } finally { w.cleanup(); }
  }
});
test("duplicate handles point to second use and relate the first use", async () => {
  const text = `import { Slot, defineObject } from "resoloop-jsx";
const Def = defineObject<{}>()({ exports: {}, body: () => <Slot key="root" name="Part" /> });
const handle = Def.instance("desk");
export default <Slot key="scene" name="Scene">
  <Def handle={handle} />
  <Def handle={handle} />
</Slot>;
`;
  const w = workspace(text);
  try {
    const error = exportFailure((await w.load()).default);
    equal(error.code, "APPLY_OBJECT_DUPLICATE");
    equal(error.message, "Object instance 'desk' occurs twice.");
    const origin = buildErrorOrigin(error)!;
    deepEqual(origin.source, expectedSource(w.entry, w.sha256, text, 6, 16, 22));
    ok(hasSource(origin.related, expectedSource(w.entry, w.sha256, text, 5, 16, 22)));
  } finally { w.cleanup(); }
});
test("defineObject import identity rejects local same-name and copied aliases", async () => {
  const localText = `import { Slot, ref } from "resoloop-jsx";
function defineObject() { return (value: any) => Object.assign((_props: any) => null, { instance: () => ({}) }); }
export const Def = defineObject()({ exports: { bad: ref.slot("ghost") }, body: () => null });
export default <Slot key="root" name="Root" />;
`;
  const aliasText = `import { Slot, defineObject } from "resoloop-jsx";
const alias = defineObject;
export const Def = alias<{}>()({ exports: { bad: "$slot:ghost" }, body: () => null });
export default <Slot key="root" name="Root" />;
`;
  for (const text of [localText, aliasText]) {
    const w = workspace(text);
    try {
      const module = await w.load();
      evaluate(module.default, { catalogs: [objectTestRuntimeTable()] });
      equal(originOfElement(module.Def), undefined);
    } finally { w.cleanup(); }
  }
});
test("JSX-free definition modules import runtime metadata and evaluate explicit catalogs", async () => {
  const text = `import { defineObject } from "resoloop-jsx";
export const Def = defineObject<{}>()({
  exports: { root: "$slot:root" },
  body: () => ({ kind: "slot", props: { key: "body", name: "Body" } } as any),
});
export default { kind: "slot", props: { key: "root", name: "Root" } };
`;
  const w = workspace(text);
  try {
    const module = await w.load();
    evaluate(module.default, { catalogs: [objectTestRuntimeTable()] });
    const origin = originOfElement(module.Def)!;
    equal(origin.attributes.root.valueSource?.status, "known");
    equal(origin.attributes.body.valueSource?.status, "known");
  } finally { w.cleanup(); }
});
test("spread and computed export objects stay unknown", async () => {
  const templates = [
    `exports: { ...{ bad: ref.slot("ghost") } }`,
    `exports: { ["bad"]: ref.slot("ghost") }`,
  ];
  for (const exportsText of templates) {
    const text = `import { Slot, defineObject, ref } from "resoloop-jsx";
const Def = defineObject<{}>()({
  ${exportsText},
  body: () => <Slot key="root" name="Part" />,
});
const handle = Def.instance("desk");
export default <Slot key="scene" name="Scene"><Def handle={handle} /></Slot>;
`;
    const w = workspace(text);
    try {
      const error = exportFailure((await w.load()).default);
      equal(error.code, "APPLY_OBJECT_EXPORT_INVALID");
      deepEqual(buildErrorOrigin(error)?.source, { status: "unknown" });
    } finally { w.cleanup(); }
  }
});
test("direct exported member keeps the original consuming expression through IR clone", async () => {
  const text = `import { Slot, Component, defineObject, ref } from "resoloop-jsx";
const Part = defineObject<{}>()({
  exports: { enabled: ref.member("lamp", "Enabled") },
  body: () => <Slot key="root" name="Part">
    <Component key="lamp" type="Synthetic.Device" fields={{ Enabled: true }} />
    <Component key="reader" type="Synthetic.Device" fields={{ Target: ref.export(handle, "enabled") }} />
  </Slot>,
});
const handle = Part.instance("desk");
export default <Slot key="scene" name="Scene"><Part handle={handle} /></Slot>;
`;
  const w = workspace(text);
  try {
    const module = await w.load();
    const result = evaluate(module.default, { catalogs: [objectTestRuntimeTable()] });
    const reader = result.document.children[0].components![1];
    const origin = originOfIr(reader)!.attributes.fields.children!.Target;
    deepEqual(origin.valueSource, expectedSource(w.entry, w.sha256, text, 6, 71, 100));
  } finally { w.cleanup(); }
});
test("Slot-only exported parent reference keeps its consumer range without catalogs", async () => {
  const text = `import { Slot, defineObject, ref } from "resoloop-jsx";
const Def = defineObject<{}>()({
  exports: { root: ref.slot("root") },
  body: () => <Slot key="root" name="Part" />,
});
const handle = Def.instance("desk");
export default <Slot
  key="scene" name="Scene" parent={ref.export(handle, "root")}
><Def handle={handle} /></Slot>;
`;
  const w = workspace(text);
  try {
    const module = await w.load();
    const result = evaluate(module.default);
    equal(result.document.slot.parent, "$slot:desk::root");
    const origin = originOfIr(result.document.slot)!;
    deepEqual(origin.attributes.parent.valueSource, expectedSource(w.entry, w.sha256, text, 8, 36, 62));
  } finally { w.cleanup(); }
});
test("source definition helpers preserve object/function identity and restore channels after throw", () => {
  const knownAt = (file: string, digest: string, offset: number, line: number, column: number): Source => ({
    status: "known", file, sha256: digest.repeat(64), range: {
      start: { offset, line, column }, end: { offset: offset + 1, line, column: column + 1 },
    },
  });
  const outerSource = knownAt("outer.tsx", "a", 0, 1, 1);
  const definitionSource = knownAt("definition.tsx", "b", 10, 2, 3);
  const consumerSource = knownAt("consumer.tsx", "c", 20, 3, 5);
  const metadata: ElementOrigin = { source: definitionSource, attributes: {} };
  const value = {};
  equal(sourceDefinitionValue(value, metadata), value);
  deepEqual(originOfElement(value), metadata);
  const fromThunk = {};
  equal(sourceDefinition(() => fromThunk, metadata), fromThunk);
  deepEqual(originOfElement(fromThunk), metadata);
  const fn = () => 1;
  equal(sourceFunction(fn, "identity"), fn);
  const outer = { source: outerSource, attributes: {} };
  const consumer = { source: consumerSource, attributes: {} };
  const outerChild = {};
  const invoke = (_props: object) => {
    throws(() => sourceDefinition(() => { throw new Error("thunk failed"); }, metadata), /thunk failed/);
    captureElement(outerChild);
    return outerChild;
  };
  sourceElement(() => callComponent(invoke, {}), outer);
  deepEqual(originOfElement(outerChild)!.source, outerSource);
  const consumerChild = {};
  const consumeAfterThrow = (_props: object) => {
    throws(() => sourceDefinition(() => { throw new Error("thunk failed"); }, metadata), /thunk failed/);
    return sourceElement(() => { captureElement(consumerChild); return consumerChild; }, consumer);
  };
  sourceElement(() => callComponent(consumeAfterThrow, {}), outer);
  const origin = originOfElement(consumerChild)!;
  deepEqual(origin.source, consumerSource);
  deepEqual(origin.related, [outerSource]);
});
const forwardingBase = `import { Slot, Component, defineObject, ref } from "resoloop-jsx";\nconst Other = defineObject<{}>()({ exports: {}, body: () => <Slot key="root" name="Other" /> });\nconst other = Other.instance("other");\nconst Part = defineObject<{ target: string }>()({\n  exports: { root: ref.slot("root") },\n  body: ({ target }) => {\n    ;\n    return (\n      <Slot key="root" name="Part">\n        <Component\n          key="sink"\n          type="Synthetic.Holder"\n        fields={{ Target: target }}\n        />\n      </Slot>\n    );\n  },\n});\nconst handle = Part.instance("desk");\nexport default <Slot key="scene" name="Scene">\n  <Other handle={other} />\n  <Part\n    handle={handle}\n    target={ref.slot("other::ghost")}\n  />\n</Slot>;\n`;
function forwardingVariant(parameter: string, statement: string, expression: string): string {
  return forwardingBase.replace("body: ({ target })", "body: (" + parameter + ")")
    .replace("    ;", statement).replace("fields={{ Target: target }}", "fields={{ Target: " + expression + " }}");
}
test("same-value assignment targets and parameter defaults disable Object forwarding", async () => {
  const cases: [string, string, string, number, number][] = [
    ["{ target }", "    ({ target } = { target });", "target", 27, 33],
    ["{ target }", "    [target] = [target];", "target", 27, 33],
    ["{ target }", "    for (target of [target]) {}", "target", 27, 33],
    ["props", "    ({ target: props.target } = { target: props.target });", "props.target", 27, 39],
    ["props", "    [props.target] = [props.target];", "props.target", 27, 39],
    ["props, unused = (props.target = props.target)", "    ;", "props.target", 27, 39],
  ];
  for (const [parameter, statement, expression, start, end] of cases) {
    const text = forwardingVariant(parameter, statement, expression), w = workspace(text);
    try {
      const error = exportFailure((await w.load()).default);
      equal(error.code, "APPLY_OBJECT_PRIVATE_REFERENCE");
      equal(error.message, "Private Object target '$slot:other::ghost'; use ref.export(handle, name).");
      const origin = buildErrorOrigin(error)!;
      deepEqual(origin.source, { status: "unknown" });
      ok(hasSource(origin.related, expectedSource(w.entry, w.sha256, text, 13, start, end)));
    } finally { w.cleanup(); }
  }
});
function bodyExportText(reverse = false, exportMember = 'body: ref.slot("ghost")',
  bodyValue = '() => <Slot key="root" name="Part" />'): string {
  const members = reverse ? `  body: ${bodyValue},\n  exports: { ${exportMember} },`
    : `  exports: { ${exportMember} },\n  body: ${bodyValue},`;
  return `import { Slot, defineObject, ref } from "resoloop-jsx";\nexport const Def = defineObject<{}>()({\n${members}\n});\nconst handle = Def.instance("desk");\nexport default <Slot key="scene" name="Scene"><Def handle={handle} /></Slot>;\n`;
}
test("body export and definition body retain separate origins in either property order", async () => {
  for (const reverse of [false, true]) {
    const text = bodyExportText(reverse), w = workspace(text);
    const exportLine = reverse ? 4 : 3, bodyLine = reverse ? 3 : 4;
    try {
      const module = await w.load(), error = exportFailure(module.default);
      equal(error.code, "APPLY_OBJECT_EXPORT_INVALID");
      equal(error.message, "Object 'desk' export 'body': target is not a declared internal Slot, Component or Field.");
      const origin = buildErrorOrigin(error)!;
      deepEqual(origin.source, expectedSource(w.entry, w.sha256, text, exportLine, 20, 37));
      ok(hasSource(origin.related, expectedSource(w.entry, w.sha256, text, 7, 60, 66)));
      const body = originOfElement(module.Def)!.attributes.body;
      const bodySource = expectedSource(w.entry, w.sha256, text, bodyLine, 9, 46);
      const exportSource = expectedSource(w.entry, w.sha256, text, exportLine, 20, 37);
      deepEqual(body.source, bodySource);
      deepEqual(body.valueSource, exportSource);
      ok(hasSource(body.related, bodySource));
      ok(hasSource(body.related, exportSource));
    } finally { w.cleanup(); }
  }
});
test("invalid body keeps its source while uncertain body exports stay unknown", async () => {
  const invalidText = bodyExportText(false, 'body: ref.slot("root")', "() => null"), invalid = workspace(invalidText);
  try {
    const module = await invalid.load(), error = exportFailure(module.default);
    equal(error.code, "APPLY_OBJECT_INVALID");
    equal(error.message, "Object 'desk': body must contain exactly one root Slot.");
    deepEqual(buildErrorOrigin(error)?.source, expectedSource(invalid.entry, invalid.sha256, invalidText, 4, 9, 19));
    const body = originOfElement(module.Def)!.attributes.body;
    deepEqual(body.source, expectedSource(invalid.entry, invalid.sha256, invalidText, 4, 9, 19));
    deepEqual(body.valueSource, expectedSource(invalid.entry, invalid.sha256, invalidText, 3, 20, 36));
  } finally { invalid.cleanup(); }
  for (const text of [bodyExportText(false, '...{ body: ref.slot("ghost") }'), bodyExportText(false, '["body"]: ref.slot("ghost")'), bodyExportText().replace('exports: { body: ref.slot("ghost") }', 'exports: (() => ({ body: ref.slot("ghost") }))()')]) {
    const w = workspace(text);
    try {
      const module = await w.load(), error = exportFailure(module.default);
      equal(error.code, "APPLY_OBJECT_EXPORT_INVALID");
      deepEqual(buildErrorOrigin(error)?.source, { status: "unknown" });
      const body = originOfElement(module.Def)!.attributes.body;
      deepEqual(body.source, expectedSource(w.entry, w.sha256, text, 4, 9, 46));
      deepEqual(body.valueSource, { status: "unknown" });
    } finally { w.cleanup(); }
  }
});
test("source-function registration preserves native inferred names and computed key evaluation", async () => {
  const text = `export const sync = function({ target }: { target: string }) { return target; };\nexport const asyncFn = async ({ target }: { target: string }) => target;\nlet assigned: (value: { target: string }) => string;\nassigned = ({ target }: { target: string }) => target;\nexport const property = { body: ({ target }: { target: string }) => target };\nfunction factory(callback = ({ target }: { target: string }) => target) { return callback; }\nexport const defaultFn = factory();\nlet keyCalls = 0;\nfunction getKey() { keyCalls++; return "dynamic"; }\nexport const computed = { [getKey()]: ({ target }: { target: string }) => target };\nexport const paren = ((({ target }: { target: string }) => target));\nexport const pdyn: Record<string, () => string> = { [getKey()]: ((({ target }: { target: string }) => target)) } as any;\nconst box: any = {};\nbox.cb = ({ target }: { target: string }) => target;\nbox["el"] = ({ target }: { target: string }) => target;\nlet lg: any;\nlg ??= ({ target }: { target: string }) => target;\nconst protoObj = { __proto__: ({ target }: { target: string }) => target };\nconst protoName = (Object.getPrototypeOf(protoObj) as Function).name;\nexport const names = [sync.name, asyncFn.name, assigned.name, property.body.name, defaultFn.name, computed.dynamic.name, paren.name, pdyn.dynamic.name, box.cb.name, box.el.name, lg.name, protoName, keyCalls];\n`;
  const w = workspace(text);
  try {
    const module = await w.load();
    deepEqual(module.names, ["sync", "asyncFn", "assigned", "body", "callback", "dynamic", "paren", "dynamic", "", "", "lg", "", 2]);
  } finally { w.cleanup(); }
});
