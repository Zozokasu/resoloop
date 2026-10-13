import { test } from "node:test";
import { deepEqual, equal, ok } from "node:assert/strict";
import ts from "typescript";
import { BuildError } from "../src/evaluate.js";
import { componentFields } from "../src/bound-field.js";
import { collectObjects, validateObjectBoundaries } from "../src/object-expand.js";
import { validateObjectExports } from "../src/object-boundary.js";
import { defineObject } from "../src/object-definition.js";
import { buildErrorLocation, sourceElement } from "../src/source-map.js";
import { bind, defineVariables, drive, types, variable } from "../src/variables.js";
import { fieldBindingDescriptor, variableRefDescriptor } from "../src/variables-internal.js";
import { objectTestRuntimeTable } from "./object-test-helpers.js";

function expectError(action: () => unknown, code: string, message: string): BuildError {
  let captured: unknown;
  try { action(); } catch (error) { captured = error; }
  if (!(captured instanceof BuildError)) throw new Error("Expected a BuildError");
  equal(captured.code, code);
  equal(captured.message, message);
  return captured;
}

const known = (file: string) => ({ status: "known" as const, file, sha256: "a".repeat(64),
  range: { start: { offset: 0, line: 3, column: 4 }, end: { offset: 1, line: 3, column: 5 } } });
function atSource<T>(file: string, action: () => T): T {
  const source = known(file);
  return sourceElement(action, { source, attributes: {
    name: { source, valueSource: source }, value: { source, valueSource: source },
  } });
}

const slot = (key: string, children?: unknown) => ({ kind: "slot", props: { key, name: key, children } }) as any;
const component = (key: string, props: Record<string, unknown> = {}) =>
  ({ kind: "component", props: { key, type: "Synthetic.Device", ...props } }) as any;
function objectAround(id: string, body: unknown): unknown {
  const definition = defineObject<{}>()({ exports: {}, body: () => body as never });
  return definition({ handle: definition.instance(id) });
}

test("catalog-bound bool Fields accept bindings and reject bare refs or unsupported targets", () => {
  const table = objectTestRuntimeTable();
  const fields = componentFields("Synthetic.Device" as never, table) as any;
  const set = defineVariables({ scopeKey: "state", variables: { on: variable(types.bool, true) } });
  const binding = drive(set.vars.on);
  equal(fieldBindingDescriptor(fields.Field({ name: "Enabled", value: binding }).props.value)?.ref, set.vars.on);
  equal(fieldBindingDescriptor(fields.Field({ name: "Enabled", value: bind(set.vars.on) }).props.value)?.mode, "bind");
  const bare = expectError(() => atSource("bare.ts", () => fields.Field({ name: "Enabled", value: set.vars.on })),
    "CREATION_VARIABLE_UNRESOLVED", "Variable reference requires drive(...) or bind(...).");
  equal(buildErrorLocation(bare), "bare.ts:3:4");
  const mismatch = expectError(() => atSource("count.ts", () => fields.Field({ name: "Count", value: binding })),
    "CREATION_VARIABLE_TYPE_MISMATCH", "Variable 'bool' cannot bind to Field 'Synthetic.Device.Count' of type 'int32'.");
  equal(buildErrorLocation(mismatch), "count.ts:3:4");
  expectError(() => fields.Field({ name: "Missing", value: binding }), "CREATION_VARIABLE_TYPE_UNSUPPORTED",
    "Variable binding target 'Synthetic.Device.Missing' requires a confirmed bool Field.");
  expectError(() => fields.Field({ name: "Uncertain", value: binding }), "CREATION_VARIABLE_TYPE_UNSUPPORTED",
    "Variable binding target 'Synthetic.Device.Uncertain' requires a confirmed bool Field.");
  const normal = expectError(() => fields.Field({ name: "Enabled", value: 1 }), "APPLY_OBJECT_TYPE_MISMATCH",
    "Object reference type mismatch: expected 'value:boolean', observed 'value:number'.");
  equal(buildErrorLocation(normal), "unknown");
});

test("Object expansion preserves authentic binding and reference identities", () => {
  const set = defineVariables({ scopeKey: "state", variables: { on: variable(types.bool, false) } });
  const binding = drive(set.vars.on);
  const reference = set.vars.on;
  const body = slot("root", component("device", { fields: { Enabled: binding, Other: reference } }));
  const object = objectAround("instance", body);
  const context = collectObjects(slot("page", object), [objectTestRuntimeTable()]);
  validateObjectExports(context);
  validateObjectBoundaries(context);
  const result = context.root as any;
  const resultComponent = result.props.children.props.body.props.children;
  equal(resultComponent.props.fields.Enabled, binding);
  equal(resultComponent.props.fields.Other, reference);
  equal(fieldBindingDescriptor(resultComponent.props.fields.Enabled)?.ref, reference);
  ok(variableRefDescriptor(resultComponent.props.fields.Other));
});

test("binding collisions use the creation diagnostic in either Field order and preserve plain duplicates", () => {
  const table = objectTestRuntimeTable();
  const fields = componentFields("Synthetic.Device" as never, table) as any;
  const set = defineVariables({ scopeKey: "state", variables: { on: variable(types.bool, true) } });
  const bindingField = (file: string) => atSource(file, () => fields.Field({ name: "Enabled", value: drive(set.vars.on) }));
  const plainField = (file: string) => atSource(file, () => fields.Field({ name: "Enabled", value: true }));
  const rootFor = (children: unknown[]) => {
    return slot("page", objectAround("one", slot("root", component("device", { children }))));
  };
  for (const [children, file] of [
    [[bindingField("first-binding.ts"), plainField("second-plain.ts")], "second-plain.ts"],
    [[plainField("first-plain.ts"), bindingField("second-binding.ts")], "second-binding.ts"],
    [[plainField("first-plain.ts"), plainField("second-plain.ts"), bindingField("third-binding.ts")], "second-plain.ts"],
  ] as const) {
    const error = expectError(() => collectObjects(rootFor([...children]), [table]), "CREATION_DRIVEN_FIELD_DECLARED",
      "Variable binding target 'Enabled' is declared more than once.");
    equal(buildErrorLocation(error), `${file}:3:4`);
  }
  const plainRoot = slot("page", objectAround("plain", slot("root", component("device", {
    children: [plainField("plain-one.ts"), plainField("plain-two.ts")],
  }))));
  expectError(() => collectObjects(plainRoot, [table]), "APPLY_FIELD_DUPLICATE",
  "Member 'Enabled' at $.objects[plain].components[0][1] is declared more than once");
  const mapRoot = slot("page", objectAround("map", slot("root", component("device", {
    fields: { Enabled: drive(set.vars.on) }, initialFields: { Enabled: false },
  }))));
  expectError(() => collectObjects(mapRoot, [table]), "CREATION_DRIVEN_FIELD_DECLARED",
  "Variable binding target 'Enabled' is declared more than once.");
});

test("Field binding signatures stay narrow and diagnostics point to handwritten fixture lines", () => {
  const source = [
    'import { componentFields } from "./src/bound-field.js";',
    'import { bind, defineVariables, drive, types, variable } from "./src/variables.js";',
    'import type { CatalogComponentRegistry } from "./src/index.js";',
    'declare module "./src/index.js" {',
    '  interface CatalogComponentRegistry {',
    '    "Synthetic.Device": { membersComplete: true; members: { Enabled: boolean; Count: number }; memberMetadata: {',
    '      Enabled: { kind: "field"; confirmed: true; scalar: "boolean"; fieldInterface: string };',
    '      Count: { kind: "field"; confirmed: true; scalar: "int32"; fieldInterface: string };',
    '    } };',
    '    "Synthetic.Open": { membersComplete: false; members: { Enabled: boolean }; memberMetadata: {',
    '      Enabled: { kind: "field"; confirmed: false; scalar: null; fieldInterface: null };',
    '    } };',
    '    "Synthetic.Unknown": { membersComplete: false; members: { Enabled: boolean } };',
    '  }',
    '}',
    'declare const table: import("./src/catalog-runtime.js").CatalogRuntimeTable;',
    'const vars = defineVariables({ scopeKey: "scope", variables: { on: variable(types.bool, true) } });',
    'const fields = componentFields("Synthetic.Device", table);',
    'fields.Field({ name: "Enabled", value: drive(vars.vars.on) });',
    'fields.Field({ name: "Enabled", value: bind(vars.vars.on) });',
    'fields.Field({ name: "Enabled", value: vars.vars.on });',
    'fields.Field({ name: "Count", value: drive(vars.vars.on) });',
    'variable(types.bool, 1);',
    'const open = componentFields("Synthetic.Open", table);',
    'open.Field({ name: "Enabled", value: drive(vars.vars.on) });',
    'const unknown = componentFields("Synthetic.Unknown", table);',
    'unknown.Field({ name: "Enabled", value: drive(vars.vars.on) });',
  ].join("\n");
  const fileName = `${process.cwd().replace(/\\/g, "/")}/.variables-field-typecheck.ts`;
  const options: ts.CompilerOptions = {
    module: ts.ModuleKind.NodeNext, moduleResolution: ts.ModuleResolutionKind.NodeNext,
    target: ts.ScriptTarget.ES2022, strict: true, skipLibCheck: true, noEmit: true,
  };
  const host = ts.createCompilerHost(options);
  const originalGetSourceFile = host.getSourceFile.bind(host);
  host.getSourceFile = (name, languageVersion, onError, shouldCreateNewSourceFile) =>
    name.replace(/\\/g, "/") === fileName
      ? ts.createSourceFile(name, source, languageVersion, true)
      : originalGetSourceFile(name, languageVersion, onError, shouldCreateNewSourceFile);
  const originalFileExists = host.fileExists.bind(host);
  host.fileExists = name => name.replace(/\\/g, "/") === fileName || originalFileExists(name);
  const program = ts.createProgram([fileName], options, host);
  const diagnostics = ts.getPreEmitDiagnostics(program);
  ok(diagnostics.every(d => d.file?.fileName.replace(/\\/g, "/") === fileName),
    "compiler diagnostics must belong to the handwritten virtual fixture");
  deepEqual(diagnostics.map(d => ({ code: d.code, line: d.file!.getLineAndCharacterOfPosition(d.start!).line + 1 })), [
    { code: 2322, line: 21 }, { code: 2322, line: 22 }, { code: 2345, line: 23 },
    { code: 2322, line: 25 }, { code: 2322, line: 27 },
  ]);
});
