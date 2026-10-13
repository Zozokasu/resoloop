import { test } from "node:test";
import { deepEqual, equal, ok, throws } from "node:assert/strict";
import ts from "typescript";
import * as publicApi from "../src/variables.js";
import { BuildError } from "../src/evaluate.js";
import {
  bind, defineVariables, drive, types, variable,
} from "../src/variables.js";
import type { Kind, VariableRef, VariableType } from "../src/variables.js";
import { fieldBindingDescriptor, variableRefDescriptor, variableSetDescriptor } from "../src/variables-internal.js";

function expectError(action: () => unknown, code: string, message: string): void {
  throws(action, (error: unknown) => error instanceof BuildError && error.code === code && error.message === message);
}

test("variables subpath exports only its six public runtime names", () => {
  deepEqual(Object.keys(publicApi).sort(), ["Variables", "bind", "defineVariables", "drive", "types", "variable"].sort());
});

test("bool declarations, references, bindings, and descriptors are frozen and identity-bound", () => {
  const declaration = variable(types.bool, true);
  const set = defineVariables({ scopeKey: "state", variables: { on: declaration, off: variable(types.bool, false) } });
  equal(declaration.defaultValue, true);
  equal(set.vars.on.kind, "bool");
  equal(variableRefDescriptor(set.vars.on)?.name, "on");
  equal(variableRefDescriptor(set.vars.on)?.set, set);
  equal(variableSetDescriptor(set)?.set, set);
  equal(variableSetDescriptor(set)?.variables[0].defaultValue, true);
  const driven = drive(set.vars.on);
  const bound = bind(set.vars.on);
  equal(driven.mode, "drive");
  equal(bound.mode, "bind");
  equal(driven.ref, set.vars.on);
  equal(bound.ref, set.vars.on);
  equal(fieldBindingDescriptor(driven)?.ref, set.vars.on);
  equal(fieldBindingDescriptor(bound)?.mode, "bind");
  ok(Object.isFrozen(declaration));
  ok(Object.isFrozen(set));
  ok(Object.isFrozen(set.vars));
  ok(Object.isFrozen(set.vars.on));
  ok(Object.isFrozen(driven));
  equal(variableSetDescriptor({ ...set }), undefined);
  equal(variableRefDescriptor({ ...set.vars.on }), undefined);
  equal(fieldBindingDescriptor({ ...driven }), undefined);
  equal(variableSetDescriptor(JSON.parse(JSON.stringify(set))), undefined);
});

test("variable identifiers and unsupported or mismatched bool declarations use fixed errors", () => {
  expectError(() => defineVariables({ scopeKey: "bad key", variables: {} }), "CREATION_ID_INVALID",
    "Variable identifier 'bad key' must match ^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$." );
  expectError(() => defineVariables({ scopeKey: 1 as never, variables: {} }), "CREATION_ID_INVALID",
    "Variable identifier '1' must match ^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$." );
  expectError(() => defineVariables({ scopeKey: "ok", variables: { "bad.key": variable(types.bool, true) } }),
    "CREATION_ID_INVALID", "Variable identifier 'bad.key' must match ^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$." );
  expectError(() => variable({ kind: "int32" } as VariableType<"int32">, 1), "CREATION_VARIABLE_TYPE_UNSUPPORTED",
    "Variable type 'int32' is unsupported; only bool is supported.");
  expectError(() => variable(types.bool, 1 as never), "CREATION_VARIABLE_TYPE_MISMATCH",
    "Variable default for 'bool' must be boolean.");
  const mutableType = { kind: "bool" as "bool" | "int32" };
  const stableDeclaration = variable(mutableType, true);
  mutableType.kind = "int32";
  equal(stableDeclaration.type.kind, "bool");
  ok(Object.isFrozen(stableDeclaration.type));
  expectError(() => drive({ kind: "bool" } as VariableRef<"bool">), "CREATION_VARIABLE_UNRESOLVED",
    "Variable reference is unresolved; use a reference from defineVariables.");
  const inherited = Object.create({ inherited: variable(types.bool, true) }) as Record<string, ReturnType<typeof variable>>;
  const ownOnly = defineVariables({ scopeKey: "own", variables: inherited });
  deepEqual(Object.keys(ownOnly.vars), []);
  const ownProto = Object.create(null) as Record<string, ReturnType<typeof variable>>;
  Object.defineProperty(ownProto, "__proto__", { value: variable(types.bool, true), enumerable: true });
  expectError(() => defineVariables({ scopeKey: "own", variables: ownProto }), "CREATION_ID_INVALID",
    "Variable identifier '__proto__' must match ^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$." );
});

test("only bool is exposed and generic API types preserve literal kinds", () => {
  deepEqual(Object.keys(types), ["bool"]);
  ok(Object.isFrozen(types));
  ok(Object.isFrozen(types.bool));
  const source = [
    'import { types, variable, defineVariables, drive } from "./src/variables.js";',
    'import type { Kind, VariableRef, VariableType } from "./src/variables.js";',
    'import type { JsonValue } from "./src/elements.js";',
    'const good = variable(types.bool, true);',
    'const badDefault = variable(types.bool, 1);',
    'const future: VariableType<"int32"> = { kind: "int32" };',
    'const expanded: Kind = future.kind;',
    'const vars = defineVariables({ scopeKey: "scope", variables: { on: good } });',
    'const boolRef: VariableRef<"bool"> = vars.vars.on;',
    'const widened: VariableRef<Kind> = vars.vars.on;',
    'const json: JsonValue = boolRef;',
    'const binding = drive(vars.vars.on);',
    'const wrongKind: VariableRef<"int32"> = vars.vars.on;',
    'void [badDefault, expanded, widened, json, binding, wrongKind];',
  ].join("\n");
  const fileName = `${process.cwd().replace(/\\/g, "/")}/.variables-api-typecheck.ts`;
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
    { code: 2345, line: 5 }, { code: 2322, line: 10 }, { code: 2322, line: 11 }, { code: 2322, line: 13 },
  ]);
});
