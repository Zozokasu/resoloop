import { test } from "node:test";
import { deepEqual, equal } from "node:assert/strict";
import { collectObjects, validateObjectBoundaries } from "../src/object-expand.js";
import { validateObjectExports } from "../src/object-boundary.js";
import { defineObject } from "../src/object-definition.js";
import { exportReference, targetReference, type ScalarRepresentation } from "../src/object-refs.js";
import { BuildError } from "../src/evaluate.js";
import type { CatalogMemberRuntime, CatalogRuntimeTable } from "../src/catalog-runtime.js";
import { buildErrorOrigin, captureElement, sourceElement, type ElementOrigin, type Origin, type Source } from "../src/source-map.js";
import { objectTestRuntimeTable } from "./object-test-helpers.js";

const slot = (key: string, children?: unknown) => ({ kind: "slot", props: { key, name: key, children } } as any);
const component = (key: string, props: Record<string, unknown> = {}) =>
  ({ kind: "component", props: { key, type: "Synthetic.Device", ...props } } as any);
const field = (name: string, value: unknown, mode = "runtime") => ({ kind: "field", props: { name, mode, value } } as any);
const fragment = (children: unknown) => ({ kind: "fragment", props: { children } } as any);
const scope = (instanceKey: string, children: unknown) => ({ kind: "scope", props: { instanceKey, children } } as any);
const reps: ScalarRepresentation[] = ["boolean", "int32", "string", "single"];
const sourceMembers: Record<ScalarRepresentation, string> = {
  boolean: "Enabled", int32: "Count", string: "Label", single: "Weight",
};
const sampleValues: Record<ScalarRepresentation, unknown> = {
  boolean: true, int32: 9, string: "lamp", single: 1.25,
};
const valueType: Record<ScalarRepresentation, string> = {
  boolean: "bool", int32: "int", string: "string", single: "float",
};

function table(receiver: ScalarRepresentation = "boolean", contentHash = "handwritten-runtime-hash"): CatalogRuntimeTable {
  const base = objectTestRuntimeTable();
  const device = base.types["Synthetic.Device"];
  const target = { ...device.members.Target!, fieldTarget: {
    representation: receiver, valueType: valueType[receiver],
    targetType: `[FrooxEngine]FrooxEngine.IField<${valueType[receiver]}>`,
  } } as CatalogMemberRuntime;
  const members = { ...device.members, Target: target };
  const canonical = { ...device, members };
  return { ...base, contentHash, types: {
    "Synthetic.Device": canonical,
    "Synthetic.DeviceAlias": { ...canonical },
  } };
}

function tableWithOtherCanonical(base: CatalogRuntimeTable): CatalogRuntimeTable {
  const device = base.types["Synthetic.Device"];
  return { ...base, types: { ...base.types, "Synthetic.Other": { ...device, canonicalType: "Synthetic.Other" } } };
}

function conflictingSourceTable(base: CatalogRuntimeTable): CatalogRuntimeTable {
  const device = base.types["Synthetic.Device"];
  const enabled = { ...device.members.Enabled!, scalar: "string" as const,
    fieldInterface: "[FrooxEngine]FrooxEngine.IField<string>" } as CatalogMemberRuntime;
  const members = { ...device.members, Enabled: enabled };
  const canonical = { ...device, members };
  return { ...base, types: { ...base.types, "Synthetic.Device": canonical,
    "Synthetic.DeviceAlias": { ...canonical } } };
}

function object(id: string, body: unknown, exported: string): any {
  const definition = defineObject<{}>()({ exports: { target: exported }, body: () => body as never });
  return definition({ handle: definition.instance(id) });
}

function prepare(root: unknown, catalogs: readonly CatalogRuntimeTable[] = []) {
  const context = collectObjects(root, catalogs);
  validateObjectExports(context);
  validateObjectBoundaries(context);
  return context;
}

function token(selector: string, type: string, member: string, representation: ScalarRepresentation, hash: string): string {
  return "$object-target:" + encodeURIComponent(JSON.stringify({ v: 1, selector,
    componentType: type, member, representation, catalogHash: hash }));
}

function failure(action: () => unknown, code: string, message: string): BuildError {
  let caught: unknown;
  try { action(); } catch (error) { caught = error; }
  if (!(caught instanceof BuildError)) throw new Error("Expected BuildError");
  equal(caught.code, code);
  equal(caught.message, message);
  return caught;
}

function known(file: string, offset: number): Source {
  return { status: "known", file, sha256: "b".repeat(64), range: {
    start: { offset, line: 2, column: offset + 1 }, end: { offset: offset + 1, line: 2, column: offset + 2 },
  } };
}

const origin = (children: Record<string, Origin>): Origin => ({ source: { status: "unknown" }, children });

function sourcedComponent(props: Record<string, unknown>, valueSource: Source): unknown {
  const element = component("reader", props);
  const metadata: ElementOrigin = { source: known("consumer.tsx", 0), attributes: {
    fields: origin({ Target: { source: known("target-property.tsx", 24), valueSource } }),
  } };
  return sourceElement(() => { captureElement(element); return element; }, metadata, props);
}

function publicReference(sourceRep: ScalarRepresentation, receiveRep: ScalarRepresentation,
  opts: { type?: string; hash?: string; table?: CatalogRuntimeTable } = {}): unknown {
  const evidence = opts.table ?? table(receiveRep);
  const member = sourceMembers[sourceRep];
  const descriptor = evidence.types["Synthetic.Device"].members[member]!;
  const sourceToken = targetReference<any, ScalarRepresentation>("lamp", member, opts.type ?? "Synthetic.Device",
    { scalar: sourceRep, fieldInterface: descriptor.fieldInterface! }, opts.hash ?? evidence.contentHash);
  const placed = object("desk", slot("root", [component("lamp", { fields: { [member]: sampleValues[sourceRep] } })]), sourceToken);
  return { placed, table: evidence };
}

test("four catalog representations resolve exactly through public Object exports", () => {
  for (const representation of reps) {
    const pair = publicReference(representation, representation) as { placed: any; table: CatalogRuntimeTable };
    const publicToken = (awaitlessExport(pair.placed, "target"));
    const root = slot("page", [pair.placed, component("reader", { fields: { Target: publicToken } })]);
    const context = prepare(root, [pair.table]);
    const tree = context.root as { props: { children: Array<{ props: { fields?: Record<string, unknown> } }> } };
    equal(tree.props.children[1].props.fields?.Target, `$member:desk::lamp.${sourceMembers[representation]}`);
  }
});

function awaitlessExport(placement: any, name: string): string {
  return exportReference(placement.props.handle, name);
}

test("four representations reject receiver mismatches and numeric cross-conversion", () => {
  for (const expected of reps) for (const actual of reps) {
    if (expected === actual) continue;
    const pair = publicReference(actual, expected) as { placed: any; table: CatalogRuntimeTable };
    const exported = awaitlessExport(pair.placed, "target");
    failure(() => prepare(slot("page", [pair.placed, component("reader", { fields: { Target: exported } })]),
      [pair.table]), "APPLY_OBJECT_TYPE_MISMATCH",
    `Object reference type mismatch: expected 'field:${expected}', observed 'field:${actual}'.`);
  }
});

test("typed target claims must match canonical Component, member, representation and catalog hash", () => {
  const base = table("boolean");
  const root = (claim: string) => slot("page", [
    component("lamp", { fields: { Enabled: true } }),
    component("reader", { fields: { Target: claim } }),
  ]);
  const wrongRep = token("$member:lamp.Enabled", "Synthetic.Device", "Enabled", "string", base.contentHash);
  const mismatchSource = known("typed-value.tsx", 30);
  const mismatchError = failure(() => prepare(slot("page", [component("lamp", { fields: { Enabled: true } }),
    sourcedComponent({ fields: { Target: wrongRep } }, mismatchSource)]), [base]), "APPLY_OBJECT_TYPE_MISMATCH",
    "Object reference type mismatch: expected 'field:string', observed 'field:boolean'.");
  deepEqual(buildErrorOrigin(mismatchError)?.source, mismatchSource);
  const wrongCanonical = token("$member:lamp.Enabled", "Synthetic.Other", "Enabled", "boolean", base.contentHash);
  const withOther = tableWithOtherCanonical(base);
  failure(() => prepare(root(wrongCanonical), [withOther]), "APPLY_OBJECT_TYPE_MISMATCH",
    "Object reference type mismatch: expected 'component', observed 'component'.");
  const missingType = token("$member:lamp.Enabled", "Synthetic.Missing", "Enabled", "boolean", base.contentHash);
  failure(() => prepare(root(missingType), [base]), "APPLY_CATALOG_UNAVAILABLE",
    "Type 'Synthetic.Missing', member 'Enabled': runtime catalog evidence is missing or unconfirmed.");
  const wrongHash = token("$member:lamp.Enabled", "Synthetic.Device", "Enabled", "boolean", "forged-hash");
  failure(() => prepare(root(wrongHash), [base]), "APPLY_CATALOG_UNAVAILABLE",
    "Type 'Synthetic.Device', member 'Enabled': runtime catalog evidence is missing or unconfirmed.");
  const conflicting = conflictingSourceTable(base);
  failure(() => prepare(root(token("$member:lamp.Enabled", "Synthetic.Device", "Enabled", "boolean", base.contentHash)),
    [base, conflicting]), "APPLY_CATALOG_UNAVAILABLE",
  "Type 'Synthetic.Device', member 'Target': runtime catalog evidence is missing or unconfirmed.");
});

test("typed references cannot fill scalar fields; unknown descriptors fail closed", () => {
  const base = table("boolean");
  const boolToken = token("$member:lamp.Enabled", "Synthetic.Device", "Enabled", "boolean", base.contentHash);
  failure(() => prepare(slot("page", [component("lamp", { fields: { Enabled: true } }),
    component("reader", { fields: { Enabled: boolToken } })]), [base]), "APPLY_OBJECT_TYPE_MISMATCH",
  "Object reference type mismatch: expected 'value:boolean', observed 'field:boolean'.");
  failure(() => prepare(slot("page", [component("lamp", { fields: { Enabled: true } }),
    component("reader", { fields: { Uncertain: boolToken } })]), [base]), "APPLY_CATALOG_UNAVAILABLE",
  "Type 'Synthetic.Device', member 'Uncertain': runtime catalog evidence is missing or unconfirmed.");
  failure(() => prepare(slot("page", [component("lamp", { fields: { Enabled: true } }),
    component("reader", { fields: { Target: boolToken } })]), []), "APPLY_CATALOG_UNAVAILABLE",
  "Type 'Synthetic.Device', member 'Target': runtime catalog evidence is missing or unconfirmed.");
});

test("typed reference consumers reject public Slot and Component exports by declared kind", () => {
  const evidence = table("boolean");
  for (const kind of ["slot", "component"] as const) {
    const body = slot("root", [component("lamp")]);
    const selector = kind === "slot" ? "$slot:root" : "$component:lamp";
    const placed = object("desk", body, selector);
    const consumer = component("consumer", { fields: { Target: exportReference(placed.props.handle, "target") } });
    failure(() => prepare(slot("page", [placed, consumer]), [evidence]), "APPLY_OBJECT_TYPE_MISMATCH",
      `Object reference type mismatch: expected 'field:boolean', observed '${kind}'.`);
    const scalarConsumer = component("scalar", { fields: { Enabled: exportReference(placed.props.handle, "target") } });
    failure(() => prepare(slot("page", [placed, scalarConsumer]), [evidence]), "APPLY_OBJECT_TYPE_MISMATCH",
      `Object reference type mismatch: expected 'value:boolean', observed '${kind}'.`);
  }
});

test("public typed references resolve in initialFields and runtime or driver Field values", () => {
  for (const mode of ["runtime", "driver"] as const) {
    const pair = publicReference("boolean", "boolean") as { placed: any; table: CatalogRuntimeTable };
    const reference = exportReference(pair.placed.props.handle, "target");
    const initialReader = component("initial-reader", { initialFields: { Target: reference } });
    const fieldReader = component("field-reader", { children: fragment(field("Target", reference, mode)) });
    const context = prepare(slot("page", [pair.placed, initialReader, fieldReader]), [pair.table]);
    const readers = (context.root as any).props.children;
    equal(readers[1].props.initialFields.Target, "$member:desk::lamp.Enabled");
    equal(readers[2].props.children.props.children.props.value, "$member:desk::lamp.Enabled");
  }
  const evidence = table("boolean");
  const wrongRepresentation = token("$member:lamp.Enabled", "Synthetic.Device", "Enabled", "string", evidence.contentHash);
  failure(() => prepare(slot("page", [component("lamp", { fields: { Enabled: true } }),
    component("reader", { children: field("Target", wrongRepresentation, "driver") })]), [evidence]),
  "APPLY_OBJECT_TYPE_MISMATCH", "Object reference type mismatch: expected 'field:string', observed 'field:boolean'.");
});

test("raw selectors resolve for privacy without typed receiver inference", () => {
  const desk = object("desk", slot("root", [component("lamp", { fields: { Enabled: true } }),
    component("reader", { fields: { Target: "$member:lamp.Enabled" } })]), "$slot:root");
  const context = prepare(slot("page", [desk]), []);
  equal((context.root as any).props.children[0].props.body.props.children[1].props.fields.Target,
    "$member:desk::lamp.Enabled");
});

test("propertyModes and Field declarations supply actual member evidence", () => {
  const evidence = table("boolean");
  const typed = token("$member:lamp.Enabled", "Synthetic.Device", "Enabled", "boolean", evidence.contentHash);
  const desk = object("desk", slot("root", [component("lamp", { propertyModes: { Enabled: "override" } }),
    component("reader", { fields: { Target: typed } })]), "$slot:root");
  const context = prepare(slot("page", [desk]), [evidence]);
  equal((context.root as any).props.children[0].props.body.props.children[1].props.fields.Target,
    "$member:desk::lamp.Enabled");
});

test("canonical type aliases and Field declarations retain actual parent evidence through fragments and Scope", () => {
  const evidence = table("boolean");
  const reference = token("$member:world::lamp.Enabled", "Synthetic.DeviceAlias", "Enabled", "boolean", evidence.contentHash);
  const root = slot("page", [component("world::lamp", { fields: { Enabled: true } }),
    object("desk", slot("root", [scope("segment", component("reader", {
      children: fragment(field("Target", reference)),
    }))]), "$slot:root")]);
  const context = prepare(root, [evidence]);
  const value = context.root as any;
  const objectCopy = value.props.children[1];
  const componentCopy = objectCopy.props.body.props.children[0].props.children;
  equal(componentCopy.props.children.props.children.props.value, "$member:world::lamp.Enabled");
  deepEqual(context.contexts.get(componentCopy.props.children.props.children), { scope: "desk::segment", owner: "desk" });
});

test("short typed selectors use the consuming Scope and Field-only aliases resolve to their declared member", () => {
  const evidence = table("boolean");
  const typed = token("$member:lamp.Enabled", "Synthetic.Device", "Enabled", "boolean", evidence.contentHash);
  const desk = object("desk", slot("root", [scope("segment", [component("lamp", {
    children: fragment(field("Enabled", true)), fieldAliases: { Exposed: "Enabled" },
  }), component("reader", { fields: { Target: typed } })])]), "$field:desk::segment::Exposed");
  const consumer = component("consumer", { fields: { Target: exportReference(desk.props.handle, "target") } });
  const context = prepare(slot("page", [desk, consumer]), [evidence]);
  const tree = context.root as any;
  const scopeChildren = tree.props.children[0].props.body.props.children[0].props.children;
  equal(scopeChildren[1].props.fields.Target, "$member:desk::segment::lamp.Enabled");
  equal(tree.props.children[1].props.fields.Target, "$field:desk::segment::Exposed");
});
