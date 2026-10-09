import { test } from "node:test";
import { deepEqual, equal, ok, throws } from "node:assert/strict";
import ts from "typescript";
import { BuildError } from "../src/evaluate.js";
import { defineObject } from "../src/object-definition.js";
import type { ObjectElement } from "../src/elements.js";
import { captureElement, originOfElement, sourceElement, type ElementOrigin } from "../src/source-map.js";
import type { FieldRef, InstanceHandle } from "../src/object-refs.js";

test("Object definitions snapshot exports, retain descriptor identity, and capture body elements", () => {
  const enabled = "$object-target:enabled" as FieldRef<boolean, "boolean">;
  const exports = { enabled };
  let bodyProps: { intensity: number; name: string; handle: InstanceHandle<typeof exports> } | undefined;
  const Definition = defineObject<{ intensity: number; name: string }>()({
    exports,
    body: props => { bodyProps = props; return null; },
  });
  const origin: ElementOrigin = { source: { status: "known", file: "fixture.ts", sha256: "a".repeat(64),
    range: { start: { offset: 0, line: 1, column: 1 }, end: { offset: 1, line: 1, column: 2 } } }, attributes: {} };
  sourceElement(() => { captureElement(Definition); return Definition; }, origin);
  const element = sourceElement(() => Definition({ handle: Definition.instance("lamp"), intensity: 1.5, name: "Lamp" }), origin) as ObjectElement;
  const descriptor = element.props.definition;
  exports.enabled = "changed" as FieldRef<boolean, "boolean">;
  equal(descriptor.exports.enabled, enabled);
  ok(Object.isFrozen(descriptor));
  ok(Object.isFrozen(descriptor.exports));
  ok(Object.isFrozen(Definition));
  equal(element.props.handle.id, "lamp");
  equal(bodyProps?.handle, element.props.handle);
  equal(bodyProps?.intensity, 1.5);
  equal(bodyProps?.name, "Lamp");
  deepEqual(originOfElement(descriptor), originOfElement(Definition));
  equal(originOfElement(element)?.source.status, "known");
});

test("Object instances validate IDs and keep their original definition descriptor", () => {
  const Definition = defineObject<{}>()({ exports: {}, body: () => null });
  const Other = defineObject<{}>()({ exports: {}, body: () => null });
  const own = Definition.instance("desk-lamp");
  const max = Definition.instance("A".repeat(64));
  equal(own.id, "desk-lamp");
  equal(max.id.length, 64);
  ok(Object.isFrozen(own) && Object.isFrozen(own.exports));
  const ownElement = Definition({ handle: own }) as ObjectElement;
  const foreign = Other.instance("other");
  const foreignElement = Definition({ handle: foreign as never }) as ObjectElement;
  equal(ownElement.props.definition, foreignElement.props.definition);
  ok(ownElement.props.definition !== (Other({ handle: Other.instance("other") }) as ObjectElement).props.definition);
  equal(foreignElement.props.handle, foreign);
  const fake = { id: "fake", exports: {} } as InstanceHandle<Record<string, string>>;
  const fakeElement = Definition({ handle: fake as never }) as ObjectElement;
  equal(fakeElement.props.definition, ownElement.props.definition);
  equal(fakeElement.props.handle, fake);
  for (const id of ["", "A".repeat(65), "bad:id", "bad.name", "bad id", "日本語"]) {
    throws(() => Definition.instance(id), (error: unknown) => error instanceof BuildError &&
      error.code === "APPLY_OBJECT_INVALID" && error.message === `Object '${id}': invalid instance ID.`);
  }
});

test("Object definition and exports use exact inferred TypeScript contracts", () => {
  const source = [
    'import { defineObject } from "./src/object-definition.js";',
    'import { exportReference } from "./src/object-refs.js";',
    'import type { FieldRef } from "./src/object-refs.js";',
    'declare const enabled: FieldRef<boolean, "boolean">;',
    'const Def = defineObject<{ intensity: number; name: string }>()({ exports: { enabled }, body: props => { const x: number = props.intensity; const n: string = props.name; return null; } });',
    'const handle = Def.instance("lamp");',
    'Def({ handle, intensity: 1.5, name: "Lamp" });',
    'const ref: FieldRef<boolean, "boolean"> = exportReference(handle, "enabled");',
    'const wide: FieldRef<boolean> = ref;',
    'Def({ handle, intensity: 1.5 });',
    'Def({ handle, intensity: "bad", name: "Lamp" });',
    'defineObject<{ handle: string }>()({ exports: {}, body: () => null });',
    'exportReference(handle, "missing");',
    'const mismatch: FieldRef<number, "int32"> = ref;',
    'const Other = defineObject<{}>()({ exports: { enabled }, body: () => null });',
    'const otherHandle = Other.instance("other");',
    'Def({ handle: otherHandle, intensity: 1, name: "same shape is allowed statically" });',
    'export {};',
  ].join("\n");
  const fileName = `${process.cwd().replace(/\\/g, "/")}/.object-definition-typecheck.ts`;
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
    { code: 2345, line: 10 }, { code: 2322, line: 11 }, { code: 2345, line: 12 },
    { code: 2345, line: 13 }, { code: 2322, line: 14 },
  ]);
});
