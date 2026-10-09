// Fictional offline fixture only; it is not evidence from a live Resonite instance.
import * as fs from "node:fs";
import * as path from "node:path";
import { spawnSync } from "node:child_process";
import { deepEqual, equal, ok } from "node:assert/strict";
import { buildFile } from "../src/cli.js";

const catalogFixture = "test/fixtures/object-contract/catalog.handwritten.json";
const irFixture = "test/fixtures/object-contract/ir.expected.json";
const sourcePrefix = `import { Slot, Component, defineObject, componentFields, ref } from "resoloop-jsx";
import { catalog } from "../.resoloop/catalog-fields.js";
const Lamp = componentFields("Fixture.Objects.Lamp", catalog);
export const ownership = { key: "object-fixture" };
`;
const successSource = `${sourcePrefix}
const Light = defineObject<{ name: string; intensity: number }>()({
  exports: { enabled: Lamp.ref("light", "Enabled") },
  body: ({ name, intensity }) => <Slot key="root" name={name}><Component key="light" type="Fixture.Objects.Lamp" fields={{ Intensity: intensity }} initialFields={{ Enabled: true }} /></Slot>,
});
const desk = Light.instance("desk-lamp");
const ceiling = Light.instance("ceiling-lamp");
export default <Slot key="scene" name="Objects"><Light handle={desk} name="Desk" intensity={1.5} /><Light handle={ceiling} name="Ceiling" intensity={4} /><Component key="toggle" type="Fixture.Objects.Toggle" fields={{ Target: ref.export(desk, "enabled") }} /></Slot>;
`;

type Run = { status: number | null; stdout: string; stderr: string };
function launch(cli: string, args: string[], cwd: string): Run {
  const result = spawnSync("dotnet", [cli, ...args], { cwd, encoding: "utf8" });
  if (result.error) throw result.error;
  return { status: result.status, stdout: result.stdout, stderr: result.stderr };
}
function issue(run: Run, code: string, jsonPath: string): void {
  const payload = JSON.parse(run.stderr);
  equal(payload.error.code, "APPLY_VALIDATION_FAILED", run.stderr);
  const issues = payload.error.context?.issues;
  ok(Array.isArray(issues), run.stderr);
  ok(issues.some((row: any) => row.code === code && row.path === jsonPath), `${code} ${jsonPath}: ${run.stderr}`);
}

export async function runObjectContract(cliPath: string, packageRoot: string): Promise<{ passed: number; failed: number }> {
  let passed = 0, failed = 0, projectNumber = 0;
  const tempRoot = fs.mkdtempSync(path.join(packageRoot, ".object-contract-"));
  const catalogPath = path.join(tempRoot, "catalog.handwritten.json");
  fs.writeFileSync(catalogPath, fs.readFileSync(path.join(packageRoot, catalogFixture)));
  const expected = JSON.parse(fs.readFileSync(path.join(packageRoot, irFixture), { encoding: "utf8" }).replace(/^\uFEFF/, ""));
  function project(source?: string): { root: string; entry: string; output: string; catalogPath: string; catalogRun: Run } {
    const root = path.join(tempRoot, `project-${++projectNumber}`);
    fs.mkdirSync(path.join(root, "content"), { recursive: true });
    fs.writeFileSync(path.join(root, ".resoloop.json"), "{}\n");
    const localCatalog = path.join(root, "catalog.handwritten.json");
    fs.writeFileSync(localCatalog, fs.readFileSync(catalogPath));
    const catalogRun = launch(cliPath, ["catalog", "types", localCatalog, "--json"], root);
    const entry = path.join(root, "content", "main.tsx");
    if (source !== undefined) fs.writeFileSync(entry, source);
    return { root, entry, output: path.join(root, "ir.json"), catalogPath: localCatalog, catalogRun };
  }
  async function built(source: string) {
    const p = project(source);
    return { p, result: await buildFile(p.entry, { projectRoot: p.root, output: p.output }) };
  }
  async function check(name: string, action: () => Promise<void> | void): Promise<void> {
    try { await action(); passed++; process.stdout.write(`object-contract: PASS ${name}\n`); }
    catch (error: any) { failed++; process.stderr.write(`object-contract: FAIL ${name}: ${error?.message ?? String(error)}\n`); }
  }
  try {
    process.stdout.write("object-contract: fictional offline fixture; not live Resonite evidence\n");
    const valid = await built(successSource);
    await check("generated-pair", () => {
      equal(valid.p.catalogRun.status, 0, valid.p.catalogRun.stderr);
      const dts = fs.readFileSync(path.join(valid.p.root, ".resoloop", "catalog-types.d.ts"), { encoding: "utf8" });
      const runtime = fs.readFileSync(path.join(valid.p.root, ".resoloop", "catalog-fields.ts"), { encoding: "utf8" });
      ok(dts.startsWith("// resoloop-catalog-types: ") && dts.includes('"generatorVersion":"3"'), dts.slice(0, 220));
      ok(runtime.startsWith("// resoloop-catalog-fields: ") && runtime.includes('"generatorVersion":"3"'), runtime.slice(0, 220));
    });
    await check("built-ir-handwritten-oracle", () => {
      equal(valid.result.exitCode, 0, valid.result.errors.join("\n"));
      const actual = { ...valid.result.document } as any; delete actual.authoring;
      deepEqual(actual, expected);
    });
    await check("csharp-validates-built-ir", () => {
      const run = launch(cliPath, ["validate", valid.p.output, "--catalog", valid.p.catalogPath, "--json"], valid.p.root);
      equal(run.status, 0, run.stdout + run.stderr);
    });
    await check("raw-member-outside-object-compatible", async () => {
      const src = `import { Slot, Component, ref } from "resoloop-jsx"; export const ownership={key:"raw"}; export default <Slot key="scene" name="Raw"><Component key="lamp" type="Fixture.Objects.Lamp" fields={{Intensity:1.5}} /><Component key="toggle" type="Fixture.Objects.Toggle" fields={{Target:ref.member("lamp","Enabled")}} /></Slot>;`;
      const { p, result } = await built(src); equal(p.catalogRun.status, 0, p.catalogRun.stderr); equal(result.exitCode, 0, result.errors.join("\n"));
      const run = launch(cliPath, ["validate", p.output, "--catalog", p.catalogPath, "--json"], p.root);
      equal(run.status, 0, run.stdout + run.stderr);
    });
    await check("typed-reference-mismatch-ts-exit-2", async () => {
      const src = `${sourcePrefix}const Light=defineObject<{name:string}>()({exports:{enabled:Lamp.ref("light","Enabled")},body:({name})=><Slot key="root" name={name}><Component key="light" type="Fixture.Objects.Lamp" /></Slot>}); const desk=Light.instance("desk"); export default <Slot key="scene" name="Typed"><Light handle={desk} name="Desk"/><Component key="toggle" type="Fixture.Objects.Toggle" fields={{Target:Lamp.ref("light","Intensity")}}/></Slot>;`;
      const { p, result } = await built(src);
      equal(p.catalogRun.status, 0, p.catalogRun.stderr); equal(result.exitCode, 2, result.errors.join("\n"));
      const diagnostic = result.errors.join("\n");
      ok(diagnostic.includes("TS2322") && diagnostic.includes("Target") && diagnostic.includes("FieldRef<number"), diagnostic);
      ok(!diagnostic.includes("TS2307") && !diagnostic.includes("never"), diagnostic);
    });
    await check("unchecked-cross-object-type-mismatch", async () => {
      const src = `${sourcePrefix}const Light=defineObject<{name:string}>()({exports:{intensity:Lamp.ref("light","Intensity")},body:({name})=><Slot key="root" name={name}><Component key="light" type="Fixture.Objects.Lamp" fields={{Intensity:2}} /></Slot>}); const desk=Light.instance("desk"); export default <Slot key="scene" name="Typed"><Light handle={desk} name="Desk"/><Component key="toggle" type="Fixture.Objects.Toggle" fields={{Target:ref.export(desk,"intensity") as any}}/></Slot>;`;
      const { p, result } = await built(src); equal(p.catalogRun.status, 0, p.catalogRun.stderr); equal(result.exitCode, 1, result.errors.join("\n")); ok(result.errors.join("\n").includes("APPLY_OBJECT_TYPE_MISMATCH"), result.errors.join("\n"));
    });
    await check("private-object-member-rejected", async () => {
      const src = `${sourcePrefix}const Light=defineObject<{name:string}>()({exports:{enabled:Lamp.ref("light","Enabled")},body:({name})=><Slot key="root" name={name}><Component key="light" type="Fixture.Objects.Lamp" initialFields={{ Enabled: true }} /></Slot>}); const desk=Light.instance("desk"); export default <Slot key="scene" name="Private"><Light handle={desk} name="Desk"/><Component key="toggle" type="Fixture.Objects.Toggle" fields={{Target:ref.member("desk::light","Enabled") as any}}/></Slot>;`;
      const { p, result } = await built(src); equal(p.catalogRun.status, 0, p.catalogRun.stderr); equal(result.exitCode, 1, result.errors.join("\n")); ok(result.errors.join("\n").includes("APPLY_OBJECT_PRIVATE_REFERENCE"), result.errors.join("\n"));
    });
    await check("unknown-object-export-rejected", async () => {
      const src = `${sourcePrefix}const Light=defineObject<{name:string}>()({exports:{enabled:Lamp.ref("light","Enabled")},body:({name})=><Slot key="root" name={name}><Component key="light" type="Fixture.Objects.Lamp" initialFields={{ Enabled: true }} /></Slot>}); const desk=Light.instance("desk"); export default <Slot key="scene" name="Unknown"><Light handle={desk} name="Desk"/><Component key="toggle" type="Fixture.Objects.Toggle" fields={{Target:ref.export(desk,"missing" as any)}}/></Slot>;`;
      const { p, result } = await built(src); equal(p.catalogRun.status, 0, p.catalogRun.stderr); equal(result.exitCode, 1, result.errors.join("\n")); ok(result.errors.join("\n").includes("APPLY_OBJECT_EXPORT_UNKNOWN"), result.errors.join("\n"));
    });
    const fixtureDoc = () => JSON.parse(JSON.stringify(expected));
    await check("csharp-int32-conversion-authoritative", () => {
      const doc = fixtureDoc(); doc.children[0].components[0].fields.Count = 1.5;
      const file = path.join(tempRoot, "conversion-ir.json"); fs.writeFileSync(file, JSON.stringify(doc));
      const run = launch(cliPath, ["validate", file, "--catalog", catalogPath, "--json"], tempRoot);
      equal(run.status, 6, run.stderr); issue(run, "VALUE_CONVERSION_FAILED", "$.children[0].components[0].fields.Count");
    });
    await check("csharp-reference-assignability-authoritative", () => {
      const doc = fixtureDoc(); doc.components[0].fields.Target = "$member:desk-lamp::light.Intensity";
      const file = path.join(tempRoot, "reference-ir.json"); fs.writeFileSync(file, JSON.stringify(doc));
      const run = launch(cliPath, ["validate", file, "--catalog", catalogPath, "--json"], tempRoot);
      equal(run.status, 6, run.stderr); issue(run, "APPLY_REFERENCE_TYPE_MISMATCH", "$.components[0].fields.Target");
    });
    await check("csharp-missing-catalog-authoritative", () => {
      const file = path.join(tempRoot, "missing-catalog-ir.json"); fs.writeFileSync(file, JSON.stringify(fixtureDoc()));
      const run = launch(cliPath, ["validate", file, "--catalog", path.join(tempRoot, "missing-file.json"), "--json"], tempRoot);
      equal(run.status, 6, run.stderr); issue(run, "APPLY_CATALOG_UNAVAILABLE", "$.components[0].type");
    });
  } finally { fs.rmSync(tempRoot, { recursive: true, force: true }); }
  return { passed, failed };
}
