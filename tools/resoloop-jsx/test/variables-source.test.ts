import { test } from "node:test";
import { equal } from "node:assert/strict";
import { variablesWorkspace } from "./variables-test-helpers.js";
import { buildFile } from "../src/cli.js";

async function expectDiagnostic(source: string, code: string, message: string, line: number, column: number): Promise<void> {
  const w = variablesWorkspace(source);
  try {
    const result = await buildFile(w.entry, { projectRoot: w.root });
    equal(result.exitCode, 1, result.errors.join("\n"));
    equal(result.errors[0], `${code}: ${message}; TSX ${w.entry}:${line}:${column}`);
  } finally { w.cleanup(); }
}

async function expectUnknownDiagnostic(source: string, code: string, message: string): Promise<void> {
  const w = variablesWorkspace(source);
  try {
    const result = await buildFile(w.entry, { projectRoot: w.root });
    equal(result.exitCode, 1, result.errors.join("\n"));
    equal(result.errors[0], `${code}: ${message}; TSX unknown`);
  } finally { w.cleanup(); }
}

const baseImports = `import { Slot, Component, componentFields } from "resoloop-jsx";
import { defineVariables, variable, types, drive, Variables } from "resoloop-jsx/variables";
import { catalog } from "./.resoloop/catalog-fields.js";
const fields = componentFields("Fixture.Objects.Lamp", catalog);`;

test("helper load failures carry exact handwritten TSX source locations, including aliases", async () => {
  await expectDiagnostic(`import { Slot } from "resoloop-jsx";
import { defineVariables as define } from "resoloop-jsx/variables";
const bad = define({
  scopeKey: "bad:key",
  variables: {},
});
export default <Slot key="root" name="Root" />;`,
  "CREATION_ID_INVALID", "Variable identifier 'bad:key' must match ^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$.", 3, 13);
  await expectDiagnostic(`import { Slot } from "resoloop-jsx";
import { variable, types } from "resoloop-jsx/variables";
variable(types.bool, 1 as any);
export default <Slot key="root" name="Root" />;`,
  "CREATION_VARIABLE_TYPE_MISMATCH", "Variable default for 'bool' must be boolean.", 3, 1);
  await expectDiagnostic(`import { Slot } from "resoloop-jsx";
import { variable } from "resoloop-jsx/variables";
variable({ kind: "int32" } as any, 1);
export default <Slot key="root" name="Root" />;`,
  "CREATION_VARIABLE_TYPE_UNSUPPORTED", "Variable type 'int32' is unsupported; only bool is supported.", 3, 1);
});

test("declaration, binding, unresolved, and Space diagnostics point to authored values", async () => {
  await expectDiagnostic(`import { Slot } from "resoloop-jsx";
import { defineVariables, variable, types, Variables } from "resoloop-jsx/variables";
const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
export default <Slot key="root" name="Root">
  <Variables declaration={set} />
  <Variables declaration={set} />
</Slot>;`,
  "CREATION_VARIABLE_DUPLICATE", "Variable declaration 'lamp' occurs more than once in a document or Slot.", 6, 27);

  await expectDiagnostic(`${baseImports}
const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
export default <Slot key="root" name="Root">
  <Slot key="owner" name="Owner"><Variables declaration={set} /></Slot>
  <Slot key="outside" name="Outside">
    <Component key="light" type="Fixture.Objects.Lamp">
      <fields.Field name="Enabled" value={drive(set.vars.on)} />
    </Component>
  </Slot>
</Slot>;`,
  "CREATION_VARIABLE_UNRESOLVED", "Variable 'lamp.on' is unresolved or outside its declared Space.", 10, 43);

  await expectDiagnostic(`${baseImports}
const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
export default <Slot key="root" name="Root">
  <Component key="raw" type="FrooxEngine.DynamicVariableSpace" fields={{ SpaceName: "other" }} />
  <Variables declaration={set} />
</Slot>;`,
  "CREATION_SPACE_SHADOWED", "Variable Space 'lamp' is shadowed by a DynamicVariableSpace.", 8, 27);
});

test("duplicate binding Fields report the binding value in either author order", async () => {
  const beforeBinding = `${baseImports}
const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
export default <Slot key="root" name="Root">
  <Component key="light" type="Fixture.Objects.Lamp">
    <fields.Field name="Enabled" value={true} />
    <fields.Field name="Enabled" value={drive(set.vars.on)} />
  </Component>
  <Variables declaration={set} />
</Slot>;`;
  await expectDiagnostic(beforeBinding, "CREATION_DRIVEN_FIELD_DECLARED",
    "Variable binding target 'Enabled' is declared more than once.", 9, 41);

  const afterBinding = `${baseImports}
const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
export default <Slot key="root" name="Root">
  <Component key="light" type="Fixture.Objects.Lamp">
    <fields.Field name="Enabled" value={drive(set.vars.on)} />
    <fields.Field name="Enabled" value={true} />
  </Component>
  <Variables declaration={set} />
</Slot>;`;
  await expectDiagnostic(afterBinding, "CREATION_DRIVEN_FIELD_DECLARED",
    "Variable binding target 'Enabled' is declared more than once.", 9, 41);
});

test("bound Field scalar and non-catalog Field diagnostics use handwritten values", async () => {
  const countBinding = `${baseImports}
const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
export default <Slot key="root" name="Root">
  <Component key="light" type="Fixture.Objects.Lamp">
    <fields.Field
      name="Count"
      value={drive(set.vars.on) as any}
    />
  </Component>
  <Variables declaration={set} />
</Slot>;`;
  await expectDiagnostic(countBinding, "CREATION_VARIABLE_TYPE_MISMATCH",
    "Variable 'bool' cannot bind to Field 'Fixture.Objects.Lamp.Count' of type 'int32'.", 10, 14);

  const unknownField = `${baseImports}
const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
export default <Slot key="root" name="Root">
  <Component key="light" type="Fixture.Objects.Lamp">
    <fields.Field
      name="Uncertain"
      value={drive(set.vars.on) as any}
    />
  </Component>
  <Variables declaration={set} />
</Slot>;`;
  await expectDiagnostic(unknownField, "CREATION_VARIABLE_TYPE_UNSUPPORTED",
    "Variable binding target 'Fixture.Objects.Lamp.Uncertain' requires a confirmed bool Field.", 10, 14);
});

test("binding Fields cannot coexist with an authored fields or initialFields value", async () => {
  const sourceFor = (section: "fields" | "initialFields") => `${baseImports}
const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
export default <Slot key="root" name="Root">
  <Component key="light" type="Fixture.Objects.Lamp"
    ${section}={{ Enabled: false }}>
    <fields.Field
      name="Enabled"
      value={drive(set.vars.on)}
    />
  </Component>
  <Variables declaration={set} />
</Slot>;`;
  for (const section of ["fields", "initialFields"] as const)
    await expectDiagnostic(sourceFor(section), "CREATION_DRIVEN_FIELD_DECLARED",
      "Variable binding target 'Enabled' is declared more than once.", 11, 14);
});

test("raw references and bindings hidden in fields or initialFields retain unknown forwarding", async () => {
  const bareRef = `${baseImports}
const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
export default <Slot key="root" name="Root">
  <Component key="light" type="Fixture.Objects.Lamp">
    <fields.Field
      name="Enabled"
      value={set.vars.on as any}
    />
  </Component>
  <Variables declaration={set} />
</Slot>;`;
  await expectUnknownDiagnostic(bareRef, "CREATION_VARIABLE_UNRESOLVED", "Variable reference requires drive(...) or bind(...).");

  const objectFields = (section: "fields" | "initialFields") => `${baseImports}
const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
export default <Slot key="root" name="Root">
  <Component key="light" type="Fixture.Objects.Lamp"
    ${section}={{ Enabled: drive(set.vars.on) } as any} />
  <Variables declaration={set} />
</Slot>;`;
  for (const section of ["fields", "initialFields"] as const)
    await expectUnknownDiagnostic(objectFields(section), "CREATION_VARIABLE_TYPE_UNSUPPORTED",
      "Variable bindings must be the value of a catalog-bound Field.");
});

test("unrelated same-name functions are not instrumented and variable-free failures keep legacy wrapping", async () => {
  const unrelated = variablesWorkspace(`import { BuildError, Slot } from "resoloop-jsx";
function drive() { throw new BuildError("CREATION_ID_INVALID", "unrelated call"); }
drive();
export default <Slot key="root" name="Root" />;`);
  try {
    const result = await buildFile(unrelated.entry, { projectRoot: unrelated.root });
    equal(result.exitCode, 1);
    equal(result.errors[0], "CREATION_ID_INVALID: unrelated call; TSX unknown");
  } finally { unrelated.cleanup(); }

  const legacy = variablesWorkspace(`import { evaluate } from "resoloop-jsx";
evaluate(null);
export default null;`);
  try {
    const result = await buildFile(legacy.entry, { projectRoot: legacy.root });
    equal(result.exitCode, 1);
    equal(result.errors[0], "EVAL_FAILED: failed to load compiled module: The entry module's default export must evaluate to exactly one <Slot> element; got none");
  } finally { legacy.cleanup(); }
});
