import { test } from "node:test";
import { deepEqual, equal, ok } from "node:assert/strict";
import { BuildError, evaluate } from "../src/evaluate.js";
import { defineObject } from "../src/object-definition.js";
import { exportReference } from "../src/object-refs.js";
import { componentFields } from "../src/bound-field.js";
import type { CatalogMemberRuntime, CatalogRuntimeTable } from "../src/catalog-runtime.js";
import { collectObjects, validateObjectBoundaries } from "../src/object-expand.js";
import { validateObjectExports } from "../src/object-boundary.js";
import { buildErrorOrigin, captureElement, sourceElement, type ElementOrigin, type Source } from "../src/source-map.js";
import { objectTestRuntimeTable } from "./object-test-helpers.js";

const slot = (key: string, name: string, children?: unknown, props: Record<string, unknown> = {}) =>
  ({ kind: "slot", props: { key, name, children, ...props } } as any);
const component = (key: string, props: Record<string, unknown> = {}) =>
  ({ kind: "component", props: { key, type: "Synthetic.Device", ...props } } as any);

function known(file: string, offset: number): Source {
  return { status: "known", file, sha256: "c".repeat(64), range: {
    start: { offset, line: 3, column: offset + 1 }, end: { offset: offset + 1, line: 3, column: offset + 2 },
  } };
}

function boundComponent(parentType: string, table: CatalogRuntimeTable) {
  const fields = (componentFields as any)("Synthetic.Device", table);
  const fieldNameSource = known("field-name.tsx", 12);
  const parentTypeSource = known("component-type.tsx", 20);
  const bound = sourceElement(() => fields.Field({ name: "Enabled", value: true }), {
    source: known("field.tsx", 10), attributes: {
      name: { source: known("field-name-property.tsx", 11), valueSource: fieldNameSource },
    },
  }, { name: "Enabled", value: true });
  const element = component("light", { type: parentType, children: bound });
  const metadata: ElementOrigin = { source: known("component.tsx", 18), attributes: {
    type: { source: known("component-type-property.tsx", 19), valueSource: parentTypeSource },
  } };
  const result = sourceElement(() => { captureElement(element); return element; }, metadata, element.props);
  return { element: result, fieldNameSource, parentTypeSource };
}

function changedEnabledTable(base: CatalogRuntimeTable): CatalogRuntimeTable {
  const original = base.types["Synthetic.Device"];
  const enabled = { ...original.members.Enabled!, scalar: "single" as const,
    fieldInterface: "[FrooxEngine]FrooxEngine.IField<float>" } as CatalogMemberRuntime;
  const device = { ...original, members: { ...original.members, Enabled: enabled } };
  return { ...base, types: { ...base.types, "Synthetic.Device": device,
    "Synthetic.DeviceAlias": { ...device } } };
}

function withOtherParentType(base: CatalogRuntimeTable): CatalogRuntimeTable {
  const device = base.types["Synthetic.Device"];
  return { ...base, types: { ...base.types,
    "Synthetic.Other": { ...device, canonicalType: "Synthetic.Other" } } };
}

function fixture(id = "desk") {
  const catalogs = [objectTestRuntimeTable()];
  const fields = (componentFields as any)("Synthetic.Device", catalogs[0]);
  const definition = defineObject<{}>()({
    exports: { enabled: fields.ref("lamp", "Enabled") },
    body: () => slot("root", "Body", [component("lamp", { fields: { Enabled: true } })]) as never,
  });
  const placed = definition({ handle: definition.instance(id) }) as any;
  const reader = component("reader", { fields: { Target: exportReference(placed.props.handle, "enabled") } });
  return { placed, root: slot("root", "Scene", [placed, reader]), catalogs };
}

function expectError(action: () => unknown, code: string, message: string): BuildError {
  let caught: unknown;
  try { action(); } catch (error) { caught = error; }
  if (!(caught instanceof BuildError)) throw new Error("Expected BuildError");
  equal(caught.code, code);
  equal(caught.message, message);
  return caught;
}

test("collection contexts are local and leave export/decoder state empty until their stages", () => {
  const { root, catalogs } = fixture();
  const first = collectObjects(root, catalogs);
  const second = collectObjects(root, catalogs);
  ok(first !== second);
  ok(first.objects !== second.objects);
  ok(first.targets !== second.targets);
  ok(first.contexts !== second.contexts);
  ok(first.decoded !== second.decoded);
  equal(first.objects.get("desk")?.exports.size, 0);
  equal(second.objects.get("desk")?.exports.size, 0);
  equal(first.decoded.size, 0);
  equal(second.decoded.size, 0);
  validateObjectExports(first);
  validateObjectBoundaries(first);
  ok(first.objects.get("desk")!.exports.has("enabled"));
  equal(first.decoded.size, 2);
  equal(second.objects.get("desk")?.exports.size, 0);
  equal(second.decoded.size, 0);
});

test("repeated and parallel evaluation does not share Object context or decoded caches", async () => {
  const { root, catalogs } = fixture();
  const expected = {
    schemaVersion: "1", ownership: { key: "root" }, slot: { name: "Scene", key: "root" },
    components: [{ type: "Synthetic.Device", key: "reader", fields: { Target: "$member:desk::lamp.Enabled" } }],
    children: [{ slot: { name: "Body", key: "desk::root" }, components: [
      { type: "Synthetic.Device", key: "desk::lamp", fields: { Enabled: true } },
    ] }],
  };
  for (let index = 0; index < 100; index++) deepEqual(evaluate(root, { catalogs }).document, expected);
  const results = await Promise.all(Array.from({ length: 12 }, () =>
    Promise.resolve().then(() => evaluate(root, { catalogs }).document)));
  for (const document of results) deepEqual(document, expected);
});

test("a failed duplicate placement does not poison a later successful evaluation", () => {
  const { placed, root, catalogs } = fixture();
  expectError(() => evaluate(slot("root", "Scene", [placed, placed])), "APPLY_OBJECT_DUPLICATE",
    "Object instance 'desk' occurs twice.");
  const result = evaluate(root, { catalogs });
  equal(result.document.children?.[0]?.slot.key, "desk::root");
  equal(result.document.components?.[0]?.fields?.Target, "$member:desk::lamp.Enabled");
});

test("an export token from a same-ID different definition remains unknown", () => {
  const first = defineObject<{}>()({ exports: { root: "$slot:root" }, body: () => slot("root", "Body") as never });
  const second = defineObject<{}>()({ exports: { root: "$slot:root" }, body: () => slot("root", "Other") as never });
  const placed = first({ handle: first.instance("desk") }) as any;
  const stale = exportReference(second.instance("desk"), "root");
  const reader = component("reader", { fields: { Target: stale } });
  const error = expectError(() => evaluate(slot("scene", "Scene", [placed, reader]), { catalogs: [] }),
    "APPLY_OBJECT_EXPORT_UNKNOWN", "Unknown Object export 'desk.root'.");
  equal(error.code, "APPLY_OBJECT_EXPORT_UNKNOWN");
});

test("a typed export with the wrong consuming field representation fails explicitly", () => {
  const table = objectTestRuntimeTable();
  const fields = (componentFields as any)("Synthetic.Device", table);
  const definition = defineObject<{}>()({
    exports: { enabled: fields.ref("lamp", "Enabled") },
    body: () => slot("root", "Body", [component("lamp", { fields: { Enabled: true } })]) as never,
  });
  const placed = definition({ handle: definition.instance("desk") }) as any;
  const consumer = component("consumer", { fields: { Label: exportReference(placed.props.handle, "enabled") } });
  const error = expectError(() => evaluate(slot("scene", "Scene", [placed, consumer]), { catalogs: [table] }),
    "APPLY_OBJECT_TYPE_MISMATCH",
    "Object reference type mismatch: expected 'value:string', observed 'field:boolean'.");
  equal(error.code, "APPLY_OBJECT_TYPE_MISMATCH");
});

test("a shared Object body resolves every placement with its own owner and keeps bound Field metadata", () => {
  const table = objectTestRuntimeTable();
  const fields = (componentFields as any)("Synthetic.Device", table);
  const sharedField = fields.Field({ name: "Enabled", value: true });
  const sharedLamp = component("lamp", { children: sharedField });
  const sharedReader = component("reader", { fields: { Target: "$member:lamp.Enabled" } });
  const sharedBody = slot("root", "Body", [sharedLamp, sharedReader]);
  const definition = defineObject<{}>()({ exports: {}, body: () => sharedBody as never });
  const desk = definition({ handle: definition.instance("desk") }) as any;
  const ceiling = definition({ handle: definition.instance("ceiling") }) as any;
  const root = slot("scene", "Scene", [
    slot("desk-container", "DeskContainer", [desk]),
    slot("ceiling-container", "CeilingContainer", [ceiling]),
  ]);

  expectError(() => evaluate(root), "APPLY_CATALOG_UNAVAILABLE",
    "Type 'Synthetic.Device', member 'Enabled': runtime catalog evidence is missing or unconfirmed.");
  deepEqual(sharedReader.props.fields, { Target: "$member:lamp.Enabled" });
  equal(desk.props.body, sharedBody);
  equal(ceiling.props.body, sharedBody);
  const result = evaluate(root, { catalogs: [table] });
  deepEqual(result.document, {
    schemaVersion: "1", ownership: { key: "scene" }, slot: { name: "Scene", key: "scene" }, components: [],
    children: [
      { slot: { name: "DeskContainer", key: "desk-container" }, children: [{
        slot: { name: "Body", key: "desk::root" }, components: [
          { type: "Synthetic.Device", key: "desk::lamp", fields: { Enabled: true } },
          { type: "Synthetic.Device", key: "desk::reader", fields: { Target: "$member:desk::lamp.Enabled" } },
        ],
      }] },
      { slot: { name: "CeilingContainer", key: "ceiling-container" }, children: [{
        slot: { name: "Body", key: "ceiling::root" }, components: [
          { type: "Synthetic.Device", key: "ceiling::lamp", fields: { Enabled: true } },
          { type: "Synthetic.Device", key: "ceiling::reader", fields: { Target: "$member:ceiling::lamp.Enabled" } },
        ],
      }] },
    ],
  });
});

test("shared Component and bound Field nodes resolve independently under two Object Scopes", () => {
  const table = objectTestRuntimeTable();
  const fields = (componentFields as any)("Synthetic.Device", table);
  const sharedField = fields.Field({ name: "Enabled", value: true });
  const sharedComponent = component("lamp", {
    fields: { Target: "$member:lamp.Enabled" }, children: sharedField,
  });
  const body = slot("root", "Body", [
    { kind: "scope", props: { instanceKey: "a", children: sharedComponent } },
    { kind: "scope", props: { instanceKey: "b", children: sharedComponent } },
  ]);
  const definition = defineObject<{}>()({ exports: {}, body: () => body as never });
  const placed = definition({ handle: definition.instance("desk") }) as any;
  const result = evaluate(slot("scene", "Scene", [placed]), { catalogs: [table] });
  deepEqual(result.document, {
    schemaVersion: "1", ownership: { key: "scene" }, slot: { name: "Scene", key: "scene" }, components: [],
    children: [{ slot: { name: "Body", key: "desk::root" }, components: [
      { type: "Synthetic.Device", key: "desk::a::lamp", fields: {
        Target: "$member:desk::a::lamp.Enabled", Enabled: true,
      } },
      { type: "Synthetic.Device", key: "desk::b::lamp", fields: {
        Target: "$member:desk::b::lamp.Enabled", Enabled: true,
      } },
    ] }],
  });
  deepEqual(sharedComponent.props.fields, { Target: "$member:lamp.Enabled" });
  equal(placed.props.body, body);
});

test("bound Field evidence validates its actual descriptor and confirmed parent type", () => {
  const table = objectTestRuntimeTable();
  const success = boundComponent("Synthetic.Device", table);
  const valid = evaluate(slot("root", "Scene", [success.element]), { catalogs: [table] });
  equal(valid.document.components?.[0]?.fields?.Enabled, true);

  const missing = boundComponent("Synthetic.Device", table);
  const unavailable = expectError(() => evaluate(slot("root", "Scene", [missing.element])),
    "APPLY_CATALOG_UNAVAILABLE",
    "Type 'Synthetic.Device', member 'Enabled': runtime catalog evidence is missing or unconfirmed.");
  deepEqual(buildErrorOrigin(unavailable)?.source, missing.fieldNameSource);
  deepEqual(buildErrorOrigin(unavailable)?.related, [missing.parentTypeSource]);

  const changed = changedEnabledTable(table);
  const wrongDescriptor = boundComponent("Synthetic.Device", table);
  expectError(() => evaluate(slot("root", "Scene", [wrongDescriptor.element]), { catalogs: [changed] }),
    "APPLY_CATALOG_UNAVAILABLE",
    "Type 'Synthetic.Device', member 'Enabled': runtime catalog evidence is missing or unconfirmed.");
  const conflicting = boundComponent("Synthetic.Device", table);
  expectError(() => evaluate(slot("root", "Scene", [conflicting.element]), { catalogs: [table, changed] }),
    "APPLY_CATALOG_UNAVAILABLE",
    "Type 'Synthetic.Device', member 'Enabled': runtime catalog evidence is missing or unconfirmed.");

  const mismatch = boundComponent("Synthetic.Other", table);
  const mismatchError = expectError(() => evaluate(slot("root", "Scene", [mismatch.element]), {
    catalogs: [withOtherParentType(table)],
  }), "APPLY_FIELD_TYPE_MISMATCH",
  "Bound Field requires Component 'Synthetic.Device', observed 'Synthetic.Other'.");
  deepEqual(buildErrorOrigin(mismatchError)?.source, mismatch.fieldNameSource);
  deepEqual(buildErrorOrigin(mismatchError)?.related, [mismatch.parentTypeSource]);
});
