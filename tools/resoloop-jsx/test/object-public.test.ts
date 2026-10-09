import { test } from "node:test";
import { deepEqual, equal } from "node:assert/strict";
import ts from "typescript";
import { Component, componentFields, defineObject, ref } from "resoloop-jsx";
import { Component as DraftComponent, componentFields as draftFields, defineObject as draftDefine, ref as draftRef } from "resoloop-jsx/draft";
import { handleDefinitionIdentity } from "../src/object-refs.js";
import type { FieldRef } from "../src/object-refs.js";

test("main and draft entries share runtime APIs and export exact typed references", () => {
  equal(DraftComponent, Component);
  equal(draftFields, componentFields);
  equal(draftDefine, defineObject);
  equal(draftRef, ref);
  const enabled = "$object-target:enabled" as FieldRef<boolean, "boolean">;
  const Definition = defineObject<{}>()({ exports: { enabled }, body: () => null });
  const handle = Definition.instance("desk");
  const definitionId = handleDefinitionIdentity(handle);
  const expectedJson = `{"v":1,"instanceId":"desk","definitionId":"${definitionId}","name":"enabled"}`;
  equal(ref.export(handle, "enabled"), "$export:" + encodeURIComponent(expectedJson));
  equal(ref.slot("s"), "$slot:s");
  equal(ref.component("c"), "$component:c");
  equal(ref.member("c", "Enabled"), "$member:c.Enabled");
  equal(ref.slotMember("s", "Name"), "$slot-member:s.Name");
  equal(ref.field("a"), "$field:a");
  equal(ref.key("a", "b"), "a::b");
});

test("public and draft entry types preserve typed Object and Field reference boundaries", () => {
  const source = [
    'import { defineObject, componentFields, ref, Component } from "resoloop-jsx";',
    'import type { FieldRef, InstanceHandle, ObjectDefinition, CatalogRuntimeTable, UncheckedReference } from "resoloop-jsx";',
    'import { defineObject as draftDefine, componentFields as draftFields, ref as draftRef, Component as DraftComponent } from "resoloop-jsx/draft";',
    'import type { FieldRef as DFieldRef, InstanceHandle as DHandle, ObjectDefinition as DDefinition, CatalogRuntimeTable as DTable, UncheckedReference as DUnchecked } from "resoloop-jsx/draft";',
    'declare module "resoloop-jsx" { interface CatalogComponentRegistry {',
    '  "Virtual.Device": { membersComplete: true; members: { Enabled: boolean; Count: number; Weight: number; Label: string; B: FieldRef<boolean,"boolean"> | UncheckedReference | null; I: FieldRef<number,"int32"> | UncheckedReference | null; S: FieldRef<string,"string"> | UncheckedReference | null; F: FieldRef<number,"single"> | UncheckedReference | null }; memberMetadata: {',
    '    Enabled: { kind:"field"; confirmed:true; scalar:"boolean"; fieldInterface:string }; Count: { kind:"field"; confirmed:true; scalar:"int32"; fieldInterface:string };',
    '    Weight: { kind:"field"; confirmed:true; scalar:"single"; fieldInterface:string }; Label: { kind:"field"; confirmed:true; scalar:"string"; fieldInterface:string }; B: { kind:"reference"; confirmed:true; scalar:null; fieldInterface:null };',
    '  } };',
    '} }',
    'declare const table: CatalogRuntimeTable;',
    'const fields = componentFields("Virtual.Device", table);',
    'const b = fields.ref("light", "Enabled"), i = fields.ref("light", "Count"), s = fields.ref("light", "Label"), f = fields.ref("light", "Weight");',
    'const Def = defineObject<{ intensity:number }>()({ exports:{ enabled:b }, body:props => null });',
    'const handle = Def.instance("desk");',
    'Def({handle, intensity:1});',
    'const exposed: FieldRef<boolean,"boolean"> = ref.export(handle, "enabled");',
    'Component({key:"connector", type:"Virtual.Device", fields:{B:exposed,I:i,S:s,F:f}, initialFields:{B:b,I:i,S:s,F:f}});',
    'const wide: FieldRef<number> = f;',
    'const raw: UncheckedReference = ref.member("light", "Enabled");',
    'const literal: DUnchecked = "$member:light.Enabled";',
    'DraftComponent({type:"Virtual.Device", fields:{B:raw}});',
    'const draftBoolean: DFieldRef<boolean,"boolean"> = draftFields("Virtual.Device", table).ref("light", "Enabled");',
    'ref.export(handle, "missing");',
    'Def({handle, intensity:"bad"});',
    'Component({key:"bad1",type:"Virtual.Device",fields:{B:f}});',
    'Component({key:"bad2",type:"Virtual.Device",fields:{B:i}});',
    'Component({key:"bad3",type:"Virtual.Device",fields:{B:s}});',
    'Component({key:"bad4",type:"Virtual.Device",initialFields:{I:f}});',
    'const loophole: UncheckedReference = f;',
    'fields.ref("light", "B");',
  ].join("\n");
  const fileName = `${process.cwd().replace(/\\/g, "/")}/.object-public-typecheck.ts`;
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
  equal(diagnostics.every(d => d.file?.fileName.replace(/\\/g, "/") === fileName), true,
    "compiler diagnostics must belong to the handwritten virtual fixture");
  deepEqual(diagnostics.map(d => ({ code: d.code, line: d.file!.getLineAndCharacterOfPosition(d.start!).line + 1 })), [
    { code: 2345, line: 24 }, { code: 2322, line: 25 }, { code: 2322, line: 26 }, { code: 2322, line: 27 },
    { code: 2322, line: 28 }, { code: 2322, line: 29 }, { code: 2322, line: 30 }, { code: 2345, line: 31 },
  ]);
});
