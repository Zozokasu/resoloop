import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { createHash } from "node:crypto";
import type { CatalogMemberRuntime, CatalogRuntimeTable } from "../src/catalog-runtime.js";

const boolField: CatalogMemberRuntime = {
  kind: "field", catalogKind: "field", confirmed: true, memberType: "Boolean", valueType: "bool",
  targetType: null, scalar: "boolean", fieldInterface: "[FrooxEngine]FrooxEngine.IField<bool>", fieldTarget: null,
};
const intField: CatalogMemberRuntime = {
  kind: "field", catalogKind: "field", confirmed: true, memberType: "Int32", valueType: "int",
  targetType: null, scalar: "int32", fieldInterface: "[FrooxEngine]FrooxEngine.IField<int>", fieldTarget: null,
};
const unknownMember: CatalogMemberRuntime = {
  kind: "other", catalogKind: "unknown", confirmed: false, memberType: null, valueType: null,
  targetType: null, scalar: null, fieldInterface: null, fieldTarget: null,
};
const lampType: CatalogRuntimeTable["types"][string] = {
  canonicalType: "Fixture.Objects.Lamp", confirmed: true, membersComplete: false,
  members: { Enabled: boolField, Count: intField, Uncertain: unknownMember },
};

/** Handwritten offline Fixture.Objects.Lamp registry/runtime pair; never generated from build output. */
export function variablesRuntimeTable(): CatalogRuntimeTable {
  return { formatVersion: "1", generatorVersion: "3", contentHash: "variables-handwritten-runtime",
    types: { "Fixture.Objects.Lamp": lampType } };
}

const registry = `type LampRegistry = { membersComplete: false; members: { Enabled: boolean; Count: number; Uncertain: unknown };
  memberMetadata: { Enabled: { kind: "field"; confirmed: true; scalar: "boolean"; fieldInterface: string };
    Count: { kind: "field"; confirmed: true; scalar: "int32"; fieldInterface: string };
    Uncertain: { kind: "other"; confirmed: false; scalar: null; fieldInterface: null } } };
declare module "resoloop-jsx" { interface CatalogComponentRegistry { "Fixture.Objects.Lamp": LampRegistry } }`;

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

export function variablesWorkspace(source: string) {
  const root = fs.mkdtempSync(path.join(packageRoot, ".variables-build-"));
  const metadata = path.join(root, ".resoloop");
  fs.mkdirSync(metadata);
  const runtime = "// resoloop-catalog-fields: {\"formatVersion\":\"1\",\"contentHash\":\"variables-handwritten-runtime\",\"generatorVersion\":\"3\"}\n" +
    'import type { CatalogRuntimeTable } from "resoloop-jsx";\n' +
    "export const catalog = " + JSON.stringify(variablesRuntimeTable()) + " as const satisfies CatalogRuntimeTable;\n";
  const sha256 = createHash("sha256").update(runtime, "utf8").digest("hex");
  fs.writeFileSync(path.join(metadata, "catalog-fields.ts"), runtime, { encoding: "utf8" });
  fs.writeFileSync(path.join(metadata, "catalog-types.d.ts"),
    `// resoloop-catalog-types: {"formatVersion":"1","contentHash":"variables-handwritten-runtime","generatorVersion":"3","runtimeFile":"catalog-fields.ts","runtimeSha256":"${sha256}"}\n${registry}\nexport {};\n`,
    { encoding: "utf8" });
  const entry = path.join(root, "main.tsx");
  const entryText = source.replace(/\r\n/g, "\n");
  fs.writeFileSync(entry, entryText, { encoding: "utf8" });
  const catalog = path.join(root, "catalog.json");
  fs.writeFileSync(catalog, JSON.stringify({ contentHash: "variables-handwritten-runtime" }) + "\n", { encoding: "utf8" });
  return { root, entry, output: path.join(root, "out.json"), catalog,
    cleanup: () => fs.rmSync(root, { recursive: true, force: true }) };
}
