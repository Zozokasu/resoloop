import { test } from "node:test";
import { equal } from "node:assert/strict";
import { BuildError, evaluate } from "../src/evaluate.js";
import { buildErrorLocation, sourceElement } from "../src/source-map.js";
import { componentFields } from "../src/bound-field.js";
import { bind, defineVariables, drive, types, variable, Variables } from "../src/variables.js";
import { objectTestRuntimeTable } from "./object-test-helpers.js";

const slot = (key: string, children?: unknown) => ({ kind: "slot", props: { key, name: key, children } }) as any;
const component = (key: string, type: string, props: Record<string, unknown> = {}) =>
  ({ kind: "component", props: { key, type, ...props } }) as any;
const table = objectTestRuntimeTable();
const fields = componentFields("Synthetic.Device" as never, table) as any;
const known = (file: string) => ({ status: "known" as const, file, sha256: "a".repeat(64),
  range: { start: { offset: 0, line: 3, column: 4 }, end: { offset: 1, line: 3, column: 5 } } });
const sourcedVariables = (set: unknown, file: string) => {
  const source = known(file);
  return sourceElement(() => Variables({ declaration: set as never }), { source, attributes: {
    declaration: { source, valueSource: source },
  } });
};

function expectBuildError(action: () => unknown, code: string, message: string): BuildError {
  let captured: unknown;
  try { action(); } catch (error) { captured = error; }
  if (!(captured instanceof BuildError)) throw new Error("Expected BuildError");
  equal(captured.code, code);
  equal(captured.message, message);
  return captured;
}

test("declarations resolve forward references and reject repeated set placement, including reused marker identity", () => {
  const set = defineVariables({ scopeKey: "scene", variables: { on: variable(types.bool, true) } });
  const target = component("light", "Synthetic.Device", {
    children: fields.Field({ name: "Enabled", value: drive(set.vars.on) }),
  });
  const root = slot("lamp", [target, Variables({ declaration: set })]);
  equal(evaluate(root, { catalogs: [table] }).document.components.length, 2);
  const marker = Variables({ declaration: set });
  expectBuildError(() => evaluate(slot("lamp", [marker, marker]), { catalogs: [table] }),
    "CREATION_VARIABLE_DUPLICATE", "Variable declaration 'scene' occurs more than once in a document or Slot.");
  expectBuildError(() => evaluate(slot("lamp", [Variables({ declaration: set }), Variables({ declaration: set })]), { catalogs: [table] }),
    "CREATION_VARIABLE_DUPLICATE", "Variable declaration 'scene' occurs more than once in a document or Slot.");
  const first = defineVariables({ scopeKey: "first", variables: { on: variable(types.bool, true) } });
  const second = defineVariables({ scopeKey: "second", variables: { on: variable(types.bool, true) } });
  const located = expectBuildError(() => evaluate(slot("lamp", [sourcedVariables(first, "first.ts"), sourcedVariables(second, "second.ts")]),
    { catalogs: [table] }), "CREATION_VARIABLE_DUPLICATE",
  "Variable declaration 'second' occurs more than once in a document or Slot.");
  equal(buildErrorLocation(located), "second.ts:3:4");
});

test("unresolved refs, unbound Fields, and JSON token leakage are rejected", () => {
  const set = defineVariables({ scopeKey: "scene", variables: { on: variable(types.bool, false) } });
  expectBuildError(() => evaluate(slot("lamp", component("light", "Synthetic.Device", {
    children: fields.Field({ name: "Enabled", value: drive(set.vars.on) }),
  })), { catalogs: [table] }), "CREATION_VARIABLE_UNRESOLVED",
  "Variable 'scene.on' is unresolved or outside its declared Space.");
  expectBuildError(() => evaluate(slot("lamp", [Variables({ declaration: set }), component("light", "Synthetic.Device", {
    children: { kind: "field", props: { name: "Enabled", value: bind(set.vars.on) } },
  })]), { catalogs: [table] }), "CREATION_VARIABLE_TYPE_UNSUPPORTED",
  "Variable bindings must be the value of a catalog-bound Field.");
  expectBuildError(() => evaluate(slot("lamp", [Variables({ declaration: set }), component("light", "Synthetic.Device", {
    fields: { nested: { value: drive(set.vars.on) } },
  })]), { catalogs: [table] }), "CREATION_VARIABLE_TYPE_UNSUPPORTED",
  "Variable bindings must be the value of a catalog-bound Field.");
  expectBuildError(() => evaluate(slot("lamp", component("light", "Synthetic.Device", {
    fields: { nested: { value: set.vars.on } },
  })), { catalogs: [table] }), "CREATION_VARIABLE_UNRESOLVED",
  "Variable 'scene.on' is unresolved or outside its declared Space.");
  expectBuildError(() => evaluate(slot("lamp", component("legacy", "Synthetic.Device", {
    children: { kind: "field", props: { name: "Enabled", value: { nested: set.vars.on } } },
  })), { catalogs: [table] }), "CREATION_VARIABLE_UNRESOLVED",
  "Variable 'scene.on' is unresolved or outside its declared Space.");
  expectBuildError(() => evaluate(slot("lamp", component("nested", "Synthetic.Device", {
    fields: { nested: { kind: "component", props: { hidden: set.vars.on } } },
  })), { catalogs: [table] }), "CREATION_VARIABLE_UNRESOLVED",
  "Variable 'scene.on' is unresolved or outside its declared Space.");
  expectBuildError(() => evaluate(slot("lamp", [Variables({ declaration: set }), component("light", "Synthetic.Device", {
    children: [fields.Field({ name: "Enabled", value: drive(set.vars.on) }), fields.Field({ name: "Enabled", value: true })],
  })]), { catalogs: [table] }), "CREATION_DRIVEN_FIELD_DECLARED",
  "Variable binding target 'Enabled' is declared more than once.");
  const modeResult = evaluate(slot("lamp", [Variables({ declaration: set }), component("mode", "Synthetic.Device", {
    propertyModes: { Enabled: "config", Other: "initial" },
    children: fields.Field({ name: "Enabled", value: drive(set.vars.on) }),
  })]), { catalogs: [table] }).document as any;
  equal(modeResult.components[0].propertyModes.Enabled, "driver-owned");
  equal(modeResult.components[0].propertyModes.Other, "initial");
});

test("raw DynamicVariableSpace boundaries shadow bindings", () => {
  const set = defineVariables({ scopeKey: "scene", variables: { on: variable(types.bool, true) } });
  const target = component("light", "Synthetic.Device", {
    children: fields.Field({ name: "Enabled", value: drive(set.vars.on) }),
  });
  expectBuildError(() => evaluate(slot("lamp", [Variables({ declaration: set }), slot("island", [
    component("raw-space", "FrooxEngine.DynamicVariableSpace", { fields: { SpaceName: "other" } }), target,
  ])]), { catalogs: [table] }), "CREATION_SPACE_SHADOWED",
  "Variable Space 'scene' is shadowed by a DynamicVariableSpace.");
});

test("raw providers use physical Space identity and duplicate names are rejected", () => {
  const provider = (key: string) => component(key, "[FrooxEngine]FrooxEngine.DynamicValueVariable<string>", {
    fields: { VariableName: "manual" },
  });
  const rawOnly = evaluate(slot("raw", [
    component("space", "FrooxEngine.DynamicVariableSpace", { fields: { SpaceName: "raw" } }),
    provider("one"), provider("two"),
  ]));
  equal(rawOnly.document.components.length, 3);
  const setForPresence = defineVariables({ scopeKey: "separate", variables: { other: variable(types.bool, false) } });
  expectBuildError(() => evaluate(slot("raw", [
    component("space", "FrooxEngine.DynamicVariableSpace", { fields: { SpaceName: "raw" } }),
    provider("one"), provider("two"), slot("declarations", Variables({ declaration: setForPresence })),
  ])), "CREATION_VARIABLE_DUPLICATE", "Variable 'raw.manual' is declared more than once in a Space.");
  const set = defineVariables({ scopeKey: "generated", variables: { manual: variable(types.bool, true) } });
  expectBuildError(() => evaluate(slot("lamp", [provider("raw"), Variables({ declaration: set })])),
    "CREATION_VARIABLE_DUPLICATE", "Variable 'generated.manual' is declared more than once in a Space.");
});

test("generated stable keys collide with author keys instead of silently suffixing", () => {
  const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
  expectBuildError(() => evaluate(slot("scene", [
    component("__rlv_5_space_4_lamp_5_scene", "Synthetic.Device"),
    Variables({ declaration: set }),
  ]), { catalogs: [table] }), "DUPLICATE_KEY",
  'component key "__rlv_5_space_4_lamp_5_scene" is used more than once (first at $.components[0], again at $.components[1])');
});

test("references cannot escape their owning Slot or cross a nested Variables owner", () => {
  const outer = defineVariables({ scopeKey: "scene", variables: { on: variable(types.bool, true) } });
  const outsideTarget = component("light", "Synthetic.Device", {
    children: fields.Field({ name: "Enabled", value: drive(outer.vars.on) }),
  });
  expectBuildError(() => evaluate(slot("page", [
    slot("owner", Variables({ declaration: outer })),
    slot("outside", outsideTarget),
  ]), { catalogs: [table] }), "CREATION_VARIABLE_UNRESOLVED",
  "Variable 'scene.on' is unresolved or outside its declared Space.");

  const inner = defineVariables({ scopeKey: "nested", variables: { other: variable(types.bool, false) } });
  const crossingTarget = component("nested-light", "Synthetic.Device", {
    children: fields.Field({ name: "Enabled", value: drive(outer.vars.on) }),
  });
  expectBuildError(() => evaluate(slot("page", [
    Variables({ declaration: outer }),
    slot("inner-owner", [Variables({ declaration: inner }), crossingTarget]),
  ]), { catalogs: [table] }), "CREATION_VARIABLE_UNRESOLVED",
  "Variable 'scene.on' is unresolved or outside its declared Space.");
});

test("raw Space above a Variables owner does not shadow its binding", () => {
  const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
  const document = evaluate(slot("page", [
    component("outer-space", "FrooxEngine.DynamicVariableSpace", { fields: { SpaceName: "outer" } }),
    slot("scene", [
      component("light", "Synthetic.Device", {
        children: fields.Field({ name: "Enabled", value: drive(set.vars.on) }),
      }),
      Variables({ declaration: set }),
    ]),
  ]), { catalogs: [table] }).document as any;
  equal(document.children[0].components[1].key, "__rlv_5_space_4_lamp_5_scene");
  equal(document.children[0].children[1].slot.key, "__rlv_4_wire_4_lamp_2_on_5_light_7_Enabled");
});

test("generated wire names collide with authored sibling Slot names", () => {
  const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
  const wireName = "__rlv_4_wire_4_lamp_2_on_5_light_7_Enabled";
  expectBuildError(() => evaluate(slot("scene", [
    component("light", "Synthetic.Device", {
      children: fields.Field({ name: "Enabled", value: drive(set.vars.on) }),
    }),
    { kind: "slot", props: { key: "authored-wire-name", name: wireName } } as any,
    Variables({ declaration: set }),
  ]), { catalogs: [table] }), "DUPLICATE_SIBLING_NAME",
  `Sibling slot name "${wireName}" is repeated under $ (children[0] and children[2]); choose distinct sibling names`);
});

test("documents without variable APIs keep the prior Scope validation behavior", () => {
  const invalid = slot("lamp", { kind: "scope", props: { instanceKey: "bad:key", children: component("light", "Synthetic.Device") } });
  expectBuildError(() => evaluate(invalid, { catalogs: [table] }), "APPLY_SCOPE_INVALID",
    "$.children.instanceKey requires a non-empty local key without ':' (reserved for '::')");
});
