import { test } from "node:test";
import { deepEqual, equal } from "node:assert/strict";
import fs from "node:fs";
import * as path from "node:path";
import { createHash } from "node:crypto";
import { syncBuiltinESMExports } from "node:module";
import * as ts from "typescript";
import { catalogRuntimeTables } from "../src/catalog-declarations.js";
import { buildErrorLocation } from "../src/source-map.js";

const hash = (text: string): string => createHash("sha256").update(text.replace(/\r\n/g, "\n"), "utf8").digest("hex");
const empty = '{"formatVersion":"1","generatorVersion":"3","contentHash":"handwritten-hash","types":{}}';

function virtualPair(root: string, contentHash = "handwritten-hash") {
  const runtimeFile = path.join(root, "catalog-fields.ts");
  const runtime = `// resoloop-catalog-fields: {"formatVersion":"1","contentHash":"${contentHash}","generatorVersion":"3"}\nimport type { CatalogRuntimeTable } from "resoloop-jsx";\nexport const catalog = ${empty.replace("handwritten-hash", contentHash)} as const satisfies CatalogRuntimeTable;\n`;
  const declarationFile = path.join(root, "catalog-types.d.ts");
  const declaration = `// resoloop-catalog-types: {"formatVersion":"1","contentHash":"${contentHash}","generatorVersion":"3","runtimeFile":"catalog-fields.ts","runtimeSha256":"${hash(runtime)}"}\nexport {};\n`;
  return { runtimeFile, runtime, declarationFile, declaration };
}

function withVirtualSources<T>(entries: Array<{ file: string; text: string }>, caseSensitive: boolean, action: (program: ts.Program) => T): T {
  const contents = new Map<string, string>();
  const sources = entries.map(({ file, text }) => {
    const absolute = path.resolve(file);
    contents.set(absolute, text);
    return ts.createSourceFile(absolute, text, ts.ScriptTarget.ES2022, true);
  });
  const originalReadFile = fs.readFileSync;
  const originalCaseDescriptor = Object.getOwnPropertyDescriptor(ts.sys, "useCaseSensitiveFileNames");
  const mutableFs = fs as unknown as { readFileSync: typeof fs.readFileSync };
  try {
    mutableFs.readFileSync = ((file: any, ...args: unknown[]) => {
      const content = contents.get(path.resolve(String(file)));
      if (content === undefined) return (originalReadFile as (...values: any[]) => any).call(fs, file, ...args);
      return (globalThis as any).Buffer.from(content, "utf8");
    }) as typeof fs.readFileSync;
    syncBuiltinESMExports();
    Object.defineProperty(ts.sys, "useCaseSensitiveFileNames", { configurable: true, writable: true, value: caseSensitive });
    return action({ getSourceFiles: () => sources } as unknown as ts.Program);
  } finally {
    try {
      mutableFs.readFileSync = originalReadFile;
      syncBuiltinESMExports();
    } finally {
      if (originalCaseDescriptor) Object.defineProperty(ts.sys, "useCaseSensitiveFileNames", originalCaseDescriptor);
      else delete (ts.sys as any).useCaseSensitiveFileNames;
    }
  }
}

function rejects(entries: Array<{ file: string; text: string }>, file: string) {
  try {
    withVirtualSources(entries, true, program => catalogRuntimeTables(program));
    throw new Error("expected catalog artifact mismatch");
  } catch (error) {
    if (error instanceof Error && error.message === "expected catalog artifact mismatch") throw error;
    equal((error as any).code, "APPLY_CATALOG_TYPES_HASH_MISMATCH");
    equal(buildErrorLocation(error as object).replace(/\\/g, "/"), `${path.resolve(file)}:1:1`.replace(/\\/g, "/"));
  }
}

test("case-sensitive path identity rejects a declaration from a differently cased directory", () => {
  const pair = virtualPair("/p/a");
  rejects([
    { file: pair.runtimeFile, text: pair.runtime },
    { file: path.join("/p/A", "catalog-types.d.ts"), text: pair.declaration },
  ], pair.runtimeFile);
});

test("case-sensitive path identity rejects a differently cased runtime leaf", () => {
  const pair = virtualPair("/p/b");
  const declaration = pair.declaration.replace('"runtimeFile":"catalog-fields.ts"', '"runtimeFile":"Catalog-fields.ts"');
  rejects([{ file: pair.runtimeFile, text: pair.runtime }, { file: pair.declarationFile, text: declaration }], pair.runtimeFile);
});

test("case-sensitive path identity verifies two pairs in distinct directories independently", () => {
  const lower = virtualPair("/p/a", "lower-hash");
  const upper = virtualPair("/p/A", "upper-hash");
  const tables = withVirtualSources([
    { file: lower.runtimeFile, text: lower.runtime }, { file: lower.declarationFile, text: lower.declaration },
    { file: upper.runtimeFile, text: upper.runtime }, { file: upper.declarationFile, text: upper.declaration },
  ], true, program => catalogRuntimeTables(program));
  equal(tables.length, 2);
  deepEqual(tables.map(table => table.contentHash), ["lower-hash", "upper-hash"]);
});

test("case-sensitive path identity retains stale declaration when it precedes the good declaration", () => {
  const pair = virtualPair("/p/a");
  const staleFile = path.join("/p/a", "Catalog-types.d.ts");
  const stale = pair.declaration.replace(hash(pair.runtime), "a".repeat(64));
  rejects([
    { file: staleFile, text: stale },
    { file: pair.declarationFile, text: pair.declaration },
    { file: pair.runtimeFile, text: pair.runtime },
  ], pair.runtimeFile);
});
