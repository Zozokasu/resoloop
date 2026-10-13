import { test } from "node:test";
import { deepEqual, equal, ok } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile } from "../src/cli.js";
import { bind, defineVariables, drive, types, variable } from "../src/variables.js";
import { variablesWorkspace } from "./variables-test-helpers.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

function assertReadonlyBoolVariableTypes(): void {
  // @ts-expect-error bool variables require a boolean default
  variable(types.bool, "true");
  const declaration = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });
  drive(declaration.vars.on);
  bind(declaration.vars.on);
  // @ts-expect-error declared variable references are read-only
  declaration.vars.on = declaration.vars.on;
}
void assertReadonlyBoolVariableTypes;

test("README variables example builds the handwritten semantic IR fixture", async () => {
  const readme = fs.readFileSync(path.join(packageRoot, "README.md"), { encoding: "utf8" }).replace(/\r\n/g, "\n");
  const marker = "<!-- variables-example -->";
  const markerIndex = readme.indexOf(marker);
  ok(markerIndex >= 0, "README variables example marker is present");
  const following = readme.slice(markerIndex + marker.length).replace(/^\n+/, "");
  const fence = /^```tsx\n([\s\S]*?)\n```/.exec(following);
  if (!fence) throw new Error("README marker is followed by a TSX fence");

  const workspace = variablesWorkspace(fence[1]);
  try {
    const result = await buildFile(workspace.entry, { projectRoot: workspace.root });
    equal(result.exitCode, 0, result.errors.join("\n"));
    if (!result.document) throw new Error("buildFile returned no document");
    const { authoring: _authoring, ...semantic } = result.document;
    const expected = JSON.parse(fs.readFileSync(
      path.join(packageRoot, "test/fixtures/variables-contract/ir.expected.json"), { encoding: "utf8" }));
    deepEqual(semantic, expected);

    const components = semantic.components;
    if (!components) throw new Error("built document has no components");
    deepEqual(components.slice(0, 2).map(component => component.propertyModes), [
      { Enabled: "driver-owned" }, { Enabled: "runtime" },
    ]);
    for (const component of components.slice(0, 2)) {
      ok(!("fields" in component));
      ok(!("initialFields" in component));
    }
    const children = semantic.children;
    if (!children) throw new Error("built document has no children");
    const providerComponents = children[0]?.components;
    if (!providerComponents?.[0]) throw new Error("built document has no provider component");
    deepEqual(providerComponents[0].identityFields, ["VariableName"]);
  } finally { workspace.cleanup(); }
});
