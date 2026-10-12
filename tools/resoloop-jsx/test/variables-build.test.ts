import { test } from "node:test";
import { deepEqual, equal, ok } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { buildFile } from "../src/cli.js";
import { evaluate } from "../src/evaluate.js";
import { componentFields } from "../src/bound-field.js";
import { defineVariables, drive, bind, types, variable, Variables } from "../src/variables.js";
import { variablesRuntimeTable, variablesWorkspace } from "./variables-test-helpers.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const source = `import { Slot, Component, componentFields } from "resoloop-jsx";
import { defineVariables, variable, types, drive, bind, Variables } from "resoloop-jsx/variables";
import { catalog } from "./.resoloop/catalog-fields.js";
const fields = componentFields("Fixture.Objects.Lamp", catalog);
const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
export default <Slot key="scene" name="Lamp"><Component key="light" type="Fixture.Objects.Lamp"><fields.Field name="Enabled" value={drive(set.vars.on)} /></Component><Component key="toggle" type="Fixture.Objects.Lamp"><fields.Field name="Enabled" value={bind(set.vars.on)} /></Component><Variables declaration={set} /></Slot>;
`;

test("buildFile and the regular CLI build Variables with the same handwritten schema-v1 IR", async () => {
  const w = variablesWorkspace(source);
  try {
    const built = await buildFile(w.entry, { projectRoot: w.root, output: w.output });
    equal(built.exitCode, 0, built.errors.join("\n"));
    const expected = {
      schemaVersion: "1", ownership: { key: "scene" }, slot: { name: "Lamp", key: "scene" },
      components: [
        { type: "Fixture.Objects.Lamp", key: "light", propertyModes: { Enabled: "driver-owned" } },
        { type: "Fixture.Objects.Lamp", key: "toggle", propertyModes: { Enabled: "runtime" } },
        { type: "[FrooxEngine]FrooxEngine.DynamicVariableSpace", key: "__rlv_5_space_4_lamp_5_scene",
          fields: { SpaceName: "lamp", OnlyDirectBinding: false } },
      ],
      children: [
        { slot: { name: "DynamicVariables", key: "__rlv_9_variables_4_lamp_5_scene" },
          components: [{ type: "[FrooxEngine]FrooxEngine.DynamicValueVariable<bool>", key: "__rlv_8_variable_4_lamp_5_scene_2_on",
            fields: { VariableName: "on", Value: true }, identityFields: ["VariableName"] }] },
        { slot: { name: "__rlv_4_wire_4_lamp_2_on_5_light_7_Enabled", key: "__rlv_4_wire_4_lamp_2_on_5_light_7_Enabled" },
          components: [{ type: "[FrooxEngine]FrooxEngine.DynamicValueVariableDriver<bool>", key: "__rlv_7_binding_4_lamp_2_on_5_light_7_Enabled",
            fields: { VariableName: "on", DefaultValue: true, Target: "$member:light.Enabled" } }] },
        { slot: { name: "__rlv_4_wire_4_lamp_2_on_6_toggle_7_Enabled", key: "__rlv_4_wire_4_lamp_2_on_6_toggle_7_Enabled" },
          components: [{ type: "[FrooxEngine]FrooxEngine.DynamicField<bool>", key: "__rlv_7_binding_4_lamp_2_on_6_toggle_7_Enabled",
            fields: { VariableName: "on", TargetField: "$member:toggle.Enabled" } }] },
      ],
      authoring: { projectRoot: w.root, source: "main.tsx", ownershipSource: "root-key" },
    };
    deepEqual(built.document, expected);
    const fields = componentFields("Fixture.Objects.Lamp" as never, variablesRuntimeTable()) as any;
    const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
    const programRoot = { kind: "slot", props: { key: "scene", name: "Lamp", children: [
      { kind: "component", props: { key: "light", type: "Fixture.Objects.Lamp", children: fields.Field({ name: "Enabled", value: drive(set.vars.on) }) } },
      { kind: "component", props: { key: "toggle", type: "Fixture.Objects.Lamp", children: fields.Field({ name: "Enabled", value: bind(set.vars.on) }) } },
      Variables({ declaration: set }),
    ] } } as any;
    const { authoring: _authoring, ...expectedSemantic } = expected;
    deepEqual(evaluate(programRoot, { catalogs: [variablesRuntimeTable()] }).document, expectedSemantic);

    const cli = spawnSync("node", [path.join(packageRoot, "dist/src/cli.js"), "build", w.entry,
      "-o", w.output, "--project-root", w.root], { cwd: packageRoot, encoding: "utf8" });
    equal(cli.status, 0, cli.stderr);
    deepEqual(JSON.parse(fs.readFileSync(w.output, { encoding: "utf8" })), expected);
    const firstBytes = fs.readFileSync(w.output);
    const repeated = await buildFile(w.entry, { projectRoot: w.root, output: w.output });
    equal(repeated.exitCode, 0, repeated.errors.join("\n"));
    deepEqual(fs.readFileSync(w.output), firstBytes);

    const bundlePath = path.join(w.root, "bundle", "build.json");
    const bundle = await buildFile(w.entry, { projectRoot: w.root, bundle: true, catalog: w.catalog, buildId: "vars", output: bundlePath });
    equal(bundle.exitCode, 0, bundle.errors.join("\n"));
    const inputList = JSON.parse(fs.readFileSync(bundlePath, { encoding: "utf8" })).inputs.files as Array<{ path: string }>;
    ok(inputList.some(item => item.path === w.entry));
  } finally { w.cleanup(); }
});
