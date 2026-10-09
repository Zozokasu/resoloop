import { test } from "node:test";
import { deepEqual, equal } from "node:assert/strict";
import { evaluate, BuildError } from "../src/evaluate.js";
import { defineObject } from "../src/object-definition.js";
import { exportReference } from "../src/object-refs.js";
import { componentFields } from "../src/bound-field.js";
import { captureElement, originOfIr, sourceElement, type Source } from "../src/source-map.js";
import { objectTestRuntimeTable } from "./object-test-helpers.js";

const slot = (key: string, name: string, children?: unknown, props: Record<string, unknown> = {}) =>
  ({ kind: "slot", props: { key, name, children, ...props } } as any);
const component = (key: string, type: string, props: Record<string, unknown> = {}) =>
  ({ kind: "component", props: { key, type, ...props } } as any);
const scope = (instanceKey: string, children: unknown) => ({ kind: "scope", props: { instanceKey, children } } as any);

function known(file: string, offset: number): Source {
  return { status: "known", file, sha256: "d".repeat(64), range: {
    start: { offset, line: 4, column: offset + 1 }, end: { offset: offset + 1, line: 4, column: offset + 2 },
  } };
}

function makeObject(id: string, name: string, table: ReturnType<typeof objectTestRuntimeTable>): any {
  const fields = (componentFields as any)("Synthetic.Device", table);
  const definition = defineObject<{}>()({
    exports: { enabled: fields.ref("light", "Enabled") },
    body: () => slot("root", name, [component("light", "Synthetic.Device", {
      fields: { Weight: 1.5 }, initialFields: { Count: 4 },
      children: fields.Field({ name: "Enabled", value: true, key: "enabled" }),
    })]) as never,
  });
  return definition({ handle: definition.instance(id) }) as any;
}

test("Object expansion produces the fixed handwritten schema-v1 IR", () => {
  const table = objectTestRuntimeTable();
  const desk = makeObject("desk", "Desk", table);
  const ceiling = makeObject("ceiling", "Ceiling", table);
  const root = slot("root", "Scene", [component("connector", "Synthetic.Device", {
    fields: { Target: exportReference(desk.props.handle, "enabled") },
  }), desk, ceiling]);

  const result = evaluate(root, { catalogs: [table] });
  deepEqual(result.document, {
    schemaVersion: "1",
    ownership: { key: "root" },
    slot: { name: "Scene", key: "root" },
    components: [{ type: "Synthetic.Device", key: "connector", fields: {
      Target: "$member:desk::light.Enabled",
    } }],
    children: [
      { slot: { name: "Desk", key: "desk::root" }, components: [{
        type: "Synthetic.Device", key: "desk::light", fields: { Weight: 1.5, Enabled: true },
        initialFields: { Count: 4 }, fieldAliases: { "desk::enabled": "Enabled" },
      }] },
      { slot: { name: "Ceiling", key: "ceiling::root" }, components: [{
        type: "Synthetic.Device", key: "ceiling::light", fields: { Weight: 1.5, Enabled: true },
        initialFields: { Count: 4 }, fieldAliases: { "ceiling::enabled": "Enabled" },
      }] },
    ],
  });
  equal(JSON.stringify(result.document).includes("$export:"), false);
  equal(JSON.stringify(result.document).includes("$object-target:"), false);
});

test("Object scope resets an enclosing Scope and migration forms are prefixed only once", () => {
  const definition = defineObject<{}>()({
    exports: { root: "$slot:root" },
    body: () => slot("root", "Body", [
      slot("raw", "Raw", undefined, { migrateFrom: "old-slot" }),
      slot("selector-slot", "Selector", undefined, { migrateFrom: "$slot:old-slot" }),
      component("raw-component", "Synthetic.Device", { migrateFrom: "old-component" }),
      component("selector-component", "Synthetic.Device", { migrateFrom: "$component:old-component" }),
    ]) as never,
  });
  const object = definition({ handle: definition.instance("desk") }) as any;
  const result = evaluate(slot("root", "Scene", [scope("outer", [object])]), { catalogs: [] });
  const body = result.document.children?.[0];
  equal(body?.slot.key, "desk::root");
  equal(body?.children?.[0]?.slot.migrateFrom, "desk::old-slot");
  equal(body?.children?.[1]?.slot.migrateFrom, "$slot:desk::old-slot");
  equal(body?.components?.[0]?.migrateFrom, "desk::old-component");
  equal(body?.components?.[1]?.migrateFrom, "$component:desk::old-component");
  const legacy = evaluate(slot("root", "Scene", [scope("outer", [slot("child", "Child", undefined, {
    migrateFrom: "$slot:old-slot",
  })])]));
  equal(legacy.document.children?.[0]?.slot.migrateFrom, "outer::$slot:old-slot");
});

test("legacy document without Objects keeps the original IR order and values", () => {
  const result = evaluate(slot("root", "Scene", [
    component("first", "Synthetic.Device", { fields: { Weight: 1.5 } }),
    slot("child", "Child"),
  ]));
  deepEqual(result.document, {
    schemaVersion: "1", ownership: { key: "root" }, slot: { name: "Scene", key: "root" },
    components: [{ type: "Synthetic.Device", key: "first", fields: { Weight: 1.5 } }],
    children: [{ slot: { name: "Child", key: "child" } }],
  });

  let caught: unknown;
  try { evaluate(slot("root", "Scene", [{}])); } catch (error) { caught = error; }
  if (!(caught instanceof BuildError)) throw new Error("Expected legacy INVALID_CHILD error");
  equal(caught.code, "INVALID_CHILD");
  equal(caught.message, "Expected a <Slot> or <Component> element at $.children, got object {}");
});

test("Object validation rejects fake, foreign, malformed, and nested placements", () => {
  const definition = defineObject<{}>()({ exports: { root: "$slot:root" }, body: () => slot("root", "Body") as never });
  const placed = definition({ handle: definition.instance("desk") }) as any;
  const foreignDefinition = defineObject<{}>()({ exports: { root: "$slot:root" }, body: () => slot("root", "Other") as never });
  const foreignObject = foreignDefinition({ handle: foreignDefinition.instance("other") }) as any;
  const fake = { kind: "object", props: { handle: { id: "fake" }, definition: placed.props.definition, body: slot("root", "Fake") } };
  const foreign = { ...placed, props: { ...placed.props, definition: foreignObject.props.definition } };
  const twoRoots = defineObject<{}>()({ exports: {}, body: () => [slot("a", "A"), slot("b", "B")] as never });
  const malformed = twoRoots({ handle: twoRoots.instance("bad") }) as any;
  const nested = defineObject<{}>()({ exports: {}, body: () => slot("root", "Outer", [placed]) as never });
  const nestedPlacement = nested({ handle: nested.instance("outer") }) as any;

  const expect = (element: unknown, code: string, message: string) => {
    let caught: unknown;
    try { evaluate(slot("scene", "Scene", [element]), { catalogs: [] }); } catch (error) { caught = error; }
    if (!(caught instanceof BuildError)) throw new Error(`Expected ${code}`);
    equal(caught.code, code);
    equal(caught.message, message);
  };
  expect(fake, "APPLY_OBJECT_INVALID", "Object 'fake': handle is missing or unbranded.");
  expect(foreign, "APPLY_OBJECT_INVALID", "Object 'desk': handle does not belong to this definition.");
  expect(malformed, "APPLY_OBJECT_INVALID", "Object 'bad': body must contain exactly one root Slot.");
  expect(nestedPlacement, "APPLY_OBJECT_INVALID", "Object 'outer': nested Objects are not supported in roadmap 5.");
});

test("reserved Object namespaces reject outer keys, while Slot-only Objects need no catalog", () => {
  const definition = defineObject<{}>()({ exports: { root: "$slot:root" }, body: () => slot("root", "Body") as never });
  const placed = definition({ handle: definition.instance("desk") }) as any;
  let caught: unknown;
  try { evaluate(slot("scene", "Scene", [slot("desk::root", "Conflict"), placed]), { catalogs: [] }); }
  catch (error) { caught = error; }
  if (!(caught instanceof BuildError)) throw new Error("Expected reserved namespace error");
  equal(caught.code, "DUPLICATE_KEY");
  equal(caught.message, "Stable key 'desk::root' occupies reserved Object namespace 'desk::'.");
  const result = evaluate(slot("scene", "Scene", [placed]), { catalogs: [] });
  deepEqual(result.document.children, [{ slot: { name: "Body", key: "desk::root" } }]);
});

test("JSON values that resemble Object elements remain ordinary field data", () => {
  const data = { kind: "object", props: { handle: { id: "fake" }, body: { kind: "slot" } } };
  const result = evaluate(slot("root", "Scene", [component("plain", "Synthetic.Device", { fields: { Payload: data } })]));
  deepEqual(result.document.components?.[0]?.fields?.Payload, data);
});

test("Object cloning preserves handwritten Field origins through final IR binding", () => {
  const nameSource = known("object-field-name.tsx", 40);
  const valueSource = known("object-field-value.tsx", 50);
  const aliasSource = known("object-field-alias.tsx", 60);
  const props = { name: "Enabled", value: true, key: "enabled" };
  const field = sourceElement(() => {
    const element = { kind: "field", props };
    captureElement(element);
    return element;
  }, { source: known("object-field.tsx", 30), attributes: {
    name: { source: known("object-field-name-attribute.tsx", 39), valueSource: nameSource },
    value: { source: known("object-field-value-attribute.tsx", 49), valueSource },
    key: { source: known("object-field-key-attribute.tsx", 59), valueSource: aliasSource },
  } }, props);
  const definition = defineObject<{}>()({ exports: { root: "$slot:root" }, body: () => slot("root", "Body", [
    component("light", "Synthetic.Device", { children: field }),
  ]) as never });
  const placed = definition({ handle: definition.instance("desk") }) as any;
  const result = evaluate(slot("scene", "Scene", [placed]));
  const light = result.document.children?.[0]?.components?.[0];
  const origins = originOfIr(light!);
  deepEqual(origins?.attributes.fields?.children?.Enabled?.source, nameSource);
  deepEqual(origins?.attributes.fields?.children?.Enabled?.valueSource, valueSource);
  deepEqual(origins?.attributes.fieldAliases?.children?.["desk::enabled"]?.valueSource, aliasSource);
});

test("driver-owned Field mode and Field name, value, and alias origins reach final IR", () => {
  const driverField = { kind: "field", props: { name: "Enabled", mode: "driver-owned", key: "enabled" } };
  const driver = component("driver", "Synthetic.Device", { children: driverField });
  const driverResult = evaluate(slot("root", "Scene", [driver]));
  deepEqual(driverResult.document.components?.[0]?.propertyModes, { Enabled: "driver-owned" });
  deepEqual(driverResult.document.components?.[0]?.fieldAliases, { enabled: "Enabled" });

  const nameSource = known("field-name.tsx", 10);
  const valueSource = known("field-value.tsx", 20);
  const aliasSource = known("field-alias.tsx", 30);
  const props = { name: "Enabled", value: true, key: "enabled" };
  const field = sourceElement(() => {
    const element = { kind: "field", props };
    captureElement(element);
    return element;
  }, {
    source: known("field.tsx", 0), attributes: {
      name: { source: known("name-attribute.tsx", 9), valueSource: nameSource },
      value: { source: known("value-attribute.tsx", 19), valueSource },
      key: { source: known("key-attribute.tsx", 29), valueSource: aliasSource },
    },
  }, props);
  const child = component("light", "Synthetic.Device", { children: field });
  const result = evaluate(slot("root", "Scene", [child]));
  const componentIr = result.document.components?.[0];
  const origins = originOfIr(componentIr!);
  deepEqual(origins?.attributes.fields?.children?.Enabled?.source, nameSource);
  deepEqual(origins?.attributes.fields?.children?.Enabled?.valueSource, valueSource);
  deepEqual(origins?.attributes.fieldAliases?.children?.enabled?.valueSource, aliasSource);
});
