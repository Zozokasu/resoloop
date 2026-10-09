import { test } from "node:test";
import { deepEqual, equal } from "node:assert/strict";
import { collectObjects, validateObjectBoundaries } from "../src/object-expand.js";
import { validateObjectExports } from "../src/object-boundary.js";
import { defineObject } from "../src/object-definition.js";
import { exportReference } from "../src/object-refs.js";
import { BuildError } from "../src/evaluate.js";
import type { ObjectElement } from "../src/elements.js";
import type { CatalogRuntimeTable } from "../src/catalog-runtime.js";
import { buildErrorOrigin, captureElement, sourceElement, type Source } from "../src/source-map.js";
import { objectTestRuntimeTable } from "./object-test-helpers.js";

const slot = (key: string, children?: unknown, props: Record<string, unknown> = {}) =>
  ({ kind: "slot", props: { key, name: key, children, ...props } } as any);
const component = (key: string, props: Record<string, unknown> = {}) =>
  ({ kind: "component", props: { key, type: "Synthetic.Device", ...props } } as any);

function object(id: string, body: unknown, exports: Record<string, string>): ObjectElement {
  const definition = defineObject<{}>()({ exports, body: () => body as never });
  return definition({ handle: definition.instance(id) }) as ObjectElement;
}

function prepare(root: unknown, catalogs: readonly CatalogRuntimeTable[] = []) {
  const context = collectObjects(root, catalogs);
  validateObjectExports(context);
  validateObjectBoundaries(context);
  return context;
}

function failure(action: () => unknown, code: string, message: string): BuildError {
  let caught: unknown;
  try { action(); } catch (error) { caught = error; }
  if (!(caught instanceof BuildError)) throw new Error("Expected BuildError");
  equal(caught.code, code);
  equal(caught.message, message);
  return caught;
}

const typedToken = (selector: string) => "$object-target:" + encodeURIComponent(JSON.stringify({ v: 1,
  selector, componentType: "Synthetic.Device", member: "Enabled", representation: "boolean",
  catalogHash: "handwritten-runtime-hash" }));

function exportToken(desk: ObjectElement, name: string, definitionId?: string): string {
  return "$export:" + encodeURIComponent(JSON.stringify({ v: 1, instanceId: desk.props.handle.id,
    definitionId: definitionId ?? desk.props.definition.definitionId, name }));
}

const deskBody = () => slot("root", [component("lamp", { fields: { Enabled: true } })]);
const deskExports = { root: "$slot:root", lamp: "$component:lamp" };

test("Slot and Component migrateFrom resolve valid public export tokens to selectors", () => {
  const desk = object("desk", deskBody(), deskExports);
  const slotToken = exportReference(desk.props.handle, "root");
  const componentToken = exportReference(desk.props.handle, "lamp");
  const consumerSlot = slot("a", undefined, { migrateFrom: slotToken });
  const consumerComponent = component("b", { migrateFrom: componentToken });
  const context = prepare(slot("page", [desk, consumerSlot, consumerComponent]));
  const children = (context.root as any).props.children;
  equal(children[1].props.migrateFrom, "$slot:desk::root");
  equal(children[2].props.migrateFrom, "$component:desk::lamp");
  equal(consumerSlot.props.migrateFrom, slotToken);
  equal(consumerComponent.props.migrateFrom, componentToken);
});

test("Slot and Component migrateFrom resolve own typed targets to member selectors", () => {
  const token = typedToken("$member:lamp.Enabled");
  const body = slot("root", [component("lamp", { fields: { Enabled: true } }),
    slot("legacy", undefined, { migrateFrom: token }), component("old", { migrateFrom: token })]);
  const context = prepare(slot("page", [object("desk", body, {})]), [objectTestRuntimeTable()]);
  const rootBody = (context.root as any).props.children[0].props.body;
  equal(rootBody.props.children[1].props.migrateFrom, "$member:desk::lamp.Enabled");
  equal(rootBody.props.children[2].props.migrateFrom, "$member:desk::lamp.Enabled");
});

test("malformed export migrateFrom tokens fail as Unknown export for both kinds", () => {
  for (const consumer of [slot("a", undefined, { migrateFrom: "$export:bad" }),
    component("b", { migrateFrom: "$export:bad" })])
    failure(() => prepare(slot("page", [object("desk", deskBody(), deskExports), consumer])),
      "APPLY_OBJECT_EXPORT_UNKNOWN", "Unknown Object export '<invalid>'.");
});

test("foreign and missing export migrateFrom tokens fail as Unknown export for both kinds", () => {
  const desk = object("desk", deskBody(), deskExports);
  const foreignId = object("desk", deskBody(), deskExports).props.definition.definitionId;
  for (const [name, definitionId] of [["root", foreignId], ["ghost", undefined]] as const) {
    const token = exportToken(desk, name, definitionId);
    for (const consumer of [slot("a", undefined, { migrateFrom: token }), component("b", { migrateFrom: token })])
      failure(() => prepare(slot("page", [desk, consumer])), "APPLY_OBJECT_EXPORT_UNKNOWN",
        `Unknown Object export 'desk.${name}'.`);
  }
});

test("typed other-owner migrateFrom is private even when the target does not exist", () => {
  const token = typedToken("$member:desk::ghost.Enabled");
  for (const consumer of [slot("a", undefined, { migrateFrom: token }), component("b", { migrateFrom: token })])
    failure(() => prepare(slot("page", [object("desk", deskBody(), {}), consumer]), [objectTestRuntimeTable()]),
      "APPLY_OBJECT_PRIVATE_REFERENCE", "Private Object target '$member:desk::ghost.Enabled'; use ref.export(handle, name).");
});

test("raw past migrateFrom keys may still name a target that does not exist", () => {
  const context = prepare(slot("page", [object("desk", deskBody(), {}), slot("a", undefined, { migrateFrom: "gone-slot" }),
    component("b", { migrateFrom: "gone-component" })]));
  const children = (context.root as any).props.children;
  equal(children[1].props.migrateFrom, "gone-slot");
  equal(children[2].props.migrateFrom, "gone-component");
});

const tableA = (): CatalogRuntimeTable => objectTestRuntimeTable(false);
const tableB = (): CatalogRuntimeTable => ({ ...tableA(), types: { "Synthetic.Other": {
  ...tableA().types["Synthetic.Device"], canonicalType: "Synthetic.Other" } } });
const unavailableTarget = "Type 'Synthetic.Device', member 'Target': runtime catalog evidence is missing or unconfirmed.";

function connect(catalogs: CatalogRuntimeTable[], reader = component("reader")) {
  const desk = object("desk", deskBody(), { enabled: "$member:lamp.Enabled" });
  reader.props.fields = { Target: exportReference(desk.props.handle, "enabled") };
  return prepare(slot("page", [desk, reader]), catalogs);
}

test("same-hash tables with different type sets are unavailable in both orders", () => {
  for (const catalogs of [[tableA(), tableB()], [tableB(), tableA()]])
    failure(() => connect(catalogs), "APPLY_CATALOG_UNAVAILABLE", unavailableTarget);
});

test("an unrelated valid table with another hash does not reject the connection", () => {
  const other = { ...tableB(), contentHash: "unrelated-hash" };
  const context = connect([tableA(), other]);
  equal((context.root as any).props.children[1].props.fields.Target, "$member:desk::lamp.Enabled");
});

test("same-hash tables differing only in key order are the same table", () => {
  const base = tableA();
  const device = base.types["Synthetic.Device"];
  const reordered: CatalogRuntimeTable = { ...base, types: { "Synthetic.Device": { ...device,
    members: Object.fromEntries(Object.entries(device.members).reverse()) } } };
  const context = connect([base, reordered]);
  equal((context.root as any).props.children[1].props.fields.Target, "$member:desk::lamp.Enabled");
});

const known = (file: string, offset: number): Source => ({ status: "known", file, sha256: "a".repeat(64), range: {
  start: { offset, line: 1, column: offset + 1 }, end: { offset: offset + 1, line: 1, column: offset + 2 } } });
const valueSource = known("target-value.tsx", 3);
const relatedSource = known("target-related.tsx", 5);

function sourcedReader() {
  const element = component("reader");
  return sourceElement(() => { captureElement(element); return element; }, { source: known("reader.tsx", 0),
    attributes: { fields: { source: known("fields.tsx", 1), children: { Target: {
      source: known("target-name.tsx", 2), valueSource, related: [relatedSource] } } } } }, element.props);
}

test("receiver catalog failures keep the consumer value source and related for every failing catalog", () => {
  const invalid = { ...tableA(), types: { "Synthetic.Device": {} } } as unknown as CatalogRuntimeTable;
  const base = tableA();
  const device = base.types["Synthetic.Device"];
  const unconfirmedTable: CatalogRuntimeTable = { ...base, types: { ...base.types,
    "Synthetic.Device": { ...device, members: { ...device.members, Target: device.members.UnconfirmedTarget } } } };
  const cases: Array<[CatalogRuntimeTable[], string, string]> = [
    [[], "APPLY_CATALOG_UNAVAILABLE", unavailableTarget],
    [[tableA(), tableB()], "APPLY_CATALOG_UNAVAILABLE", unavailableTarget],
    [[unconfirmedTable], "APPLY_CATALOG_UNAVAILABLE", unavailableTarget],
    [[invalid], "APPLY_CATALOG_TYPES_HASH_MISMATCH",
      "Catalog runtime table does not match format/generator/descriptor shape; regenerate catalog types."],
  ];
  for (const [catalogs, code, message] of cases) {
    const error = failure(() => connect(catalogs, sourcedReader()), code, message);
    deepEqual(buildErrorOrigin(error), { source: valueSource, related: [relatedSource] });
  }
});

test("receiver catalog failures without consumer metadata keep an unknown source", () => {
  const error = failure(() => connect([]), "APPLY_CATALOG_UNAVAILABLE", unavailableTarget);
  deepEqual(buildErrorOrigin(error), { source: { status: "unknown" }, related: [] });
});
