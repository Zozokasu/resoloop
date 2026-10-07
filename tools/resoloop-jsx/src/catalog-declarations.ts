import * as fs from "node:fs";
import * as path from "node:path";
import { BuildError } from "./evaluate.js";

/** Load only the optional declaration and compare its identity, not catalog semantics. */
export function catalogDeclaration(projectRoot: string, catalogText?: string): string | undefined {
  const file = path.join(projectRoot, ".resoloop", "catalog-types.d.ts");
  if (!fs.existsSync(file)) return undefined;
  if (catalogText !== undefined) {
    verifyCatalogDeclaration(file, fs.readFileSync(file, { encoding: "utf8" }), catalogText, true);
  }
  return file;
}

/** Also recognize generated declarations brought in by a reference/import at a custom path. */
export function verifyCatalogDeclaration(file: string, text: string, catalogText: string, required = false): void {
  const firstLine = text.replace(/^\uFEFF/, "").split(/\r?\n/, 1)[0];
  const prefix = "// resoloop-catalog-types: ";
  if (!required && !firstLine.startsWith(prefix)) return;
  let declarationHash: unknown;
  let declarationFormat: unknown;
  let declarationGenerator: unknown;
  let catalogHash: unknown;
  try {
    if (firstLine.startsWith(prefix)) {
      const header = JSON.parse(firstLine.slice(prefix.length));
      declarationHash = header.contentHash;
      declarationFormat = header.formatVersion;
      declarationGenerator = header.generatorVersion;
    }
    catalogHash = JSON.parse(catalogText.replace(/^\uFEFF/, "")).contentHash;
  } catch {
    // A malformed identity cannot match; leave catalog validation to C#.
  }
  if (declarationFormat !== "1" || declarationGenerator !== "2" ||
    typeof declarationHash !== "string" || typeof catalogHash !== "string" || declarationHash !== catalogHash)
    throw new BuildError("APPLY_CATALOG_TYPES_HASH_MISMATCH", `Catalog declarations at '${file}' do not match the current declaration format/generator or --catalog contentHash; regenerate catalog types.`);
}
