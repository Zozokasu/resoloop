import { test } from "node:test";
import { deepEqual, equal, ok } from "node:assert/strict";
import { collectObjects, validateObjectBoundaries } from "../src/object-expand.js";
import { validateObjectExports } from "../src/object-boundary.js";
import { defineObject } from "../src/object-definition.js";
import { exportReference } from "../src/object-refs.js";
import { BuildError } from "../src/evaluate.js";
import type { ObjectElement } from "../src/elements.js";
import type { CatalogRuntimeTable } from "../src/catalog-runtime.js";
import { buildErrorOrigin, captureElement, sourceElement, type ElementOrigin, type Origin, type Source } from "../src/source-map.js";
import { objectTestRuntimeTable } from "./object-test-helpers.js";

const slot = (key: string, children?: unknown, props: Record<string, unknown> = {}) =>
  ({ kind: "slot", props: { key, name: key, children, ...props } } as any);
const component = (key: string, props: Record<string, unknown> = {}) =>
  ({ kind: "component", props: { key, type: "Synthetic.Device", ...props } } as any);
const field = (name: string, value?: unknown, mode = "runtime") => ({ kind: "field", props: { name, mode, value } } as any);
const scope = (instanceKey: string, children: unknown) => ({ kind: "scope", props: { instanceKey, children } } as any);

function object(id: string, body: unknown = slot("root"), exports: Record<string, string> = {}): ObjectElement {
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

const privateSelectors = [
  "$slot:desk::ghost", "$component:desk::ghost", "$ref:desk::ghost",
  "$member:desk::ghost.Enabled", "$slot-member:desk::ghost.Position", "$field:desk::ghost",
];

function privateFixture(selector: string, property: string): unknown {
  const desk = object("desk");
  const nested = [{ nested: selector }];
  if (property === "fields") return slot("root", [desk, component("reader", { fields: { Link: nested } })]);
  if (property === "initialFields") return slot("root", [desk, component("reader", { initialFields: { Link: nested } })]);
  if (property === "Field.value") return slot("root", [desk, component("reader", { children: field("Enabled", nested, "driver") })]);
  if (property === "Slot.parent") return slot("root", [desk], { parent: selector });
  if (property === "Slot.migrateFrom") return slot("root", [desk], { migrateFrom: selector });
  return slot("root", [desk, component("reader", { migrateFrom: selector })]);
}

test("all six selectors stay private across every value consumer", () => {
  const properties = ["fields", "initialFields", "Field.value", "Slot.parent", "Slot.migrateFrom", "Component.migrateFrom"];
  for (const selector of privateSelectors) for (const property of properties) {
    failure(() => prepare(privateFixture(selector, property), []), "APPLY_OBJECT_PRIVATE_REFERENCE",
      `Private Object target '${selector}'; use ref.export(handle, name).`);
  }
});

test("Object-local missing selectors retain fixed kind-specific diagnostics", () => {
  failure(() => prepare(slot("page", [object("desk", slot("root", [component("lamp", {
    fields: { Link: "$member:missing.Enabled" },
  })]))]), []), "APPLY_MEMBER_REFERENCE_NOT_FOUND",
  "Member reference '$member:desk::missing.Enabled' has no declared target");
  failure(() => prepare(slot("page", [object("desk", slot("root", [component("lamp", {
    fields: { Link: "$field:ghost" },
  })]))]), []), "APPLY_FIELD_ALIAS_NOT_FOUND", "Field alias 'desk::ghost' has no declared target");
  failure(() => prepare(slot("page", [object("desk", slot("root", [component("lamp", {
    fields: { Link: "$slot-member:ghost.Position" },
  })]))]), []), "APPLY_SCOPE_INVALID",
  "Scoped reference '$slot-member:desk::ghost.Position' has no declared target; use a qualified key for another scope");
});

test("migrateFrom checks privacy without requiring an existing local target", () => {
  const local = object("local", slot("root", [
    slot("nested", undefined, { migrateFrom: "historical-slot" }),
    component("lamp", { migrateFrom: "historical-component" }),
  ]));
  equal(prepare(slot("page", [local]), []).objects.size, 1);
  failure(() => prepare(slot("page", [object("desk"), slot("reader", undefined, { migrateFrom: "desk::old" })]), []),
    "APPLY_OBJECT_PRIVATE_REFERENCE", "Private Object target '$slot:desk::old'; use ref.export(handle, name).");
});

test("raw and typed-looking references into an Object namespace fail before lookup", () => {
  const typed = "$object-target:" + encodeURIComponent(JSON.stringify({ v: 1,
    selector: "$member:desk::ghost.Enabled", componentType: "Synthetic.Device", member: "Enabled",
    representation: "boolean", catalogHash: "handwritten-runtime-hash" }));
  failure(() => prepare(slot("page", [object("desk"), component("reader", { fields: { Link: typed } })]),
    [objectTestRuntimeTable()]), "APPLY_OBJECT_PRIVATE_REFERENCE",
  "Private Object target '$member:desk::ghost.Enabled'; use ref.export(handle, name).");
});

test("Slot member selectors are private by Slot key but cannot be exported as a Slot", () => {
  failure(() => prepare(slot("page", [object("desk", slot("root"), { position: "$slot-member:root.Position" })]), []),
    "APPLY_OBJECT_EXPORT_INVALID",
    "Object 'desk' export 'position': target is not a declared internal Slot, Component or Field.");
});

test("nested Object scopes qualify raw selectors and migrations without mutating inputs", () => {
  const producer = component("lamp", { fields: { Enabled: true } });
  const reader = component("reader", { fields: { Link: "$member:lamp.Enabled" } });
  const body = slot("root", [scope("segment", [producer, reader,
    slot("legacy", undefined, { migrateFrom: "old-slot" }),
    component("old", { migrateFrom: "old-component" }),
  ])]);
  const context = prepare(slot("page", [object("desk", body)]), [objectTestRuntimeTable()]);
  const rootCopy = context.root as any;
  const bodyCopy = rootCopy.props.children[0].props.body;
  const bodySlotChildren = bodyCopy.props.children;
  const segmentScope = bodySlotChildren[0];
  const segmentChildren = segmentScope.props.children;
  equal(segmentChildren[1].props.fields.Link, "$member:desk::segment::lamp.Enabled");
  equal(segmentChildren[2].props.migrateFrom, "desk::segment::old-slot");
  equal(segmentChildren[3].props.migrateFrom, "desk::segment::old-component");
  equal(reader.props.fields.Link, "$member:lamp.Enabled");
});

test("active Object cloning preserves JSON kind objects and an own __proto__ field member", () => {
  const jsonValue = JSON.parse('{"kind":"object","__proto__":{"nested":{"target":"$member:lamp.Enabled"}},"ordinary":"kept"}');
  const fields = JSON.parse('{"__proto__":null}');
  fields.__proto__ = jsonValue;
  const root = slot("page", [object("desk", slot("root", [component("lamp", { fields: { Enabled: true } }),
    component("reader", { fields })]))]);
  const context = prepare(root, []);
  const readerFields = (context.root as any).props.children[0].props.body.props.children[1].props.fields;
  ok(Object.hasOwn(readerFields, "__proto__"));
  equal(readerFields["__proto__"].kind, "object");
  equal(readerFields["__proto__"].__proto__.nested.target, "$member:desk::lamp.Enabled");
  equal(readerFields["__proto__"].ordinary, "kept");
  ok(Object.hasOwn(fields, "__proto__"));
  equal(fields["__proto__"], jsonValue);
});

test("public export tokens resolve only a placed matching definition and emit selectors", () => {
  const desk = object("desk", slot("root", [component("lamp", { fields: { Enabled: true } })]), {
    enabled: "$member:lamp.Enabled",
  });
  const token = exportReference(desk.props.handle, "enabled");
  const root = slot("page", [desk, component("reader", { fields: { Target: token } })]);
  const context = prepare(root, [objectTestRuntimeTable()]);
  const resolved = context.root as { props: { children: Array<{ props: { fields?: Record<string, unknown> } }> } };
  equal(resolved.props.children[1].props.fields?.Target, "$member:desk::lamp.Enabled");
  equal(JSON.stringify(context.root).includes("$export:"), false);

  const unplaced = object("other", slot("root"), { enabled: "$slot:root" });
  const dangling = exportReference(unplaced.props.handle, "enabled");
  failure(() => prepare(slot("page", [component("reader", { fields: { Link: dangling } })]), []),
    "APPLY_OBJECT_EXPORT_UNKNOWN", "Unknown Object export 'other.enabled'.");
  const placed = object("other", slot("root"), { enabled: "$slot:root" });
  const foreignDefinition = object("other", slot("root"), { enabled: "$slot:root" });
  const forged = exportReference(foreignDefinition.props.handle, "enabled");
  ok(forged.startsWith("$export:"));
  failure(() => prepare(slot("page", [placed, component("reader", { fields: { Link: forged } })]), []),
    "APPLY_OBJECT_EXPORT_UNKNOWN", "Unknown Object export 'other.enabled'.");
});

test("legacy references keep the exact no-Object root and export diagnostics are fixed", () => {
  const legacy = slot("root", [component("c", { fields: { Link: "$member:missing.Enabled" } })]);
  const context = prepare(legacy, []);
  equal(context.needsPass, false);
  equal(context.root, legacy);
  failure(() => prepare(slot("page", [object("desk", slot("root"), { "  ": "$slot:root" })]), []),
    "APPLY_OBJECT_EXPORT_INVALID", "Object 'desk' export '  ': export name must be nonempty.");
  failure(() => prepare(slot("page", [object("desk", slot("root"), { again: "$export:bad" })]), []),
    "APPLY_OBJECT_EXPORT_INVALID", "Object 'desk' export 'again': re-exporting another Object is not supported.");
});

function known(file: string, offset: number): Source {
  return { status: "known", file, sha256: "a".repeat(64), range: {
    start: { offset, line: 1, column: offset + 1 }, end: { offset: offset + 1, line: 1, column: offset + 2 },
  } };
}

const origin = (children: Record<string, Origin>): Origin => ({ source: { status: "unknown" }, children });

function sourcedComponent(props: Record<string, unknown>, attributes: ElementOrigin["attributes"]): unknown {
  const element = component("reader", props);
  return sourceElement(() => { captureElement(element); return element; }, { source: known("component.tsx", 0), attributes }, props);
}

function sourcedObject(id: string): ObjectElement {
  const definition = defineObject<{}>()({ exports: { missing: "$slot:ghost" }, body: () => slot("root") as never });
  const handle = definition.instance(id);
  const metadata: ElementOrigin = { source: known("object.tsx", 0), attributes: {
    handle: { source: known("handle-consumed.tsx", 2), valueSource: known("handle-id.tsx", 4) },
    missing: { source: known("export-name.tsx", 6), valueSource: known("export-value.tsx", 8) },
  } };
  return sourceElement(() => { captureElement(definition); return definition({ handle }); }, metadata, { handle }) as ObjectElement;
}

test("consumer diagnostics use valueSource or unknown and keep related evidence", () => {
  const valueSource = known("value.tsx", 10);
  const relatedSource = known("related.tsx", 12);
  const withValue = sourcedComponent({ fields: { Link: "$member:desk::ghost.Enabled" } }, {
    fields: { source: known("field-prop.tsx", 1), valueSource: known("fields-prop.tsx", 3),
      children: { Link: { source: known("link-prop.tsx", 5), valueSource, related: [relatedSource] } } },
  });
  const error = failure(() => prepare(slot("page", [object("desk"), withValue]), []),
    "APPLY_OBJECT_PRIVATE_REFERENCE",
    "Private Object target '$member:desk::ghost.Enabled'; use ref.export(handle, name).");
  const origin = buildErrorOrigin(error)!;
  if (origin.source.status !== "known") throw new Error("Expected explicit consuming value source");
  deepEqual(origin.source, valueSource);
  deepEqual(origin.related, [relatedSource]);

  const withoutValue = sourcedComponent({ fields: { Link: "$member:desk::ghost.Enabled" } }, {
    fields: { source: known("property-only.tsx", 20), children: {
      Link: { source: known("link-only.tsx", 22) },
    } },
  });
  const unknownError = failure(() => prepare(slot("page", [object("desk"), withoutValue]), []),
    "APPLY_OBJECT_PRIVATE_REFERENCE",
    "Private Object target '$member:desk::ghost.Enabled'; use ref.export(handle, name).");
  deepEqual(buildErrorOrigin(unknownError)?.source, { status: "unknown" });
});

test("nested privacy leaves use their own valueSource and ignore source-only metadata", () => {
  const valueSource = known("nested-value.tsx", 40);
  const withValueMetadata = origin({ Link: origin({ nested: origin({ "0": origin({
    target: { source: known("name-only.tsx", 41), valueSource },
  }) }) }) });
  const withValue = sourcedComponent({ fields: { Link: { nested: [{ target: "$member:desk::ghost.Enabled" }] } } }, {
    fields: withValueMetadata,
  });
  const error = failure(() => prepare(slot("page", [object("desk"), withValue]), []),
    "APPLY_OBJECT_PRIVATE_REFERENCE",
    "Private Object target '$member:desk::ghost.Enabled'; use ref.export(handle, name)." );
  deepEqual(buildErrorOrigin(error)?.source, valueSource);

  const withoutValueMetadata = origin({ Link: origin({ nested: origin({ "0": origin({
    target: { source: known("name-only.tsx", 41) },
  }) }) }) });
  const withoutValue = sourcedComponent({ fields: { Link: { nested: [{ target: "$member:desk::ghost.Enabled" }] } } }, {
    fields: withoutValueMetadata,
  });
  const unknownError = failure(() => prepare(slot("page", [object("desk"), withoutValue]), []),
    "APPLY_OBJECT_PRIVATE_REFERENCE",
    "Private Object target '$member:desk::ghost.Enabled'; use ref.export(handle, name)." );
  deepEqual(buildErrorOrigin(unknownError)?.source, { status: "unknown" });
});

test("export diagnostics use declared export value and consumed handle source", () => {
  const error = failure(() => prepare(slot("page", [sourcedObject("desk")]), []),
    "APPLY_OBJECT_EXPORT_INVALID", "Object 'desk' export 'missing': target is not a declared internal Slot, Component or Field.");
  const origin = buildErrorOrigin(error)!;
  if (origin.source.status !== "known") throw new Error("Expected export value source");
  deepEqual(origin.source, known("export-value.tsx", 8));
  const related = origin.related[0];
  if (!related || related.status !== "known") throw new Error("Expected consumed handle source");
  deepEqual(related, known("handle-consumed.tsx", 2));
});

test("invalid exports reject foreign, missing, malformed and control-name targets with fixed reasons", () => {
  const other = object("other", slot("root"));
  const foreign = object("desk", slot("root"), { target: "$slot:other::root" });
  failure(() => prepare(slot("page", [foreign, other]), []), "APPLY_OBJECT_EXPORT_INVALID",
    "Object 'desk' export 'target': target is not a declared internal Slot, Component or Field.");
  const outside = object("desk", slot("root"), { target: "$slot:outside" });
  failure(() => prepare(slot("page", [outside, slot("outside")]), []), "APPLY_OBJECT_EXPORT_INVALID",
    "Object 'desk' export 'target': target is not a declared internal Slot, Component or Field.");
  for (const selector of ["$slot:ghost", "$field:ghost"]) {
    const missing = object("desk", slot("root"), { target: selector });
    failure(() => prepare(slot("page", [missing]), []), "APPLY_OBJECT_EXPORT_INVALID",
      "Object 'desk' export 'target': target is not a declared internal Slot, Component or Field.");
  }
  const malformed = object("desk", slot("root"), { target: "$object-target:bad" });
  failure(() => prepare(slot("page", [malformed]), []), "APPLY_OBJECT_EXPORT_INVALID",
    "Object 'desk' export 'target': malformed typed target token.");
  const controlName = object("desk", slot("root"), { ["bad\u0001"]: "$slot:root" });
  failure(() => prepare(slot("page", [controlName]), []), "APPLY_OBJECT_EXPORT_INVALID",
    "Object 'desk' export 'bad\u0001': export name must be nonempty.");
});

test("forward public export resolution preserves its input token and emits the declared selector", () => {
  const desk = object("desk", slot("root", [component("lamp", { fields: { Enabled: true } })]), {
    enabled: "$member:lamp.Enabled",
  });
  const token = exportReference(desk.props.handle, "enabled");
  const reader = component("reader", { fields: { Target: token } });
  const context = prepare(slot("page", [reader, desk]), [objectTestRuntimeTable()]);
  equal((context.root as any).props.children[0].props.fields.Target, "$member:desk::lamp.Enabled");
  equal(reader.props.fields.Target, token);

  const unknownName = "$export:" + encodeURIComponent(JSON.stringify({ v: 1,
    instanceId: "desk", definitionId: desk.props.definition.definitionId, name: "missing" }));
  failure(() => prepare(slot("page", [component("reader", { fields: { Link: unknownName } }), desk]),
    [objectTestRuntimeTable()]), "APPLY_OBJECT_EXPORT_UNKNOWN", "Unknown Object export 'desk.missing'.");
});
