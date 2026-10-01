// Contract test: every fixture this tool emits must be accepted by the real
// `resoloop validate` command (offline, no --strict, no Resonite connection).
//
// Exit codes:
//   0  all checks passed
//   1  at least one check failed
//   3  resoloop.dll not found — `dotnet build ResoLoop.slnx` was not run;
//      the contract test did NOT run (report as "not executed")

import * as fs from "node:fs";
import * as path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { buildFile } from "../src/cli.js";

const here = path.dirname(fileURLToPath(import.meta.url)); // dist/scripts
const packageRoot = path.resolve(here, "..", ".."); // tools/resoloop-jsx
const repoRoot = path.resolve(packageRoot, "..", ".."); // repository root
const fixturesDir = path.join(packageRoot, "test", "fixtures");

// Find src/RLoop.Cli/bin recursively for net10.0/resoloop.dll; newest wins.
function findCliDll(): string | undefined {
  const binRoot = path.join(repoRoot, "src", "RLoop.Cli", "bin");
  if (!fs.existsSync(binRoot)) return undefined;
  const matches: string[] = [];
  const stack = [binRoot];
  while (stack.length > 0) {
    const dir = stack.pop()!;
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) stack.push(full);
      else if (
        entry.isFile() &&
        /^resoloop\.dll$/i.test(entry.name) &&
        /net10\.0/i.test(full)
      )
        matches.push(full);
    }
  }
  matches.sort((a, b) => fs.statSync(b).mtimeMs - fs.statSync(a).mtimeMs);
  return matches[0];
}

/** Fixtures that must produce a document `resoloop validate` accepts. */
const SUCCESS_FIXTURES: { file: string; draft?: boolean; error?: string }[] = [
  { file: "generated-shape-oracle.tsx" },
  { file: "ownership.tsx" },
  { file: "nesting.tsx" },
  { file: "reference.tsx" },
  { file: "composition.tsx" },
  { file: "repeat.tsx" },
  { file: "conditional.tsx" },
  { file: "key-stability-base.tsx" },
  { file: "key-stability-inserted.tsx" },
  { file: "scoped.tsx" },
  { file: "scoped-inserted.tsx" },
  { file: "missing-key-draft.tsx", draft: true, error: "APPLY_DRAFT_KEY_UNSTABLE" },
];

async function main(): Promise<number> {
  const dll = findCliDll();
  if (dll === undefined) {
    process.stderr.write(
      "contract: resoloop.dll not found under src/RLoop.Cli/bin/**/net10.0/\n" +
        "contract: build the .NET CLI first: dotnet build ResoLoop.slnx\n"
    );
    return 3;
  }
  process.stdout.write(`contract: using ${dll}\n`);

  let checked = 0;
  let passed = 0;
  let failed = 0;
  const tmpDir = fs.mkdtempSync(path.join(fixturesDir, ".resoloop-jsx-contract-"));
  try {
    for (const fixture of SUCCESS_FIXTURES) {
      checked++;
      const entry = path.join(fixturesDir, fixture.file);
      const outJson = path.join(
        tmpDir,
        fixture.file.replace(/\.tsx$/, ".json")
      );
      const build = await buildFile(entry, {
        draft: fixture.draft === true,
        output: outJson,
      });
      if (build.exitCode !== 0) {
        failed++;
        process.stderr.write(
          `contract: FAIL ${fixture.file}: resoloop-jsx build exited ${build.exitCode}\n` +
            build.errors.join("\n") + "\n"
        );
        continue;
      }
      const run = spawnSync("dotnet", [dll, "validate", outJson], {
        cwd: repoRoot,
        encoding: "utf8",
      });
      if (run.error) {
        failed++;
        process.stderr.write(
          `contract: FAIL ${fixture.file}: could not launch dotnet: ${String(run.error)}\n`
        );
        continue;
      }
      if (fixture.error ? run.status !== 0 && (run.stdout + run.stderr).includes(fixture.error) : run.status === 0) {
        passed++;
        process.stdout.write(`contract: PASS ${fixture.file}${fixture.error ? ` (rejected: ${fixture.error})` : ""}\n`);
      } else {
        failed++;
        process.stderr.write(
          `contract: FAIL ${fixture.file}: resoloop validate exited ${run.status}\n` +
            `stdout: ${run.stdout}\nstderr: ${run.stderr}\n`
        );
      }
    }

    for (const fixture of [
      { file: "scoped-source.json", error: "" },
      { file: "scoped-collision.json", error: "APPLY_EXPANDED_KEY_CONFLICT" },
      { file: "scoped-separator.json", error: "APPLY_SCOPE_INVALID" },
      { file: "scoped-outside.json", error: "APPLY_REFERENCE_NOT_FOUND" },
    ]) {
      checked++;
      const run = spawnSync("dotnet", [dll, "validate", path.join(fixturesDir, fixture.file), "--json"], { cwd: repoRoot, encoding: "utf8" });
      const accepted = !run.error && (fixture.error
        ? run.status !== 0 && (run.stdout + run.stderr).includes(fixture.error)
        : run.status === 0);
      if (accepted) {
        passed++;
        process.stdout.write(`contract: PASS ${fixture.file}${fixture.error ? ` (rejected: ${fixture.error})` : ""}\n`);
      } else {
        failed++;
        process.stderr.write(`contract: FAIL ${fixture.file}: ${run.error ?? run.stdout + run.stderr}\n`);
      }
    }

    // Negative check: schemaVersion as a JSON number must be rejected.
    checked++;
    const invalidPath = path.join(fixturesDir, "invalid-schema-version.json");
    const negative = spawnSync("dotnet", [dll, "validate", invalidPath], {
      cwd: repoRoot,
      encoding: "utf8",
    });
    if (negative.error) {
      failed++;
      process.stderr.write(
        `contract: FAIL invalid-schema-version.json: could not launch dotnet: ${String(negative.error)}\n`
      );
    } else if (negative.status !== 0) {
      passed++;
      process.stdout.write(
        `contract: PASS invalid-schema-version.json (rejected, exit ${negative.status})\n`
      );
    } else {
      failed++;
      process.stderr.write(
        "contract: FAIL invalid-schema-version.json: validate unexpectedly succeeded\n"
      );
    }
  } finally {
    try {
      fs.rmSync(tmpDir, { recursive: true, force: true });
    } catch {
      // best effort
    }
  }

  process.stdout.write(
    `contract: checked=${checked} passed=${passed} failed=${failed}\n`
  );
  return failed === 0 ? 0 : 1;
}

main().then(
  (code) => process.exit(code),
  (err) => {
    process.stderr.write(`contract: ${err?.message ?? String(err)}\n`);
    process.exit(1);
  }
);
