import { test } from "node:test";
import { deepEqual, equal, ok } from "node:assert/strict";
import { BuildError, evaluate } from "../src/evaluate.js";
import { componentFields } from "../src/bound-field.js";
import { bind, defineVariables, drive, types, variable, Variables } from "../src/variables.js";
import { buildFile } from "../src/cli.js";
import { variablesRuntimeTable, variablesWorkspace } from "./variables-test-helpers.js";

const table = variablesRuntimeTable();
const Light = componentFields("Fixture.Objects.Lamp" as never, table) as any;
const slot = (props: Record<string, unknown> = {}, children?: unknown) => ({
  kind: "slot", props: { key: "scene", name: "Lamp", ...props, ...(children === undefined ? {} : { children }) },
}) as any;
const component = (key: string, children: unknown) => ({
  kind: "component", props: { key, type: Light.type, children },
}) as any;

function expectBuildError(action: () => unknown, code: string, message: string): void {
  let thrown: unknown;
  try { action(); } catch (error) { thrown = error; }
  if (!(thrown instanceof BuildError)) throw new Error(`Expected BuildError ${code}`);
  equal(thrown.code, code);
  equal(thrown.message, message);
}

test("ordinary Slot props reject bare references and nested bindings", () => {
  const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
  const unresolved = "Variable 'lamp.on' is unresolved or outside its declared Space.";
  expectBuildError(() => evaluate(slot({ tag: set.vars.on })), "CREATION_VARIABLE_UNRESOLVED", unresolved);
  expectBuildError(() => evaluate(slot({ tag: set.vars.on }, [Variables({ declaration: set })])),
    "CREATION_VARIABLE_UNRESOLVED", unresolved);
  expectBuildError(() => evaluate(slot({ position: [set.vars.on] })), "CREATION_VARIABLE_UNRESOLVED", unresolved);
  const componentProp = { kind: "component", props: { key: "light", type: Light.type, custom: set.vars.on } };
  expectBuildError(() => evaluate(slot({}, [componentProp, Variables({ declaration: set })])),
    "CREATION_VARIABLE_UNRESOLVED", unresolved);
  expectBuildError(() => evaluate(slot({ tag: { nested: drive(set.vars.on) } }, [Variables({ declaration: set })])),
    "CREATION_VARIABLE_TYPE_UNSUPPORTED", "Variable bindings must be the value of a catalog-bound Field.");
});

test("ordinary Slot props preserve plain document identity when no variable API is present", () => {
  const root = slot({ tag: "author-tag" });
  const before = JSON.stringify(root);
  const document: any = evaluate(root).document;
  deepEqual(document.ownership, { key: "scene" });
  equal(document.slot.key, "scene");
  equal(document.slot.tag, "author-tag");
  equal(JSON.stringify(root), before);
});

test("mutating a bound Field name cannot reuse another member's bool evidence", () => {
  const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
  for (const binding of [drive(set.vars.on), bind(set.vars.on)]) {
    const field = Light.Field({ name: "Enabled", value: binding }) as any;
    field.props.name = "Count";
    const root = slot({}, [component("light", field), Variables({ declaration: set })]);
    expectBuildError(() => evaluate(root, { catalogs: [table] }), "CREATION_VARIABLE_TYPE_MISMATCH",
      "Variable 'bool' cannot bind to Field 'Fixture.Objects.Lamp.Count' of type 'int32'.");
  }

  const uncertain = Light.Field({ name: "Enabled", value: drive(set.vars.on) }) as any;
  uncertain.props.name = "Uncertain";
  const uncertainRoot = slot({}, [component("light", uncertain), Variables({ declaration: set })]);
  expectBuildError(() => evaluate(uncertainRoot, { catalogs: [table] }),
    "CREATION_VARIABLE_TYPE_UNSUPPORTED",
    "Variable binding target 'Fixture.Objects.Lamp.Uncertain' requires a confirmed bool Field.");
});

test("unchanged confirmed bool members still support drive and bind", () => {
  const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
  const root = slot({}, [
    component("light", Light.Field({ name: "Enabled", value: drive(set.vars.on) })),
    component("toggle", Light.Field({ name: "Enabled", value: bind(set.vars.on) })),
    Variables({ declaration: set }),
  ]);
  const document: any = evaluate(root, { catalogs: [table] }).document;
  deepEqual(document.components.slice(0, 2).map((item: any) => item.propertyModes), [
    { Enabled: "driver-owned" }, { Enabled: "runtime" },
  ]);
  equal(document.children.length, 3);
});

test("buildFile rejects a Slot tag reference and a Field renamed after binding", async () => {
  const plainRefSource = [
    'import { Slot } from "resoloop-jsx";',
    'import { defineVariables, variable, types } from "resoloop-jsx/variables";',
    'const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });',
    'export default <Slot key="scene" name="Lamp" tag={set.vars.on as any} />;',
  ].join("\n");
  const plainRef = variablesWorkspace(plainRefSource);
  try {
    const result = await buildFile(plainRef.entry, { projectRoot: plainRef.root });
    equal(result.exitCode, 1);
    equal(result.errors[0], "CREATION_VARIABLE_UNRESOLVED: Variable 'lamp.on' is unresolved or outside its declared Space.; TSX unknown");
  } finally { plainRef.cleanup(); }

  const changedFieldSource = [
    'import { Slot, Component, componentFields } from "resoloop-jsx";',
    'import { defineVariables, variable, types, drive, Variables } from "resoloop-jsx/variables";',
    'import { catalog } from "./.resoloop/catalog-fields.js";',
    'const Light = componentFields("Fixture.Objects.Lamp", catalog);',
    'const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });',
    'const field = Light.Field({ name: "Enabled", value: drive(set.vars.on) }) as any;',
    'field.props.name = "Count";',
    'export default <Slot key="scene" name="Lamp">',
    '  <Component key="light" type={Light.type}>{field}</Component>',
    '  <Variables declaration={set} />',
    '</Slot>;',
  ].join("\n");
  const changedField = variablesWorkspace(changedFieldSource);
  try {
    const result = await buildFile(changedField.entry, { projectRoot: changedField.root });
    equal(result.exitCode, 1);
    equal(result.errors[0], `CREATION_VARIABLE_TYPE_MISMATCH: Variable 'bool' cannot bind to Field 'Fixture.Objects.Lamp.Count' of type 'int32'.; TSX unknown`);
  } finally { changedField.cleanup(); }
});
