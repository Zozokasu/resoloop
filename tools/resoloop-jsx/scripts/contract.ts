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
import { checkSourceOracle } from "../test/source-oracle.js";
import { deepEqual, equal, ok } from "node:assert/strict";

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

    // V11: synthetic original and independent handwritten diagnostics. Expected
    // codes/paths come only from this table, never from generated types/catalog.
    const catalogDir = path.join(fixturesDir, "catalog-v11");
    const oracle = JSON.parse(fs.readFileSync(path.join(catalogDir, "oracle.handwritten.json"))) as {
      cases: { name: string; document: unknown; issues: { code: string; path: string }[] }[];
    };
    for (const fixture of oracle.cases) {
      checked++;
      const file = path.join(tmpDir, `catalog-${fixture.name}.json`);
      fs.writeFileSync(file, JSON.stringify(fixture.document));
      const run = spawnSync("dotnet", [dll, "validate", file, "--catalog", path.join(catalogDir, "catalog.synthetic.json"), "--url", "not-a-url", "--json"], { cwd: repoRoot, encoding: "utf8" });
      let matches = false;
      try {
        const output = JSON.parse(fixture.issues.length ? run.stderr : run.stdout);
        const issues = (fixture.issues.length ? output.error.context.issues : output.data.issues) as { code: string; path: string }[];
        matches = !run.error && run.status === (fixture.issues.length ? 6 : 0) &&
          JSON.stringify(issues.map(i => ({ code: i.code, path: i.path }))) === JSON.stringify(fixture.issues) &&
          (fixture.issues.length > 0 || output.data.strict === false);
      } catch { /* Invalid output fails the contract. */ }
      if (matches) { passed++; process.stdout.write(`contract: PASS synthetic-catalog/${fixture.name}\n`); }
      else { failed++; process.stderr.write(`contract: FAIL synthetic-catalog/${fixture.name}: ${run.error ?? run.stdout + run.stderr}\n`); }
    }

    // V12: actual Node producer -> offline Core/CLI. The expected statuses and
    // reasons below are handwritten; synthetic provenance is never live evidence.
    const bundleEntry = path.join(tmpDir, "bundle.tsx");
    const bundleImport = path.join(tmpDir, "config.ts");
    const bundleFile = path.join(tmpDir, "R1", "bundle.data");
    fs.writeFileSync(bundleImport, "export const amount = 1;");
    fs.writeFileSync(bundleEntry, `import { Slot, Component } from "resoloop-jsx";
      import { amount } from "./config.js";
      export default <Slot key="root" name="Synthetic"><Component key="holder" type="Synthetic.Holder" fields={{Amount: amount}} /></Slot>;`);
    const built = await buildFile(bundleEntry, { bundle: true, catalog: path.join(catalogDir, "catalog.synthetic.json"),
      buildId: "R1", output: bundleFile, projectRoot: tmpDir });
    if (built.exitCode !== 0) throw new Error(built.errors.join("\n"));
    const bundleOriginal = fs.readFileSync(bundleFile, { encoding: "utf8" });
    function bundleCheck(name: string, file: string, request: string | undefined, exit: number, reason?: string): void {
      checked++;
      const run = spawnSync("dotnet", [dll!, "validate", file, ...(request === undefined ? [] : ["--build-id", request]), "--url", "not-a-url", "--json"],
        { cwd: repoRoot, encoding: "utf8" });
      if (!run.error && run.status === exit && (!reason || (run.stdout + run.stderr).includes(reason))) {
        passed++; process.stdout.write(`contract: PASS bundle/${name}\n`);
      } else { failed++; process.stderr.write(`contract: FAIL bundle/${name}: ${run.error ?? run.stdout + run.stderr}\n`); }
    }
    bundleCheck("success", bundleFile, "R1", 0);
    bundleCheck("stale-request", bundleFile, "R2", 6, "requestMismatch");
    bundleCheck("missing-request", bundleFile, undefined, 2, "OPTION_REQUIRED");
    const ordinaryIr = path.join(tmpDir, "ordinary-ir.json");
    fs.writeFileSync(ordinaryIr, JSON.parse(bundleOriginal).ir.text);
    bundleCheck("ordinary-rejects-request", ordinaryIr, "R1", 2, "INVALID_OPTION");
    const mixedMap = JSON.parse(bundleOriginal); mixedMap.map = { text: "{}", sha256: mixedMap.map.sha256 };
    const mixedMapFile = path.join(tmpDir, "mixed-map.json"); fs.writeFileSync(mixedMapFile, JSON.stringify(mixedMap));
    bundleCheck("mixed-map", mixedMapFile, "R1", 6, "mixed");
    const mixedCatalog = JSON.parse(bundleOriginal); mixedCatalog.catalog.text += " ";
    const mixedCatalogFile = path.join(tmpDir, "mixed-catalog.json"); fs.writeFileSync(mixedCatalogFile, JSON.stringify(mixedCatalog));
    bundleCheck("mixed-catalog", mixedCatalogFile, "R1", 6, "mixed");
    fs.writeFileSync(bundleImport, "export const amount = 2;");
    bundleCheck("changed-import", bundleFile, "R1", 6, "inputChanged");
    fs.writeFileSync(bundleEntry, "export default 123;");
    const failedFile = path.join(tmpDir, "R2", "bundle.data");
    const failedBuild = await buildFile(bundleEntry, { bundle: true, catalog: path.join(catalogDir, "catalog.synthetic.json"),
      buildId: "R2", output: failedFile, projectRoot: tmpDir });
    checked++;
    if (failedBuild.exitCode === 1 && !fs.existsSync(failedFile) && fs.readFileSync(bundleFile, { encoding: "utf8" }) === bundleOriginal) {
      passed++; process.stdout.write("contract: PASS bundle/failed-R2-never-published\n");
    } else { failed++; process.stderr.write("contract: FAIL bundle/failed-R2-never-published\n"); }

    // Independent V11 source oracle crosses the real producer/consumer boundary.
    const sourceBundle = path.join(tmpDir, "source-v11", "bundle.json");
    const sourceBuild = await buildFile(path.join(fixturesDir, "source-v11/main.tsx"), {
      bundle: true, catalog: path.join(catalogDir, "catalog.synthetic.json"), buildId: "V11", output: sourceBundle });
    equal(sourceBuild.exitCode, 0, sourceBuild.errors.join("\n"));
    const sourceOriginal = JSON.parse(fs.readFileSync(sourceBundle));
    const sourceOracle = checkSourceOracle(packageRoot, sourceOriginal);
    for (const command of ["validate", "diff", "plan", "apply"]) {
      checked++;
      const diagnosticFile = path.join(tmpDir, command + ".diagnostics.json");
      const run = spawnSync("dotnet", [dll, command, sourceBundle, "--build-id", "V11", "--diagnostics", diagnosticFile, "--json"], { cwd: repoRoot, encoding: "utf8" });
      try {
        equal(run.status, 6, run.stdout + run.stderr);
        const report = JSON.parse(fs.readFileSync(diagnosticFile)); equal(report.diagnosticVersion, "1");
        for (const row of sourceOracle) {
          const diagnostic = report.diagnostics.find((d: any) => d.code === row.code && d.jsonPath === row.path);
          ok(diagnostic, row.code + " " + row.path); equal(diagnostic.severity, row.severity);
          equal(diagnostic.key, row.key); equal(diagnostic.member, row.member); equal(diagnostic.phase, command); equal(diagnostic.buildId, "V11");
          deepEqual(diagnostic.source, row.expectedSource);
          if (row.related) deepEqual(diagnostic.related, row.expectedRelated);
          equal(diagnostic.completeness.runtime, "unknown");
        }
        const mismatch = report.diagnostics.find((d: any) => d.code === "APPLY_REFERENCE_TYPE_MISMATCH");
        deepEqual(mismatch.expected, { status: "known", value: "Synthetic.Base" });
        deepEqual(mismatch.observed, { status: "known", value: "Synthetic.Other" });
        const legacy = spawnSync("dotnet", [dll, command, sourceBundle, "--build-id", "V11", "--json"], { cwd: repoRoot, encoding: "utf8" });
        equal(run.stdout, legacy.stdout); equal(run.stderr, legacy.stderr);
        passed++; process.stdout.write(`contract: PASS source-v11/${command}\n`);
      } catch (error: any) { failed++; process.stderr.write(`contract: FAIL source-v11/${command}: ${error.message}\n${run.stderr}\n`); }
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
