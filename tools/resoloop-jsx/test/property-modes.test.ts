import { test } from "node:test";
import { deepEqual, equal, throws } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile } from "../src/cli.js";
import { evaluate, BuildError } from "../src/evaluate.js";

test("propertyModes TSX matches independent handwritten output", async () => {
  const fixtures = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../test/fixtures");
  const result = await buildFile(path.join(fixtures, "property-modes.tsx"));
  equal(result.exitCode, 0);
  const expected = JSON.parse(fs.readFileSync(path.join(fixtures, "property-modes.handwritten.json"), { encoding: "utf8" }));
  expected.authoring = { projectRoot: fixtures, source: "property-modes.tsx", ownershipSource: "root-key" };
  deepEqual(result.document, expected);
});

test("propertyModes rejects invalid modes and contradictory field sections", () => {
  for (const props of [
    { fields: { X: 1 }, propertyModes: { X: "initial" } },
    { initialFields: { X: 1 }, propertyModes: { X: "config" } },
    { propertyModes: { X: "typo" } },
  ]) throws(() => evaluate({ kind: "slot", props: { name: "Modes", key: "modes", children:
    { kind: "component", props: { type: "Test.Modes", key: "values", ...props } } } }),
    (error: unknown) => error instanceof BuildError && error.code === "APPLY_COMPONENT_FIELD_POLICY_CONFLICT");
});
