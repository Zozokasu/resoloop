import { test } from "node:test";
import { deepEqual, equal } from "node:assert/strict";
import { evaluate } from "../src/evaluate.js";
import { componentFields } from "../src/bound-field.js";
import { bind, defineVariables, drive, types, variable, Variables } from "../src/variables.js";
import { objectTestRuntimeTable } from "./object-test-helpers.js";

const slot = (key: string, children?: unknown) => ({ kind: "slot", props: { key, name: key, children } }) as any;
const component = (key: string, type: string, props: Record<string, unknown> = {}) =>
  ({ kind: "component", props: { key, type, ...props } }) as any;
const table = objectTestRuntimeTable();
const fields = componentFields("Synthetic.Device" as never, table) as any;

test("Variables expand to a handwritten schema-v1 oracle in author order", () => {
  const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
  const light = component("light", "Synthetic.Device", { propertyModes: { Enabled: "driver-owned" },
    children: fields.Field({ name: "Enabled", value: drive(set.vars.on) }) });
  const toggle = component("toggle", "Synthetic.Device", { propertyModes: { Enabled: "runtime" },
    children: fields.Field({ name: "Enabled", value: bind(set.vars.on) }) });
  const root = { kind: "slot", props: { key: "scene", name: "Lamp",
    children: [light, toggle, Variables({ declaration: set })] } };
  const before = JSON.stringify(root);
  const actual = evaluate(root, { catalogs: [table] }).document;
  const expected = {
    schemaVersion: "1", ownership: { key: "scene" },
    slot: { name: "Lamp", key: "scene" },
    components: [
      { type: "Synthetic.Device", key: "light", propertyModes: { Enabled: "driver-owned" } },
      { type: "Synthetic.Device", key: "toggle", propertyModes: { Enabled: "runtime" } },
      { type: "[FrooxEngine]FrooxEngine.DynamicVariableSpace", key: "__rlv_5_space_4_lamp_5_scene",
        fields: { SpaceName: "lamp", OnlyDirectBinding: false } },
    ],
    children: [
      { slot: { name: "DynamicVariables", key: "__rlv_9_variables_4_lamp_5_scene" },
        components: [{ type: "[FrooxEngine]FrooxEngine.DynamicValueVariable<bool>",
          key: "__rlv_8_variable_4_lamp_5_scene_2_on", fields: { VariableName: "on", Value: true }, identityFields: ["VariableName"] }] },
      { slot: { name: "__rlv_4_wire_4_lamp_2_on_5_light_7_Enabled", key: "__rlv_4_wire_4_lamp_2_on_5_light_7_Enabled" },
        components: [{ type: "[FrooxEngine]FrooxEngine.DynamicValueVariableDriver<bool>",
          key: "__rlv_7_binding_4_lamp_2_on_5_light_7_Enabled", fields: { VariableName: "on", DefaultValue: true, Target: "$member:light.Enabled" } }] },
      { slot: { name: "__rlv_4_wire_4_lamp_2_on_6_toggle_7_Enabled", key: "__rlv_4_wire_4_lamp_2_on_6_toggle_7_Enabled" },
        components: [{ type: "[FrooxEngine]FrooxEngine.DynamicField<bool>",
          key: "__rlv_7_binding_4_lamp_2_on_6_toggle_7_Enabled", fields: { VariableName: "on", TargetField: "$member:toggle.Enabled" } }] },
    ],
  };
  deepEqual(actual, expected);
  equal(JSON.stringify(root), before);
  equal(JSON.stringify(evaluate(root, { catalogs: [table] }).document), JSON.stringify(actual));
});

test("Scope keeps its author wrapper and applies scope exactly once to generated identities", () => {
  const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, false) } });
  const source = { kind: "slot", props: { key: "scene", name: "Lamp", children: [{ kind: "scope", props: { instanceKey: "room", children: [
    component("light", "Synthetic.Device", { children: fields.Field({ name: "Enabled", value: drive(set.vars.on) }) }),
    Variables({ declaration: set }),
  ] } }] } };
  const document = evaluate(source, { catalogs: [table] }).document as any;
  equal(document.components[0].key, "room::light");
  equal(document.components[1].key, "room::__rlv_5_space_4_lamp_5_scene");
  equal(document.children[0].slot.key, "room::__rlv_9_variables_4_lamp_5_scene");
  equal(document.children[1].slot.key, "room::__rlv_4_wire_4_lamp_2_on_15_room%3A%3Alight_7_Enabled");
});

test("same variable scope names remain separate across owners and each declaration emits every provider", () => {
  const first = defineVariables({ scopeKey: "shared", variables: {
    on: variable(types.bool, true), off: variable(types.bool, false),
  } });
  const second = defineVariables({ scopeKey: "shared", variables: { on: variable(types.bool, false) } });
  const root = slot("page", [
    slot("first", Variables({ declaration: first })),
    slot("second", Variables({ declaration: second })),
  ]);
  const document = evaluate(root).document as any;
  equal(document.children[0].components[0].fields.SpaceName, "shared");
  equal(document.children[0].children[0].components.length, 2);
  equal(document.children[0].children[0].components[0].fields.VariableName, "on");
  equal(document.children[0].children[0].components[1].fields.VariableName, "off");
  equal(document.children[1].components[0].fields.SpaceName, "shared");
  equal(document.children[1].children[0].components.length, 1);
  equal(document.children[1].children[0].components[0].fields.VariableName, "on");
  equal(document.children[0].components[0].key, "__rlv_5_space_6_shared_5_first");
  equal(document.children[1].components[0].key, "__rlv_5_space_6_shared_6_second");
});

test("a Field fragment inside a Component keeps generated wires in the physical Slot", () => {
  const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
  const root = { kind: "slot", props: { key: "scene", name: "Lamp", children: [
    component("light", "Synthetic.Device", { children: { kind: "fragment", props: {
      children: fields.Field({ name: "Enabled", value: drive(set.vars.on) }),
    } } }),
    Variables({ declaration: set }),
  ] } };
  const document = evaluate(root, { catalogs: [table] }).document as any;
  equal(document.children.length, 2);
  equal(document.children[1].slot.key, "__rlv_4_wire_4_lamp_2_on_5_light_7_Enabled");
  equal(document.components[0].key, "light");
});

test("unscoped colon keys and delimiter-like tuples keep exact target identities", () => {
  const colonSet = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
  const colonRoot = slot("scene", [
    component("c:one", "Synthetic.Device", { children: fields.Field({ name: "Enabled", value: drive(colonSet.vars.on) }) }),
    Variables({ declaration: colonSet }),
  ]);
  const colonDocument = evaluate(colonRoot, { catalogs: [table] }).document as any;
  equal(colonDocument.children[1].slot.key, "__rlv_4_wire_4_lamp_2_on_7_c%3Aone_7_Enabled");
  equal(colonDocument.children[1].components[0].fields.Target, "$member:c:one.Enabled");

  const tupleSet = defineVariables({ scopeKey: "lamp", variables: {
    a_b: variable(types.bool, true), a: variable(types.bool, false),
  } });
  const tupleRoot = slot("scene", [
    component("c", "Synthetic.Device", { children: fields.Field({ name: "Enabled", value: drive(tupleSet.vars.a_b) }) }),
    component("b_c", "Synthetic.Device", { children: fields.Field({ name: "Enabled", value: drive(tupleSet.vars.a) }) }),
    Variables({ declaration: tupleSet }),
  ]);
  const tupleDocument = evaluate(tupleRoot, { catalogs: [table] }).document as any;
  equal(tupleDocument.children[1].slot.key, "__rlv_4_wire_4_lamp_3_a_b_1_c_7_Enabled");
  equal(tupleDocument.children[2].slot.key, "__rlv_4_wire_4_lamp_1_a_3_b_c_7_Enabled");
});
