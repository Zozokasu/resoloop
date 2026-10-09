import { test } from "node:test";
import { deepEqual, equal, ok } from "node:assert/strict";
import { BuildError } from "../src/evaluate.js";
import { collectObjects } from "../src/object-expand.js";
import { defineObject } from "../src/object-definition.js";
import { componentFields } from "../src/bound-field.js";
import { buildErrorOrigin, sourceElement, type ElementOrigin } from "../src/source-map.js";
import type { ComponentElement, FieldElement, FragmentElement, ObjectElement, ScopeElement, SlotElement } from "../src/elements.js";
import { objectTestRuntimeTable } from "./object-test-helpers.js";

const slot = (key: string | undefined, children?: unknown) =>
  ({ kind: "slot", props: { key, name: key ?? "root", children } } as unknown as SlotElement);
const component = (key: string | undefined, props: Record<string, unknown> = {}) =>
  ({ kind: "component", props: { key, type: "Virtual.Device", ...props } } as unknown as ComponentElement);
const field = (name: string, key?: string) =>
  ({ kind: "field", props: { name, key, mode: "runtime" } } as FieldElement);
const fragment = (children: unknown) => ({ kind: "fragment", props: { children } } as unknown as FragmentElement);
const scope = (instanceKey: string, children: unknown) =>
  ({ kind: "scope", props: { instanceKey, children } } as unknown as ScopeElement);

function makeObject(id: string, body: unknown = slot("root")) {
  const definition = defineObject<{}>()({ exports: {}, body: () => body as never });
  return definition({ handle: definition.instance(id) }) as ObjectElement;
}

function failure(action: () => unknown, code: string, message: string): BuildError {
  let captured: unknown;
  try { action(); } catch (error) { captured = error; }
  if (!(captured instanceof BuildError)) throw new Error("Expected BuildError");
  equal(captured.code, code);
  equal(captured.message, message);
  return captured;
}

function known(file: string): ElementOrigin {
  const source = { status: "known" as const, file, sha256: "a".repeat(64),
    range: { start: { offset: 0, line: 1, column: 1 }, end: { offset: 1, line: 1, column: 2 } } };
  return { source, attributes: { handle: { source } } };
}

test("Object-local keys reset scope and remain stable across parents and ordering", () => {
  const first = makeObject("lamp", slot("root", [scope("segment", slot("key"))]));
  const second = makeObject("desk", slot("root", [slot("child")]));
  const root = slot("page", [scope("outside", first), second]);
  const context = collectObjects(root, []);
  equal(context.needsPass, true);
  equal(context.targets.has("slot:lamp::root"), true);
  equal(context.targets.has("slot:lamp::segment::key"), true);
  equal(context.targets.has("slot:desk::root"), true);
  equal(context.targets.has("slot:desk::child"), true);
  const bodyRoot = first.props.body as unknown as { props: { children: unknown[] } };
  deepEqual(context.contexts.get(first.props.body as object), { scope: "lamp", owner: "lamp" });
  deepEqual(context.contexts.get(bodyRoot.props.children[0] as object), { scope: "lamp::segment", owner: "lamp" });
  const moved = collectObjects(slot("other-parent", [second, scope("elsewhere", first)]), []);
  equal(moved.targets.has("slot:lamp::root"), true);
  equal(moved.targets.has("slot:desk::child"), true);
});

test("Object IDs are unique and handles must be branded for their exact definition", () => {
  const definition = defineObject<{}>()({ exports: {}, body: () => slot("root") });
  const duplicateRoot = slot("root", [definition({ handle: definition.instance("same") }), definition({ handle: definition.instance("same") })]);
  failure(() => collectObjects(duplicateRoot, []), "APPLY_OBJECT_DUPLICATE", "Object instance 'same' occurs twice.");
  const one = makeObject("fake");
  (one as any).props.handle = { id: "fake", exports: {} };
  failure(() => collectObjects(slot("root", [one]), []), "APPLY_OBJECT_INVALID",
    "Object 'fake': handle is missing or unbranded.");
  const branded = defineObject<{}>()({ exports: {}, body: () => slot("root") });
  const template = branded.instance("valid");
  const brand = Object.getOwnPropertySymbols(template)[0];
  ok(brand);
  const invalidId = { id: "bad id", exports: {} };
  Object.defineProperty(invalidId, brand, { enumerable: false, value: Object.getOwnPropertyDescriptor(template, brand)?.value });
  const malformed = branded({ handle: template }) as ObjectElement;
  (malformed as any).props.handle = invalidId;
  failure(() => collectObjects(slot("root", [malformed]), []), "APPLY_OBJECT_INVALID",
    "Object 'bad id': invalid instance ID.");
  const left = defineObject<{}>()({ exports: {}, body: () => slot("root") });
  const right = defineObject<{}>()({ exports: {}, body: () => slot("root") });
  failure(() => collectObjects(slot("root", [left({ handle: right.instance("foreign") as never })]), []),
    "APPLY_OBJECT_INVALID", "Object 'foreign': handle does not belong to this definition.");
});

test("Object body requires one root Slot, explicit local keys, and no nested Objects", () => {
  failure(() => collectObjects(slot("root", [makeObject("empty", false)]), []), "APPLY_OBJECT_INVALID",
    "Object 'empty': body must contain exactly one root Slot.");
  failure(() => collectObjects(slot("root", [makeObject("two", [slot("a"), slot("b")])]), []), "APPLY_OBJECT_INVALID",
    "Object 'two': body must contain exactly one root Slot.");
  failure(() => collectObjects(slot("root", [makeObject("component", component("c"))]), []), "APPLY_OBJECT_INVALID",
    "Object 'component': body must contain exactly one root Slot.");
  failure(() => collectObjects(slot("root", [makeObject("missing", slot(undefined))]), []), "APPLY_SCOPE_INVALID",
    "$.objects[missing] requires a non-empty local key without ':' (reserved for '::')");
  const inner = makeObject("inner");
  failure(() => collectObjects(slot("root", [makeObject("outer", slot("root", [inner]))]), []), "APPLY_OBJECT_INVALID",
    "Object 'outer': nested Objects are not supported in roadmap 5.");
});

test("Object namespaces reserve exact IDs and member declarations index all sources", () => {
  const object = makeObject("desk");
  failure(() => collectObjects(slot("root", [component("desk::ghost"), object]), []), "DUPLICATE_KEY",
    "Stable key 'desk::ghost' occupies reserved Object namespace 'desk::'.");
  failure(() => collectObjects(slot("root", [slot("desk::ghost"), object]), []), "DUPLICATE_KEY",
    "Stable key 'desk::ghost' occupies reserved Object namespace 'desk::'.");
  failure(() => collectObjects(slot("root", [object, component("c", {
    fields: { Enabled: true }, children: field("Other", "desk::field"),
  })]), []), "DUPLICATE_KEY",
    "Stable key 'desk::field' occupies reserved Object namespace 'desk::'.");
  failure(() => collectObjects(slot("root", [object, scope("desk", slot("inside"))]), []), "DUPLICATE_KEY",
    "Stable key 'desk' occupies reserved Object namespace 'desk::'.");
  failure(() => collectObjects(slot("root", [object, scope("desk", [])]), []), "DUPLICATE_KEY",
    "Stable key 'desk' occupies reserved Object namespace 'desk::'.");
  failure(() => collectObjects(slot("root", [object, component("c", { fieldAliases: { "desk::ghost": "Enabled" } })]), []), "DUPLICATE_KEY",
    "Stable key 'desk::ghost' occupies reserved Object namespace 'desk::'.");
  const good = collectObjects(slot("root", [object, component("desk0::ghost")]), []);
  equal(good.targets.has("component:desk0::ghost"), true);
  const ordinary = collectObjects(slot("root", [object, component("desk")]), []);
  equal(ordinary.targets.has("component:desk"), true);

  const members = makeObject("unit", slot("root", [component("device", {
    fields: { Enabled: "$member:light.Enabled" }, initialFields: { Count: undefined }, propertyModes: { RuntimeOnly: "runtime" },
    fieldAliases: { enabled: "Enabled", ghost: "Missing" },
    children: fragment([field("Child")]),
  })]));
  const context = collectObjects(slot("root", [members]), []);
  for (const member of ["Enabled", "Count", "RuntimeOnly", "Child"])
    equal(context.targets.has(`member:unit::device.${member}`), true);
  equal(context.targets.has("field:unit::enabled"), true);
  equal(context.targets.has("field:unit::ghost"), false);
  equal(context.targets.get("member:unit::device.Enabled")?.componentType, "Virtual.Device");
  equal("scalar" in (context.targets.get("member:unit::device.Enabled") ?? {}), false);
  const childFragment = fragment(field("Enabled"));
  const childAlias = collectObjects(slot("root", [makeObject("child", slot("root", [component("device", {
    fieldAliases: { enabled: "Enabled" }, children: childFragment,
  })]))]), []);
  equal(childAlias.targets.get("field:child::enabled")?.member, "Enabled");
  deepEqual(childAlias.contexts.get(childFragment), { scope: "child", owner: "child" });
  deepEqual(childAlias.contexts.get(childAlias.targets.get("member:child::device.Enabled")!.element),
    { scope: "child", owner: "child" });
  const modeAndField = collectObjects(slot("root", [component("mode", {
    fields: { Trigger: "$export:activates" }, propertyModes: { Enabled: "runtime" }, children: field("Enabled"),
  })]), []);
  equal(modeAndField.targets.has("member:mode.Enabled"), true);
});

test("Object target references activate collection without resolving exports before unit10", () => {
  const table = objectTestRuntimeTable();
  const runtimeFieldsFactory = componentFields as any;
  const target = runtimeFieldsFactory("Synthetic.Device", table).ref("unit::light", "Enabled");
  const definition = defineObject<{}>()({ exports: { enabled: target }, body: () => slot("root", [
    component("light", { fields: { Enabled: true } }),
  ]) });
  const object = definition({ handle: definition.instance("unit") });
  const context = collectObjects(slot("root", [object]), [table]);
  equal(context.objects.get("unit")!.exports.size, 0);
  equal(context.targets.get("member:unit::light.Enabled")?.kind, "member");
  equal(context.decoded.size, 0);
  equal(context.needsPass, true);
});

test("field aliases and Field declarations reject duplicates with evaluator-compatible diagnostics", () => {
  const duplicateAlias = slot("root", [component("device", {
    fields: { Enabled: "$export:activates" }, fieldAliases: { a: "Enabled" }, children: field("Other", "a"),
  })]);
  failure(() => collectObjects(duplicateAlias, []), "APPLY_FIELD_ALIAS_DUPLICATE",
    "Field alias 'a' is declared more than once");
  const duplicateField = slot("root", [component("device", {
    fields: { Enabled: true, Trigger: "$export:activates" }, children: field("Enabled"),
  })]);
  failure(() => collectObjects(duplicateField, []), "APPLY_FIELD_DUPLICATE",
    "Member 'Enabled' at $.components[0] is declared more than once");
  const first = component("c1", { fields: { A: "$export:activates" }, fieldAliases: { same: "A" } });
  const second = component("c2", { fields: { B: 2 }, fieldAliases: { same: "B" } });
  failure(() => collectObjects(slot("root", [first, second]), []), "APPLY_FIELD_ALIAS_DUPLICATE",
    "Field alias 'same' is declared more than once");
});

test("legacy input bypasses collection; Object tokens and bound metadata activate it", () => {
  const legacy = slot("root", [slot("same"), component("same")]);
  const untouched = collectObjects(legacy, []);
  equal(untouched.needsPass, false);
  equal(untouched.root, legacy);
  equal(untouched.targets.size, 0);
  const tokenRoot = slot("root", [component("c", { fields: { Ref: "$export:malformed" } })]);
  equal(collectObjects(tokenRoot, []).needsPass, true);
  const table = objectTestRuntimeTable();
  const bound = componentFields("Synthetic.Device" as never, table).Field({ name: "Enabled", value: true });
  const boundContext = collectObjects(slot("root", [component("c", { children: bound })]), []);
  equal(boundContext.needsPass, true);
  equal(boundContext.targets.has("member:c.Enabled"), true);
});

test("JSON object-shaped values are not traversed as JSX placements", () => {
  const jsonObject = { kind: "object", props: { handle: "plain JSON" } };
  const legacy = slot("root", [component("legacy", { fields: { Payload: jsonObject } })]);
  const legacyContext = collectObjects(legacy, []);
  equal(legacyContext.needsPass, false);
  equal(legacyContext.root, legacy);

  const object = makeObject("json", slot("root", [component("device", { fields: { Payload: jsonObject } })]));
  const context = collectObjects(slot("page", [object]), []);
  equal(context.objects.size, 1);
  equal(context.targets.has("member:json::device.Payload"), true);
});

test("duplicate Object errors point to the second consumed handle and relate the first", () => {
  const definition = defineObject<{}>()({ exports: {}, body: () => slot("root") });
  const firstOrigin = known("first.ts");
  const secondOrigin = known("second.ts");
  const first = sourceElement(() => definition({ handle: definition.instance("same") }), firstOrigin,
    { handle: definition.instance("same") }) as ObjectElement;
  const second = sourceElement(() => definition({ handle: definition.instance("same") }), secondOrigin,
    { handle: definition.instance("same") }) as ObjectElement;
  const error = failure(() => collectObjects(slot("root", [first, second]), []), "APPLY_OBJECT_DUPLICATE",
    "Object instance 'same' occurs twice.");
  const origin = buildErrorOrigin(error)!;
  if (origin.source.status !== "known") throw new Error("Expected known second-handle origin");
  equal(origin.source.file, "second.ts");
  const related = origin.related[0];
  if (!related || related.status !== "known") throw new Error("Expected known first-handle related origin");
  equal(related.file, "first.ts");
});
