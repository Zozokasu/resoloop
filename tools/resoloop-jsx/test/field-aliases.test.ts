import { test } from "node:test";
import { deepEqual, equal, throws, ok } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile } from "../src/cli.js";
import { evaluate, BuildError } from "../src/evaluate.js";
import { ref } from "../src/ref.js";
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const fixture = path.join(root, "test/fixtures");
test("Field TSX matches independent handwritten schema-v1 output", async () => {
  const built = await buildFile(path.join(fixture, "field-aliases.tsx"));
  equal(built.exitCode, 0, built.errors.join("\n"));
  const expected = JSON.parse(fs.readFileSync(path.join(fixture, "field-aliases.handwritten.json"), { encoding: "utf8" }));
  expected.authoring = { projectRoot: fixture, source: "field-aliases.tsx", ownershipSource: "root-key" };
  deepEqual(built.document, expected);
  equal(ref.field("alias"), "$field:alias");
});
const field = (name: string, value: any = true, extra = {}) => ({ kind: "field", props: { name, value, ...extra } });
const document = (children: any, extra = {}) => ({ kind: "slot", props: { name: "Root", key: "root", children: {
  kind: "component", props: { type: "Test", key: "c", children, ...extra },
} } });
test("Field duplicate declarations and invalid placement stop build", () => {
  for (const [tree, code] of [
    [document([field("Enabled"), field("Enabled")]), "APPLY_FIELD_DUPLICATE"],
    [document(field("Enabled"), { fields: { Enabled: true } }), "APPLY_FIELD_DUPLICATE"],
    [document(field("Enabled"), { initialFields: { Enabled: false } }), "APPLY_FIELD_DUPLICATE"],
    [document([field("A", true, { key: "same" }), field("B", true, { key: "same" })]), "APPLY_FIELD_ALIAS_DUPLICATE"],
    [document({ kind: "field", props: { name: "Enabled" } }), "APPLY_FIELD_INVALID"],
    [{ kind: "slot", props: { name: "Root", key: "root", children: field("Enabled") } }, "INVALID_CHILD"],
  ] as const) throws(() => evaluate(tree), (e: unknown) => e instanceof BuildError && e.code === code);
});
test("Field aliases and values carry original source ranges; duplicate points at second Field", async () => {
  const temp = fs.mkdtempSync(path.join(root, ".aliases-"));
  try {
    const entry = path.join(temp, "main.tsx");
    const text = 'import { Slot, Component, Field, ref } from "resoloop-jsx";\nexport default <Slot key="root" name="Root"><Component key="c" type="Synthetic.Holder"><Field key="target" name="Target" value={ref.field("missing")} /></Component></Slot>;\n';
    fs.writeFileSync(entry, text);
    const output = path.join(temp, "R1/bundle.json");
    const built = await buildFile(entry, { bundle: true, output, buildId: "R1", catalog: path.join(fixture, "catalog-v11/catalog.synthetic.json") });
    equal(built.exitCode, 0, built.errors.join("\n"));
    const map = JSON.parse(JSON.parse(fs.readFileSync(output, { encoding: "utf8" })).map.text);
    for (const [p, snippet, channel] of [
      ['$.components[0]["fieldAliases"]["target"]', '"target"', "source"],
      ['$.components[0].fields["Target"]', 'ref.field("missing")', "valueSource"],
    ]) {
      const source = map.entries.find((e: any) => e.jsonPath === p)?.[channel];
      equal(source.status, "known"); equal(text.slice(source.range.start.offset, source.range.end.offset), snippet);
    }
    const duplicateText = text.replace('<Field key="target"', '<Field name="Target" value={true} /><Field key="target"');
    fs.writeFileSync(entry, duplicateText);
    const duplicate = await buildFile(entry, { bundle: true, output: path.join(temp, "R2/bundle.json"), buildId: "R2",
      catalog: path.join(fixture, "catalog-v11/catalog.synthetic.json") });
    equal(duplicate.exitCode, 1); ok(duplicate.errors.join("\n").includes("APPLY_FIELD_DUPLICATE"));
    const secondNameOffset = duplicateText.lastIndexOf('name="Target"') + "name=".length;
    const column = secondNameOffset - duplicateText.indexOf("\n");
    ok(duplicate.errors.join("\n").endsWith("; TSX " + entry + ":2:" + column), duplicate.errors.join("\n"));
  } finally { fs.rmSync(temp, { recursive: true, force: true }); }
});

for (const separateComponents of [false, true]) test("Duplicate Field aliases point at the second key: " + (separateComponents ? "across Components" : "one Component"), async () => {
  const temp = fs.mkdtempSync(path.join(root, ".alias-duplicate-"));
  try {
    const entry = path.join(temp, "main.tsx");
    // Independent original TSX: distinct members eliminate duplicate-member
    // diagnosis and leave alias uniqueness as the only failing declaration.
    const boundary = separateComponents ? '</Component><Component key="other" type="Synthetic.Holder">' : "";
    const text = 'import { Slot, Component, Field } from "resoloop-jsx";\n' +
      'export default <Slot key="root" name="Root"><Component key="c" type="Synthetic.Holder">\n' +
      '  <Field key="shared" name="Amount" value={1} />' + boundary + '\n' +
      '  <Field key="shared" name="Target" value={null} />\n' +
      '</Component></Slot>;\n';
    fs.writeFileSync(entry, text);
    const result = await buildFile(entry, { bundle: true, output: path.join(temp, "R1/bundle.json"),
      buildId: "R1", catalog: path.join(fixture, "catalog-v11/catalog.synthetic.json") });
    equal(result.exitCode, 1);
    equal(result.errors.length, 1);
    ok(result.errors[0].startsWith("APPLY_FIELD_ALIAS_DUPLICATE:"), result.errors[0]);
    // Both inputs fix the second key's quoted value at line 4, column 14.
    // This expectation comes from handwritten text, not a generated map.
    ok(result.errors[0].endsWith("; TSX " + entry + ":4:14"), result.errors[0]);
    equal(fs.existsSync(path.join(temp, "R1/bundle.json")), false);
  } finally { fs.rmSync(temp, { recursive: true, force: true }); }
});
