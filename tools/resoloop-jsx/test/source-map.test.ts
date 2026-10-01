import { test } from "node:test";
import { equal, ok, deepEqual } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile } from "../src/cli.js";
import { checkSourceOracle } from "./source-oracle.js";
const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

test("V11_SourceOracle: function props scope fragment map conditional fields and initialFields", async () => {
  const root = fs.mkdtempSync(path.join(packageRoot, ".s2-5-map-"));
  try {
    const output = path.join(root, "R1/bundle.json");
    const result = await buildFile(path.join(packageRoot, "test/fixtures/source-v11/main.tsx"), {
      bundle: true, catalog: path.join(packageRoot, "test/fixtures/catalog-v11/catalog.synthetic.json"), buildId: "R1", output });
    equal(result.exitCode, 0, result.errors.join("\n"));
    checkSourceOracle(packageRoot, JSON.parse(fs.readFileSync(output)));
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test("legacy build stays metadata free and bundle IR matches it", async () => {
  const root = fs.mkdtempSync(path.join(packageRoot, ".s2-5-legacy-"));
  try {
    const entry = path.join(packageRoot, "test/fixtures/source-v11/main.tsx");
    const legacy = await buildFile(entry);
    const output = path.join(root, "R1/bundle.json");
    const built = await buildFile(entry, { bundle: true, catalog: path.join(packageRoot, "test/fixtures/catalog-v11/catalog.synthetic.json"), buildId: "R1", output });
    equal(legacy.exitCode, 0); equal(built.exitCode, 0, built.errors.join("\n"));
    deepEqual(JSON.parse(fs.readFileSync(output)).ir.text, JSON.stringify(legacy.document, null, 2) + "\n");
    ok(!JSON.stringify(legacy.document).includes('"range"'));
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});


test("direct fields props forward while copied props and map callback values remain unknown", async () => {
  const root = fs.mkdtempSync(path.join(packageRoot, ".s2-5-forward-"));
  try {
    const entry = path.join(root, "main.tsx");
    const output = path.join(root, "R1/bundle.json");
    fs.writeFileSync(entry, `import { Slot, Component, ref } from "resoloop-jsx";
function Pass(props: { fields: { Target: string }; target: string }) {
  const copy = { ...props };
  return <>
    <Component key="direct" type="Synthetic.Holder" fields={props.fields} />
    <Component key="copy" type="Synthetic.Holder" fields={{ Target: copy.target }} />
    {[{ target: "$component:missing-map" }].map(item => <Component key="map" type="Synthetic.Holder" fields={{ Target: item.target }} />)}
  </>;
}
export default <Slot key="root" name="Forward"><Pass fields={{ Target: ref.component("base") }} target={ref.component("missing-caller")} /></Slot>;
`);
    const result = await buildFile(entry, { bundle: true, catalog: path.join(packageRoot, "test/fixtures/catalog-v11/catalog.synthetic.json"), buildId: "R1", output });
    equal(result.exitCode, 0, result.errors.join("\n"));
    const entries = JSON.parse(JSON.parse(fs.readFileSync(output)).map.text).entries;
    const direct = entries.find((e: any) => e.jsonPath === '$.components[0].fields["Target"]');
    equal(direct.source.status, "known"); equal(direct.source.range.start.line, 10);
    equal(direct.valueSource.status, "known"); equal(direct.valueSource.range.start.line, 10);
    for (const index of [1, 2]) {
      const e = entries.find((e: any) => e.jsonPath === '$.components[' + index + '].fields["Target"]');
      deepEqual(e.valueSource, { status: "unknown" }); ok(e.related.length > 0);
    }
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});


test("origin transformer preserves await and attribute evaluation order", async () => {
  const root = fs.mkdtempSync(path.join(packageRoot, ".s2-5-await-"));
  try {
    const entry = path.join(root, "main.tsx"), output = path.join(root, "R1/bundle.json");
    fs.writeFileSync(entry, `import { Slot, Component } from "resoloop-jsx";
let order = 0;
export default <Slot key="root" name="Async"><Component key="holder" type="Synthetic.Holder" fields={{ Amount: await Promise.resolve(++order), Optional: ++order }} /></Slot>;
`);
    const legacy = await buildFile(entry);
    const bundle = await buildFile(entry, { bundle: true, catalog: path.join(packageRoot, "test/fixtures/catalog-v11/catalog.synthetic.json"), buildId: "R1", output });
    equal(legacy.exitCode, 0, legacy.errors.join("\n")); equal(bundle.exitCode, 0, bundle.errors.join("\n"));
    deepEqual(legacy.document, bundle.document);
    deepEqual(bundle.document?.components?.[0].fields, { Amount: 1, Optional: 2 });
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});


test("nested package boundary preserves legacy TypeScript module-resolution errors", async () => {
  const root = fs.mkdtempSync(path.join(packageRoot, ".s2-5-commonjs-"));
  try {
    fs.writeFileSync(path.join(root, "package.json"), '{"type":"commonjs"}');
    const entry = path.join(root, "main.tsx"), output = path.join(root, "R1/bundle.json");
    fs.writeFileSync(entry, 'import { Slot, Component } from "resoloop-jsx"; export default <Slot key="root" name="CJS"><Component key="holder" type="Synthetic.Holder" fields={{ Amount: 1 }} /></Slot>;');
    const legacy = await buildFile(entry);
    const bundle = await buildFile(entry, { bundle: true, catalog: path.join(packageRoot, "test/fixtures/catalog-v11/catalog.synthetic.json"), buildId: "R1", output });
    equal(legacy.exitCode, 2); equal(bundle.exitCode, 2);
    deepEqual(legacy.errors, bundle.errors); ok(!fs.existsSync(output));
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});


test("Scope instanceKey build error uses original attribute range only in bundle mode", async () => {
  const root = fs.mkdtempSync(path.join(packageRoot, ".s2-5-scope-error-"));
  try {
    const entry = path.join(root, "main.tsx"), output = path.join(root, "R1/bundle.json");
    fs.writeFileSync(entry, `import { Slot, Scope } from "resoloop-jsx";
export default <Slot key="root" name="Scope">
  <Scope instanceKey="bad:segment"><Slot key="child" name="Child" /></Scope>
</Slot>;
`);
    const legacy = await buildFile(entry);
    const bundle = await buildFile(entry, { bundle: true, catalog: path.join(packageRoot, "test/fixtures/catalog-v11/catalog.synthetic.json"), buildId: "R1", output });
    equal(legacy.exitCode, 1); equal(bundle.exitCode, 1);
    ok(legacy.errors[0].startsWith("APPLY_SCOPE_INVALID:")); ok(!legacy.errors[0].includes("TSX"));
    ok(bundle.errors[0].endsWith("main.tsx:3:22"), bundle.errors[0]); ok(!fs.existsSync(output));
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
