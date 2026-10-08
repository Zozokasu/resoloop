import { test } from "node:test";
import { deepEqual, equal, ok, throws } from "node:assert/strict";
import * as ts from "typescript";
import { BuildError } from "../src/evaluate.js";
import {
  createHandle, decodeReference, exportReference, handleDefinitionIdentity, newDefinitionIdentity,
  targetReference, type DecodedReference, type FieldRef, type PublicRefs, type UncheckedReference,
} from "../src/object-refs.js";

const targetPrefix = "$object-target:";
const exportPrefix = "$export:";
const cache = () => new Map<string, DecodedReference>();

test("typed target emits the ordered self-contained envelope and decodes with caller cache", () => {
  const token = targetReference<boolean, "boolean">(
    "light", "Enabled", "[FrooxEngine]FrooxEngine.Light",
    { scalar: "boolean", fieldInterface: "Boolean" }, "catalog-1",
  );
  const expectedJson = '{"v":1,"selector":"$member:light.Enabled","componentType":"[FrooxEngine]FrooxEngine.Light","member":"Enabled","representation":"boolean","catalogHash":"catalog-1"}';
  equal(token, targetPrefix + encodeURIComponent(expectedJson));
  const local = cache();
  const decoded = decodeReference(token, local);
  deepEqual(decoded, {
    kind: "target", selector: "$member:light.Enabled", componentType: "[FrooxEngine]FrooxEngine.Light",
    member: "Enabled", representation: "boolean", catalogHash: "catalog-1",
  });
  equal(decodeReference(token, local), decoded);
  equal(local.size, 1);
  equal(decodeReference(token, cache())?.kind, "target");
});

test("handle snapshots exports, hides its brand, and distinguishes definitions with same public shape", () => {
  const source = { Enabled: "typed-enabled" };
  const first = createHandle("lamp", source, newDefinitionIdentity());
  const second = createHandle("lamp", source, newDefinitionIdentity());
  source.Enabled = "mutated";
  equal(first.exports.Enabled, "typed-enabled");
  ok(Object.isFrozen(first));
  ok(Object.isFrozen(first.exports));
  equal(Object.keys(first).includes("id"), true);
  equal(Object.getOwnPropertySymbols(first).length, 1);
  equal(Object.getOwnPropertyDescriptor(first, Object.getOwnPropertySymbols(first)[0])?.enumerable, false);
  ok(Object.isFrozen(first) && Object.isFrozen(first.exports));
  ok(handleDefinitionIdentity(first) !== handleDefinitionIdentity(second));
  const tokenA = exportReference(first, "Enabled");
  const tokenB = exportReference(second, "Enabled");
  ok(tokenA.startsWith(exportPrefix));
  ok(tokenA !== tokenB);
  deepEqual(decodeReference(tokenA, cache()), {
    kind: "export", instanceId: "lamp", definitionId: handleDefinitionIdentity(first), name: "Enabled",
  });
});

test("export lookup requires own declared names and keeps name spelling", () => {
  const handle = createHandle("unit_1", { " Input ": "ref" }, "definition-x");
  equal(decodeReference(exportReference(handle, " Input "), cache())?.kind, "export");
  throws(() => exportReference(handle, "missing" as " Input "), (error: unknown) =>
    error instanceof BuildError && error.code === "APPLY_OBJECT_EXPORT_UNKNOWN" &&
    error.message === "Unknown Object export 'unit_1.missing'.");
  throws(() => exportReference(handle, "   " as " Input "), (error: unknown) =>
    error instanceof BuildError && error.code === "APPLY_OBJECT_EXPORT_INVALID" &&
    error.message === "Object 'unit_1' export '   ': export name must be nonempty.");
  throws(() => exportReference(handle, "bad\nname" as " Input "), (error: unknown) =>
    error instanceof BuildError && error.code === "APPLY_OBJECT_EXPORT_INVALID" &&
    error.message === "Object 'unit_1' export 'bad\nname': export name must be nonempty.");
  throws(() => createHandle("bad id", {}, "definition-x"), (error: unknown) =>
    error instanceof BuildError && error.message === "Object 'bad id': invalid instance ID.");
});

test("unbranded handles fail and ordinary source selectors remain unchanged", () => {
  throws(() => handleDefinitionIdentity({ id: "fake", exports: {} } as never), (error: unknown) =>
    error instanceof BuildError && error.code === "APPLY_OBJECT_INVALID" &&
    error.message === "Object '<invalid>': handle is missing or unbranded.");
  deepEqual(decodeReference("$member:light.Enabled", cache()), undefined);
  deepEqual(decodeReference("$field:target", cache()), undefined);
});

function encoded(prefix: string, value: unknown): string {
  return prefix + encodeURIComponent(JSON.stringify(value));
}

test("decoder rejects malformed target syntax, keys, versions, selectors, and representations", () => {
  const base = { v: 1, selector: "$member:light.Enabled", componentType: "Light", member: "Enabled", representation: "boolean", catalogHash: "hash" };
  const invalid = [
    targetPrefix + "%", // malformed URI
    encoded(targetPrefix, { ...base, extra: true }),
    encoded(targetPrefix, { ...base, v: 2 }),
    encoded(targetPrefix, { ...base, selector: "$member:.Enabled" }),
    encoded(targetPrefix, { ...base, selector: "$member:light.Other" }),
    encoded(targetPrefix, { ...base, representation: "double" }),
    encoded(targetPrefix, { ...base, member: "" }),
    encoded(targetPrefix, Object.fromEntries(Object.entries(base).filter(([key]) => key !== "catalogHash"))),
  ];
  for (const token of invalid) throws(() => decodeReference(token, cache()), (error: unknown) =>
    error instanceof BuildError && error.code === "APPLY_OBJECT_EXPORT_INVALID" &&
    error.message === "Object '<invalid>' export '<invalid>': malformed typed target token.");
});

test("decoder rejects malformed export payloads without granting access", () => {
  const valid = { v: 1, instanceId: "lamp", definitionId: "definition-1", name: "Enabled" };
  for (const payload of [
    { ...valid, extra: true }, { ...valid, v: 2 }, { ...valid, instanceId: "bad id" },
    { ...valid, name: "  " }, { ...valid, definitionId: "" },
  ]) throws(() => decodeReference(encoded(exportPrefix, payload), cache()), (error: unknown) =>
    error instanceof BuildError && error.code === "APPLY_OBJECT_EXPORT_UNKNOWN" &&
    error.message === "Unknown Object export '<invalid>'.");
});

test("public reference types preserve scalar representation, invariance, and raw compatibility", () => {
  const source = [
    'import type { FieldRef, UncheckedReference } from "./src/object-refs.js";',
    'import { ref } from "./src/ref.js";',
    'type Receiver = FieldRef<boolean, "boolean"> | UncheckedReference | null;',
    'declare function accept(value: Receiver): void;',
    'declare const boolRef: FieldRef<boolean, "boolean">;',
    'declare const intRef: FieldRef<number, "int32">;',
    'declare const singleRef: FieldRef<number, "single">;',
    'declare const stringRef: FieldRef<string, "string">;',
    'declare const literalRef: FieldRef<true, "boolean">;',
    'const boolCheck: FieldRef<boolean, "boolean"> = boolRef;',
    'const intCheck: FieldRef<number> = intRef;',
    'const singleCheck: FieldRef<number> = singleRef;',
    'const stringCheck: FieldRef<string> = stringRef;',
    'const rawMember: UncheckedReference = "$member:light.Enabled";',
    'const rawField: UncheckedReference = "$field:target";',
    'const plain: UncheckedReference = "raw reference";',
    'const refMember: UncheckedReference = ref.member("light", "Enabled");',
    'const refField: UncheckedReference = ref.field("target");',
    'accept(null);',
    'accept("$member:light.Enabled");',
    'accept(intRef);',
    'accept(singleRef);',
    'accept(stringRef);',
    'const literalInvariant: FieldRef<boolean, "boolean"> = literalRef;',
    'declare function acceptInt(value: FieldRef<number, "int32">): void;',
    'acceptInt(singleRef);',
    'declare function acceptNumber(value: FieldRef<number>): void;',
    'acceptNumber(boolRef);',
    'declare function acceptString(value: FieldRef<string>): void;',
    'acceptString(boolRef);',
    'const boolInvariantNumber: FieldRef<number> = boolRef;',
    'const literalInvariantReverse: FieldRef<true, "boolean"> = boolRef;',
  ].join("\n");
  const fileName = `${process.cwd().replace(/\\/g, "/")}/.object-refs-typecheck.ts`;
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
    "all compiler diagnostics must belong to the virtual source fixture");
  deepEqual(diagnostics.map(d => ({ code: d.code, ...d.file!.getLineAndCharacterOfPosition(d.start!) })), [
    { code: 2345, line: 20, character: 7 },
    { code: 2345, line: 21, character: 7 },
    { code: 2345, line: 22, character: 7 },
    { code: 2322, line: 23, character: 6 },
    { code: 2345, line: 25, character: 10 },
    { code: 2345, line: 27, character: 13 },
    { code: 2345, line: 29, character: 13 },
    { code: 2322, line: 30, character: 6 },
    { code: 2322, line: 31, character: 6 },
  ]);
  ok(diagnostics.every(d => d.code === 2345 || d.code === 2322));
});
