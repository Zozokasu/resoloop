import * as fs from "node:fs";
import * as path from "node:path";
import { spawnSync } from "node:child_process";
import { buildFile } from "../src/cli.js";
import { variablesWorkspace } from "../test/variables-test-helpers.js";
import { deepEqual, equal, ok } from "node:assert/strict";

const CHECKS = [
  "built-ir-handwritten-oracle",
  "csharp-validates-built-ir",
  "repeated-build-byte-identical",
  "expanded-modes-provider-identity-wire-count",
] as const;

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function readExample(packageRoot: string): string {
  const readme = fs.readFileSync(path.join(packageRoot, "README.md"), { encoding: "utf8" }).replace(/\r\n/g, "\n");
  const marker = "<!-- variables-example -->";
  const markerIndex = readme.indexOf(marker);
  if (markerIndex < 0) throw new Error("README variables example marker is missing");
  const following = readme.slice(markerIndex + marker.length).replace(/^\n+/, "");
  const fence = /^```tsx\n([\s\S]*?)\n```/.exec(following);
  if (!fence) throw new Error("README marker is not followed by a TSX fence");
  return fence[1].replace(/\r\n/g, "\n");
}

/** Offline contract for the README variables example; this never connects to Resonite. */
export async function runVariablesContract(
  cliPath: string,
  packageRoot: string,
): Promise<{ passed: number; failed: number }> {
  let passed = 0;
  let failed = 0;
  let workspace: ReturnType<typeof variablesWorkspace> | undefined;
  let built: Awaited<ReturnType<typeof buildFile>> | undefined;
  let setupError: string | undefined;

  try {
    const source = readExample(packageRoot);
    workspace = variablesWorkspace(source);
    process.stdout.write("variables-contract: fixture is fictional offline-only; not live evidence\n");
    built = await buildFile(workspace.entry, { projectRoot: workspace.root, output: workspace.output });
  } catch (error) {
    setupError = describe(error);
  }

  const check = async (name: typeof CHECKS[number], action: () => void | Promise<void>): Promise<void> => {
    try {
      if (setupError !== undefined) throw new Error(setupError);
      await action();
      passed++;
      process.stdout.write(`variables-contract: PASS ${name}\n`);
    } catch (error) {
      failed++;
      process.stderr.write(`variables-contract: FAIL ${name}: ${describe(error)}\n`);
    }
  };

  try {
    await check("built-ir-handwritten-oracle", () => {
      if (!workspace || !built) throw new Error("README source built successfully");
      equal(built.exitCode, 0, built.errors.join("\n"));
      if (!built.document) throw new Error("buildFile returned no document");
      const { authoring: _authoring, ...semantic } = built.document;
      const expected = JSON.parse(fs.readFileSync(
        path.join(packageRoot, "test/fixtures/variables-contract/ir.expected.json"), { encoding: "utf8" }));
      deepEqual(semantic, expected);
    });

    await check("csharp-validates-built-ir", () => {
      if (!workspace || !built) throw new Error("README source built successfully");
      equal(built.exitCode, 0, built.errors.join("\n"));
      const run = spawnSync("dotnet", [cliPath, "validate", workspace.output, "--json"], {
        cwd: workspace.root,
        encoding: "utf8",
      });
      if (run.error) throw new Error(`could not launch dotnet: ${String(run.error)}`);
      if (run.status !== 0)
        throw new Error(`validate exited ${run.status}\nstdout: ${run.stdout}\nstderr: ${run.stderr}`);
      process.stdout.write("variables-contract: csharp validate exit=0 url=none\n");
    });

    await check("repeated-build-byte-identical", async () => {
      if (!workspace || !built) throw new Error("README source built successfully");
      equal(built.exitCode, 0, built.errors.join("\n"));
      const firstBytes = fs.readFileSync(workspace.output);
      const repeated = await buildFile(workspace.entry, { projectRoot: workspace.root, output: workspace.output });
      equal(repeated.exitCode, 0, repeated.errors.join("\n"));
      deepEqual(fs.readFileSync(workspace.output), firstBytes);
    });

    await check("expanded-modes-provider-identity-wire-count", () => {
      if (!built) throw new Error("README source built successfully");
      equal(built.exitCode, 0, built.errors.join("\n"));
      if (!built.document) throw new Error("buildFile returned no document");
      const document: any = built.document;
      deepEqual(document.components.slice(0, 2).map((component: any) => component.propertyModes), [
        { Enabled: "driver-owned" }, { Enabled: "runtime" },
      ]);
      for (const component of document.components.slice(0, 2)) {
        ok(!( "fields" in component));
        ok(!( "initialFields" in component));
      }
      equal(document.children.length, 3);
      equal(document.children[0].slot.name, "DynamicVariables");
      equal(document.children[0].components.length, 1);
      deepEqual(document.children[0].components[0].identityFields, ["VariableName"]);
      deepEqual(document.children.slice(1).map((child: any) => child.components.length), [1, 1]);
      deepEqual(document.children.slice(1).map((child: any) => child.components[0].type), [
        "[FrooxEngine]FrooxEngine.DynamicValueVariableDriver<bool>",
        "[FrooxEngine]FrooxEngine.DynamicField<bool>",
      ]);
    });
  } finally {
    workspace?.cleanup();
  }

  return { passed, failed };
}
