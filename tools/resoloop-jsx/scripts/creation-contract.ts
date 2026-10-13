import * as fs from "node:fs";
import * as path from "node:path";
import { spawnSync } from "node:child_process";
import { buildFile } from "../src/cli.js";
import { variablesWorkspace } from "../test/variables-test-helpers.js";
import { deepEqual, equal } from "node:assert/strict";

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/** Offline creation envelope checks backed by handwritten fixture expectations. */
export async function runCreationContract(
  cliPath: string,
  packageRoot: string,
): Promise<{ passed: number; failed: number }> {
  let passed = 0;
  let failed = 0;
  const fixtures = path.join(packageRoot, "test", "fixtures", "creation-contract");
  const temporaryRoot = fs.mkdtempSync(path.join(packageRoot, ".creation-contract-"));
  const tempResolved = path.resolve(temporaryRoot);
  const packageResolved = path.resolve(packageRoot) + path.sep;
  let variables: ReturnType<typeof variablesWorkspace> | undefined;

  async function check(name: string, action: () => Promise<void>): Promise<void> {
    try {
      await action();
      passed++;
      process.stdout.write(`creation-contract: PASS ${name}\n`);
    } catch (error) {
      failed++;
      process.stderr.write(`creation-contract: FAIL ${name}: ${describe(error)}\n`);
    }
  }

  try {
    const cases = [
      { name: "siblings", file: "siblings.tsx", workspace: false },
      { name: "variables", file: "variables.tsx", workspace: true },
    ] as const;
    for (const fixture of cases) {
      await check(`${fixture.name}/handwritten-envelope-and-cli`, async () => {
        let entry: string;
        let projectRoot: string;
        if (fixture.workspace) {
          const source = fs.readFileSync(path.join(fixtures, fixture.file), { encoding: "utf8" });
          variables = variablesWorkspace(source);
          entry = variables.entry;
          projectRoot = variables.root;
        } else {
          entry = path.join(fixtures, fixture.file);
          projectRoot = fixtures;
        }

        const output = path.join(temporaryRoot, `${fixture.name}.json`);
        const build = await buildFile(entry, { target: "creation", output, projectRoot });
        equal(build.exitCode, 0, build.errors.join("\n"));
        const actual = JSON.parse(fs.readFileSync(output, { encoding: "utf8" }));
        actual.authoring.projectRoot = "<PROJECT_ROOT>";
        const expected = JSON.parse(fs.readFileSync(path.join(fixtures, `${fixture.name}.expected.json`), { encoding: "utf8" }));
        deepEqual(actual, expected);

        const run = spawnSync("dotnet", [cliPath, "validate", output, "--json"], {
          encoding: "utf8",
          cwd: path.resolve(packageRoot, "..", ".."),
        });
        if (run.error) throw new Error(`could not launch dotnet: ${String(run.error)}`);
        equal(run.status, 0, `validate stdout: ${run.stdout}\nvalidate stderr: ${run.stderr}`);
        const validation = JSON.parse(run.stdout);
        equal(validation.data.valid, true, run.stdout + run.stderr);
      });
      variables?.cleanup();
      variables = undefined;
    }
  } finally {
    variables?.cleanup();
    if (!tempResolved.startsWith(packageResolved) || path.basename(tempResolved).indexOf(".creation-contract-") !== 0)
      throw new Error(`refusing to clean unexpected temporary path: ${tempResolved}`);
    fs.rmSync(tempResolved, { recursive: true, force: true });
  }

  return { passed, failed };
}
