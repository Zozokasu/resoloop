import { test } from "node:test";
import { deepEqual, equal, ok } from "node:assert/strict";
import ts from "typescript";
import { BuildError } from "../src/evaluate.js";
import { buildErrorLocation } from "../src/source-map.js";
import { boundFieldMetadata, checkBoundFieldParent, componentFields, copyBoundFieldMetadata } from "../src/bound-field.js";
import { decodeReference } from "../src/object-refs.js";
import { objectTestRuntimeTable, tableWithOwnType } from "./object-test-helpers.js";

function expectError(action: () => unknown, code: string, message: string): BuildError {
  let captured: unknown;
  try { action(); } catch (error) { captured = error; }
  if (!(captured instanceof BuildError)) throw new Error("Expected a BuildError");
  equal(captured.code, code);
  equal(captured.message, message);
  return captured;
}

test("catalog fields bind canonical metadata, exact scalar references, and parent checks", () => {
  const table = objectTestRuntimeTable();
  const bound = componentFields("Synthetic.Device" as never, table) as any;
  const field = bound.Field({ name: "Enabled", value: true }) as object;
  const metadata = boundFieldMetadata(field)!;
  equal(metadata.type, "Synthetic.Device");
  equal(metadata.member, "Enabled");
  equal(metadata.catalogHash, "handwritten-runtime-hash");
  equal(metadata.descriptor.scalar, "boolean");
  const copied = { ...field };
  equal(boundFieldMetadata(copied), undefined);
  copyBoundFieldMetadata(field, copied);
  deepEqual(boundFieldMetadata(copied), metadata);
  const alias = componentFields("Synthetic.DeviceAlias" as never, table) as any;
  const aliasField = alias.Field({ name: "Enabled", value: true }) as object;
  equal(boundFieldMetadata(aliasField)?.type, "Synthetic.Device");
  const token = alias.ref("light", "Enabled");
  equal(token, "$object-target:" + encodeURIComponent(
    '{"v":1,"selector":"$member:light.Enabled","componentType":"Synthetic.Device","member":"Enabled","representation":"boolean","catalogHash":"handwritten-runtime-hash"}'
  ));
  deepEqual(decodeReference(token, new Map()), {
    kind: "target", selector: "$member:light.Enabled", componentType: "Synthetic.Device",
    member: "Enabled", representation: "boolean", catalogHash: "handwritten-runtime-hash",
  });
  for (const [member, representation] of [["Count", "int32"], ["Label", "string"], ["Weight", "single"]] as const) {
    const json = member === "Count"
      ? '{"v":1,"selector":"$member:light.Count","componentType":"Synthetic.Device","member":"Count","representation":"int32","catalogHash":"handwritten-runtime-hash"}'
      : member === "Label"
        ? '{"v":1,"selector":"$member:light.Label","componentType":"Synthetic.Device","member":"Label","representation":"string","catalogHash":"handwritten-runtime-hash"}'
        : '{"v":1,"selector":"$member:light.Weight","componentType":"Synthetic.Device","member":"Weight","representation":"single","catalogHash":"handwritten-runtime-hash"}';
    const reference = alias.ref("light", member);
    equal(reference, "$object-target:" + encodeURIComponent(json));
    const decoded = decodeReference(reference, new Map());
    deepEqual(decoded?.kind === "target" ? { kind: decoded.kind, componentType: decoded.componentType, member: decoded.member,
      representation: decoded.representation, catalogHash: decoded.catalogHash } : undefined, {
      kind: "target", componentType: "Synthetic.Device", member, representation,
      catalogHash: "handwritten-runtime-hash",
    });
  }
  checkBoundFieldParent(field, "Synthetic.DeviceAlias", [table]);
  const otherType = tableWithOwnType("Other.Type", table.types["Synthetic.Device"]);
  const mismatch = expectError(() => checkBoundFieldParent(field, "Other.Type", [otherType]),
    "APPLY_FIELD_TYPE_MISMATCH", "Bound Field requires Component 'Synthetic.Device', observed 'Other.Type'.");
  equal(buildErrorLocation(mismatch), "unknown");
  expectError(() => checkBoundFieldParent(field, "Synthetic.Device", []), "APPLY_CATALOG_UNAVAILABLE",
    "Type 'Synthetic.Device', member 'Enabled': runtime catalog evidence is missing or unconfirmed.");
  equal(bound.Field({ name: "Enabled", mode: "runtime" }) !== undefined, true);
});

test("catalog fields reject unknown evidence and values with fixed diagnostics", () => {
  const table = objectTestRuntimeTable();
  const bound = componentFields("Synthetic.Device" as never, table) as any;
  expectError(() => bound.Field({ name: "Missing", mode: "runtime" } as never), "APPLY_CATALOG_UNAVAILABLE",
    "Type 'Synthetic.Device', member 'Missing': runtime catalog evidence is missing or unconfirmed.");
  expectError(() => componentFields("Missing.Type" as never, table), "APPLY_CATALOG_UNAVAILABLE",
    "Type 'Missing.Type', member '<type>': runtime catalog evidence is missing or unconfirmed.");
  expectError(() => bound.ref("light", "Uncertain" as never), "APPLY_CATALOG_UNAVAILABLE",
    "Type 'Synthetic.Device', member 'Uncertain': runtime catalog evidence is missing or unconfirmed.");
  expectError(() => bound.ref("light", "Target" as never), "APPLY_CATALOG_UNAVAILABLE",
    "Type 'Synthetic.Device', member 'Target': runtime catalog evidence is missing or unconfirmed.");
  expectError(() => componentFields("Synthetic.Device" as never, undefined as never), "APPLY_CATALOG_UNAVAILABLE",
    "Type 'Synthetic.Device', member '<type>': runtime catalog evidence is missing or unconfirmed.");
  const malformed = JSON.parse(JSON.stringify(objectTestRuntimeTable())) as ReturnType<typeof objectTestRuntimeTable>;
  (malformed.types["Synthetic.Device"].members.Enabled as any).scalar = "double";
  expectError(() => componentFields("Synthetic.Device" as never, malformed), "APPLY_CATALOG_TYPES_HASH_MISMATCH",
    "Catalog runtime table does not match format/generator/descriptor shape; regenerate catalog types.");
  const mismatch = expectError(() => bound.Field({ name: "Enabled", value: 1 } as never), "APPLY_OBJECT_TYPE_MISMATCH",
    "Object reference type mismatch: expected 'value:boolean', observed 'value:number'.");
  equal(buildErrorLocation(mismatch), "unknown");
  for (const [representation, value, observed] of [
    ["int32", false, "boolean"], ["string", null, "null"], ["single", [], "array"],
  ] as const) {
    const member = representation === "int32" ? "Count" : representation === "string" ? "Label" : "Weight";
    const error = expectError(() => bound.Field({ name: member, value } as never), "APPLY_OBJECT_TYPE_MISMATCH",
      `Object reference type mismatch: expected 'value:${representation}', observed 'value:${observed}'.`);
    equal(buildErrorLocation(error), "unknown");
  }
});

test("bound field signatures use catalog completeness and representations", () => {
  const source = [
    'import { componentFields } from "./src/bound-field.js";',
    'import type { CatalogComponentRegistry } from "./src/index.js";',
    'declare module "./src/index.js" {',
    '  interface CatalogComponentRegistry {',
    '    "Synthetic.Device": { membersComplete: true; members: { Enabled: boolean; Weight: number; Count: number; Label: string; Target: string | null }; memberMetadata: {',
    '      Enabled: { kind: "field"; confirmed: true; scalar: "boolean"; fieldInterface: string };',
    '      Weight: { kind: "field"; confirmed: true; scalar: "single"; fieldInterface: string };',
    '      Count: { kind: "field"; confirmed: true; scalar: "int32"; fieldInterface: string };',
    '      Label: { kind: "field"; confirmed: true; scalar: "string"; fieldInterface: string };',
    '      Hidden: { kind: "field"; confirmed: false; scalar: null; fieldInterface: null };',
    '      Nullable: { kind: "field"; confirmed: true; scalar: "boolean" | null; fieldInterface: string | null };',
    '      Target: { kind: "reference"; confirmed: true; scalar: null; fieldInterface: null };',
    '    } };',
    '    "Synthetic.Open": { membersComplete: false; members: { Enabled: boolean }; memberMetadata: {',
    '      Enabled: { kind: "field"; confirmed: true; scalar: "boolean"; fieldInterface: string };',
    '    } };',
    '  }',
    '}',
    'declare const table: import("./src/catalog-runtime.js").CatalogRuntimeTable;',
    'const fields = componentFields("Synthetic.Device", table);',
    'fields.Field({ name: "Enabled", value: true });',
    'fields.Field({ name: "Weight", value: 1.5 });',
    'fields.Field({ name: "Enabled", mode: "runtime" });',
    'fields.Field({ name: "Enabled", mode: "driver-owned" });',
    'fields.ref("light", "Enabled");',
    'fields.ref("light", "Weight");',
    'const open = componentFields("Synthetic.Open", table);',
    'open.Field({ name: "Future", mode: "runtime", value: { x: 1 } });',
    'fields.Field({ name: "Enabeld", value: true });',
    'fields.Field({ name: "Enabled", value: 1 });',
    'fields.Field({ name: "Weight", value: false });',
    'fields.Field({ name: "Enabled" });',
    'fields.Field({ name: "Enabled", mode: "initial" });',
    'fields.ref("light", "Hidden");',
    'fields.ref("light", "Nullable");',
    'fields.ref("light", "Missing");',
    'fields.ref("light", "Target");',
    'declare const wrongRep: import("./src/object-refs.js").FieldRef<number, "int32">;',
    'const mismatch: import("./src/object-refs.js").FieldRef<number, "int32"> = fields.ref("light", "Weight");',
    'const booleanRef: import("./src/object-refs.js").FieldRef<boolean, "boolean"> = fields.ref("light", "Enabled");',
    'const int32Ref: import("./src/object-refs.js").FieldRef<number, "int32"> = fields.ref("light", "Count");',
    'const stringRef: import("./src/object-refs.js").FieldRef<string, "string"> = fields.ref("light", "Label");',
    'const singleRef: import("./src/object-refs.js").FieldRef<number, "single"> = fields.ref("light", "Weight");',
  ].join("\n");
  const fileName = `${process.cwd().replace(/\\/g, "/")}/.bound-field-typecheck.ts`;
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
    { code: 2820, line: 29 }, { code: 2322, line: 30 }, { code: 2322, line: 31 },
    { code: 2345, line: 32 }, { code: 2345, line: 33 }, { code: 2345, line: 34 },
    { code: 2345, line: 35 }, { code: 2345, line: 36 }, { code: 2345, line: 37 },
    { code: 2322, line: 39 },
  ]);
});
