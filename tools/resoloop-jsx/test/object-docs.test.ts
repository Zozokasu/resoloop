import { test } from "node:test";
import { deepEqual, equal, match } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { createHash } from "node:crypto";
import { buildFile } from "../src/cli.js";
import type { CatalogRuntimeTable } from "../src/catalog-runtime.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const docs = ["../../README.md", "../../README_JA.md", "../../README-DETAILS.md", "README.md"];
const runtimeHash = "handwritten-object-docs-catalog";
const boolField = { kind: "field", catalogKind: "field", confirmed: true, memberType: "BooleanField", valueType: "bool", targetType: null, scalar: "boolean", fieldInterface: "[FrooxEngine]FrooxEngine.IField<bool>", fieldTarget: null } as const;
const singleField = { kind: "field", catalogKind: "field", confirmed: true, memberType: "SingleField", valueType: "float", targetType: null, scalar: "single", fieldInterface: "[FrooxEngine]FrooxEngine.IField<float>", fieldTarget: null } as const;
const targetReference = { kind: "reference", catalogKind: "field-reference", confirmed: true, memberType: "BooleanField", valueType: null, targetType: "[FrooxEngine]FrooxEngine.BooleanFieldAlias", scalar: null, fieldInterface: null, fieldTarget: { representation: "boolean", valueType: "bool", targetType: "[FrooxEngine]FrooxEngine.IField<bool>" } } as const;
const table: CatalogRuntimeTable = {
  formatVersion: "1", generatorVersion: "3", contentHash: runtimeHash,
  types: {
    "Fixture.Objects.Lamp": { canonicalType: "Fixture.Objects.Lamp", confirmed: true, membersComplete: true, members: { Enabled: boolField, Intensity: singleField } },
    "Fixture.Objects.Toggle": { canonicalType: "Fixture.Objects.Toggle", confirmed: true, membersComplete: true, members: { Target: targetReference } },
  },
};
const registry = `import "resoloop-jsx";
export {};
declare module "resoloop-jsx" {
  interface CatalogComponentRegistry {
    "Fixture.Objects.Lamp": { membersComplete: true; members: { Enabled: boolean; Intensity: number }; memberMetadata: { Enabled: { kind: "field"; confirmed: true; scalar: "boolean"; fieldInterface: string }; Intensity: { kind: "field"; confirmed: true; scalar: "single"; fieldInterface: string } } };
    "Fixture.Objects.Toggle": { membersComplete: true; members: { Target: import("resoloop-jsx").FieldRef<boolean, "boolean"> | import("resoloop-jsx").UncheckedReference | null }; memberMetadata: { Target: { kind: "reference"; confirmed: true; scalar: null; fieldInterface: null } } };
  }
}`;
const sha = (text: string) => createHash("sha256").update(text, "utf8").digest("hex");

function exampleFrom(file: string): string {
  const text = documentText(file);
  const marker = "<!-- object-example -->";
  equal(text.split(marker).length - 1, 1, `${file} must have exactly one ${marker}`);
  const after = text.slice(text.indexOf(marker) + marker.length);
  const block = /^\n(?:```|~~~)tsx\n([\s\S]*?)\n(?:```|~~~)/.exec(after);
  equal(block !== null, true, `${file} must have a tsx code block right after the marker`);
  const body = block![1];
  equal(body.includes("roadmap5-example"), false, `${file} example must not contain roadmap5-example`);
  return body + "\n";
}

function documentText(file: string): string {
  return fs.readFileSync(path.resolve(packageRoot, file), { encoding: "utf8" }).replace(/\r\n/g, "\n");
}
const requiredDocumentation = [
  "defineObject<P>()", "Def.instance(id)", "ref.export", "componentFields(type, catalog)",
  ".resoloop/catalog-types.d.ts", ".resoloop/catalog-fields.ts", "format 1", "generator 3",
  "schema", "ownership", "version 3", "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$",
  "TS2307", "TS6059", "APPLY_CATALOG_TYPES_HASH_MISMATCH", "APPLY_CATALOG_UNAVAILABLE",
  "exit 1", "exit 2", "exit 6", "APPLY_OBJECT_INVALID", "APPLY_OBJECT_DUPLICATE",
  "APPLY_OBJECT_EXPORT_INVALID", "APPLY_OBJECT_EXPORT_UNKNOWN", "APPLY_OBJECT_TYPE_MISMATCH",
  "APPLY_OBJECT_PRIVATE_REFERENCE", "APPLY_FIELD_TYPE_MISMATCH", "VALUE_CONVERSION_FAILED",
  "APPLY_REFERENCE_TYPE_MISMATCH", "__proto__", "FieldRef<T, R>", "UncheckedReference",
  "string | null", "ref.*", "projectRoot", "--project-root", "authoring.source",
  "../project/content/main.tsx", "--catalog", "tsc", "identity", "privacy", "raw internal reference",
  "Object boundary", "experimental", "Toggle", "Reflection", "initialFields", "state",
  "ESM cache", "watch mode", "runtime reference ID",
] as const;

function assertDocumentationTerms(file: string): void {
  const text = documentText(file);
  for (const term of requiredDocumentation) equal(text.toLowerCase().includes(term.toLowerCase()), true, `${file} is missing ${term}`);
  equal(text.includes("has not been verified against a live Resonite session") || text.includes("\u5b9f\u6a5f\u306eResonite\u3067\u306f\u672a\u691c\u8a3c"), true,
    `${file} must say the Object API is not verified against a live Resonite session`);
  equal(/\bunit \d+\b|\bL[0-8]\b|roadmap5|evidence levels?/i.test(text), false, `${file} must not contain internal development terms`);
}
function workspace(source: string) {
  const root = fs.mkdtempSync(path.join(packageRoot, ".object-docs-"));
  const catalogDir = path.join(root, ".resoloop");
  const contentDir = path.join(root, "content");
  fs.mkdirSync(catalogDir); fs.mkdirSync(contentDir);
  const runtime = `// resoloop-catalog-fields: {"formatVersion":"1","contentHash":"${runtimeHash}","generatorVersion":"3"}\nimport type { CatalogRuntimeTable } from "resoloop-jsx";\nexport const catalog = ${JSON.stringify(table)} as const satisfies CatalogRuntimeTable;\n`;
  const declaration = `// resoloop-catalog-types: {"formatVersion":"1","contentHash":"${runtimeHash}","generatorVersion":"3","runtimeFile":"catalog-fields.ts","runtimeSha256":"${sha(runtime)}"}\n${registry}\n`;
  fs.writeFileSync(path.join(catalogDir, "catalog-fields.ts"), runtime);
  fs.writeFileSync(path.join(catalogDir, "catalog-types.d.ts"), declaration);
  const entry = path.join(contentDir, "main.tsx");
  fs.writeFileSync(entry, source);
  return { root, entry, cleanup: () => fs.rmSync(root, { recursive: true, force: true }) };
}

test("all four README Object examples compile to the independent handwritten IR", async () => {
  for (const file of docs) assertDocumentationTerms(file);
  const examples = docs.map(file => ({ file, source: exampleFrom(file) }));
  for (const example of examples.slice(1)) equal(example.source, examples[0].source, `${example.file} example differs`);
  const expected = JSON.parse(fs.readFileSync(path.join(packageRoot, "test/fixtures/object-contract/ir.expected.json"), { encoding: "utf8" }).replace(/^﻿/, ""));
  for (const { file, source } of examples) {
    match(source, /Fixture\.Objects\.Lamp[\s\S]*Fixture\.Objects\.Toggle/);
    const w = workspace(source);
    try {
      const result = await buildFile(w.entry, { projectRoot: w.root });
      equal(result.exitCode, 0, `${file}: ${result.errors.join("\n")}`);
      const { authoring: _authoring, ...actual } = result.document!;
      deepEqual(actual, expected, `${file} output differs from the handwritten IR`);
    } finally { w.cleanup(); }
  }
});