import * as fs from "node:fs";
import * as path from "node:path";
import { createHash } from "node:crypto";
import * as ts from "typescript";
import { BuildError } from "./evaluate.js";
import { markBuildErrorOrigin, type Origin } from "./source-map.js";
import { verifyRuntimeTable, type CatalogRuntimeTable } from "./catalog-runtime.js";

const declarationPrefix = "// resoloop-catalog-types: ";
const runtimePrefix = "// resoloop-catalog-fields: ";
const mismatchText = "Catalog declarations/runtime at '{file}' do not match format/generator/contentHash/runtimeSha256; regenerate catalog types.";
type Header = { formatVersion: "1"; generatorVersion: "3"; contentHash: string; runtimeFile?: string; runtimeSha256?: string };
type Bytes = { readonly length: number; readonly [index: number]: number; toString(): string };
declare const TextEncoder: new () => { encode(text: string): Uint8Array };

function hasHeaderCandidate(text: string, prefix: string): boolean {
  const lines = text.split(/\r?\n/, 2).map(line => line.replace(/^\uFEFF/, ""));
  const candidate = prefix.slice(0, -1);
  return lines[0].startsWith(candidate) ||
    lines[0].startsWith("//") && lines[0].includes(candidate.slice(3)) && (lines[1] ?? "").startsWith(candidate);
}

function mismatch(file: string, bytes?: Bytes): BuildError {
  const error = new BuildError("APPLY_CATALOG_TYPES_HASH_MISMATCH", mismatchText.replace("{file}", file));
  const sha256 = bytes ? createHash("sha256").update(bytes).digest("hex") : createHash("sha256").update("", "utf8").digest("hex");
  const origin: Origin = { source: { status: "known", file, sha256,
    range: { start: { offset: 0, line: 1, column: 1 }, end: { offset: 0, line: 1, column: 1 } } } };
  markBuildErrorOrigin(error, origin);
  return error;
}

function parseHeader(file: string, text: string, prefix: string, bytes?: Bytes): Header | undefined {
  const lines = text.split(/\r?\n/, 2);
  const first = lines[0].replace(/^\uFEFF/, "");
  if (!first.startsWith(prefix)) {
    if (hasHeaderCandidate(text, prefix)) throw mismatch(file, bytes);
    return undefined;
  }
  let value: any;
  try { value = JSON.parse(first.slice(prefix.length)); } catch { throw mismatch(file, bytes); }
  const expectedKeys = prefix === declarationPrefix && Object.hasOwn(value ?? {}, "runtimeFile")
    ? ["formatVersion", "contentHash", "generatorVersion", "runtimeFile", "runtimeSha256"]
    : ["formatVersion", "contentHash", "generatorVersion"];
  if (!value || typeof value !== "object" || Array.isArray(value) || value.formatVersion !== "1" ||
      value.generatorVersion !== "3" || typeof value.contentHash !== "string" || value.contentHash.length === 0 ||
      JSON.stringify(Object.keys(value)) !== JSON.stringify(expectedKeys))
    throw mismatch(file, bytes);
  if (prefix === declarationPrefix) {
    const hasFile = Object.hasOwn(value, "runtimeFile");
    const hasHash = Object.hasOwn(value, "runtimeSha256");
    if (hasFile !== hasHash || hasFile && (typeof value.runtimeFile !== "string" || !value.runtimeFile.trim() ||
        path.basename(value.runtimeFile) !== value.runtimeFile || /[\\/]/.test(value.runtimeFile) ||
        typeof value.runtimeSha256 !== "string" || !/^[0-9a-f]{64}$/.test(value.runtimeSha256)))
      throw mismatch(file, bytes);
    return { formatVersion: "1", generatorVersion: "3", contentHash: value.contentHash,
      ...(hasFile ? { runtimeFile: value.runtimeFile, runtimeSha256: value.runtimeSha256 } : {}) };
  }
  return { formatVersion: "1", generatorVersion: "3", contentHash: value.contentHash };
}

function catalogHash(file: string, catalogText: string | undefined, bytes?: Bytes): string | undefined {
  if (catalogText === undefined) return undefined;
  try {
    const parsed = JSON.parse(catalogText.replace(/^\uFEFF/, ""));
    if (typeof parsed?.contentHash !== "string" || !parsed.contentHash) throw new Error();
    return parsed.contentHash;
  } catch { throw mismatch(file, bytes); }
}

/** Load the optional declaration and compare its generated identity, not catalog semantics. */
export function catalogDeclaration(projectRoot: string, catalogText?: string): string | undefined {
  const file = path.join(projectRoot, ".resoloop", "catalog-types.d.ts");
  if (!fs.existsSync(file)) return undefined;
  const bytes = fs.readFileSync(file);
  const text = bytes.toString().replace(/^\uFEFF/, "");
  verifyCatalogDeclaration(file, text, catalogText, true, bytes);
  return file;
}

/** Validate a generated declaration header; marker-free handwritten registries remain compatible. */
export function verifyCatalogDeclaration(file: string, text: string, catalogText?: string, required = false, observedBytes?: Bytes): void {
  const bytes = observedBytes ?? new TextEncoder().encode(text);
  const header = parseHeader(file, text, declarationPrefix, bytes);
  if (!header && required) throw mismatch(file, bytes);
  if (!header) return;
  const hash = catalogHash(file, catalogText, bytes);
  if (hash !== undefined && hash !== header.contentHash) throw mismatch(file, bytes);
}

function sourceBytes(source: ts.SourceFile): Bytes {
  let bytes: Bytes;
  try { bytes = fs.readFileSync(source.fileName); } catch { throw mismatch(source.fileName); }
  const decoded = bytes.toString();
  const roundTrip = new TextEncoder().encode(decoded);
  if (decoded.replace(/^\uFEFF/, "") !== source.text || roundTrip.length !== bytes.length ||
      roundTrip.some((byte, index) => byte !== bytes[index])) throw mismatch(source.fileName, bytes);
  return bytes;
}

function typeName(node: ts.TypeNode | undefined): boolean {
  return !!node && ts.isTypeReferenceNode(node) && ts.isIdentifier(node.typeName) &&
    node.typeName.text === "CatalogRuntimeTable" && !node.typeArguments?.length;
}

function unwrap(node: ts.Expression): ts.Expression {
  while (ts.isParenthesizedExpression(node)) node = node.expression;
  return node;
}

function readLiteral(file: string, node: ts.Expression, bytes: Bytes): unknown {
  node = unwrap(node);
  if (ts.isStringLiteral(node)) return node.text;
  if (ts.isNumericLiteral(node)) {
    const value = Number(node.text);
    if (Number.isFinite(value)) return value;
    throw mismatch(file, bytes);
  }
  if (node.kind === ts.SyntaxKind.TrueKeyword) return true;
  if (node.kind === ts.SyntaxKind.FalseKeyword) return false;
  if (node.kind === ts.SyntaxKind.NullKeyword) return null;
  if (ts.isPrefixUnaryExpression(node) && node.operator === ts.SyntaxKind.MinusToken && ts.isNumericLiteral(node.operand)) {
    const value = -Number(node.operand.text);
    if (Number.isFinite(value)) return value;
  }
  if (ts.isArrayLiteralExpression(node)) return node.elements.map(element => {
    if (ts.isSpreadElement(element)) throw mismatch(file, bytes);
    return readLiteral(file, element, bytes);
  });
  if (ts.isObjectLiteralExpression(node)) {
    const result: Record<string, unknown> = Object.create(null);
    for (const property of node.properties) {
      if (!ts.isPropertyAssignment(property)) throw mismatch(file, bytes);
      let key: string;
      if (ts.isIdentifier(property.name) || ts.isStringLiteral(property.name)) key = property.name.text;
      else if (ts.isComputedPropertyName(property.name) && ts.isStringLiteral(property.name.expression) &&
          property.name.expression.text === "__proto__") key = "__proto__";
      else throw mismatch(file, bytes);
      if (key === "__proto__" && !(ts.isComputedPropertyName(property.name) && ts.isStringLiteral(property.name.expression) && property.name.expression.text === "__proto__"))
        throw mismatch(file, bytes);
      if (Object.hasOwn(result, key)) throw mismatch(file, bytes);
      Object.defineProperty(result, key, { value: readLiteral(file, property.initializer, bytes), enumerable: true, configurable: true, writable: true });
    }
    return result;
  }
  throw mismatch(file, bytes);
}

function runtimeTable(source: ts.SourceFile, header: Header, bytes: Bytes): CatalogRuntimeTable {
  const imports = source.statements.filter(ts.isImportDeclaration);
  const exports = source.statements.filter(ts.isExportDeclaration);
  const vars = source.statements.filter(ts.isVariableStatement);
  if (source.statements.length !== 2 || imports.length !== 1 || exports.length !== 0 || vars.length !== 1)
    throw mismatch(source.fileName, bytes);
  const imp = imports[0];
  if (!imp.importClause?.isTypeOnly || imp.importClause.name || imp.importClause.namedBindings === undefined ||
      !ts.isNamedImports(imp.importClause.namedBindings) || imp.importClause.namedBindings.elements.length !== 1 ||
      imp.importClause.namedBindings.elements[0].name.text !== "CatalogRuntimeTable" ||
      imp.importClause.namedBindings.elements[0].propertyName || !ts.isStringLiteral(imp.moduleSpecifier) ||
      imp.moduleSpecifier.text !== "resoloop-jsx") throw mismatch(source.fileName, bytes);
  const statement = vars[0];
  if (!(statement.modifiers ?? []).some(modifier => modifier.kind === ts.SyntaxKind.ExportKeyword) ||
      (statement.modifiers ?? []).some(modifier => modifier.kind !== ts.SyntaxKind.ExportKeyword && modifier.kind !== ts.SyntaxKind.ConstKeyword) ||
      !(statement.declarationList.flags & ts.NodeFlags.Const) || statement.declarationList.declarations.length !== 1)
    throw mismatch(source.fileName, bytes);
  const declaration = statement.declarationList.declarations[0];
  if (!ts.isIdentifier(declaration.name) || declaration.name.text !== "catalog" || declaration.type || !declaration.initializer)
    throw mismatch(source.fileName, bytes);
  let expression = unwrap(declaration.initializer);
  if (!ts.isSatisfiesExpression(expression) || !typeName(expression.type)) throw mismatch(source.fileName, bytes);
  expression = unwrap(expression.expression);
  if (!ts.isAsExpression(expression) || !ts.isTypeReferenceNode(expression.type) ||
      !ts.isIdentifier(expression.type.typeName) || expression.type.typeName.text !== "const" ||
      expression.type.typeArguments?.length)
    throw mismatch(source.fileName, bytes);
  const value = readLiteral(source.fileName, expression.expression, bytes);
  let table: CatalogRuntimeTable;
  try { table = verifyRuntimeTable(value); } catch { throw mismatch(source.fileName, bytes); }
  if (table.contentHash !== header.contentHash) throw mismatch(source.fileName, bytes);
  return table;
}

function pathIdentity(value: string): string {
  const resolved = path.resolve(value).replace(/\\/g, "/");
  return ts.sys.useCaseSensitiveFileNames ? resolved : resolved.toLowerCase();
}

/** Inspect only artifacts already in the compiler Program; never execute or implicitly load them. */
export function catalogRuntimeTables(program: ts.Program, catalogText?: string): CatalogRuntimeTable[] {
  const declarations = new Map<string, { source: ts.SourceFile; header: Header; bytes: Bytes }>();
  const runtimes: Array<{ source: ts.SourceFile; header: Header; bytes: Bytes }> = [];
  for (const source of program.getSourceFiles()) {
    const hasDeclarationMarker = hasHeaderCandidate(source.text, declarationPrefix);
    const hasRuntimeMarker = hasHeaderCandidate(source.text, runtimePrefix);
    if (!hasDeclarationMarker && !hasRuntimeMarker) continue;
    const bytes = sourceBytes(source);
    if (hasDeclarationMarker) {
      const header = parseHeader(source.fileName, source.text, declarationPrefix, bytes);
      if (!header) throw mismatch(source.fileName, bytes);
      const actualCatalogHash = catalogHash(source.fileName, catalogText, bytes);
      if (actualCatalogHash !== undefined && actualCatalogHash !== header.contentHash) throw mismatch(source.fileName, bytes);
      declarations.set(pathIdentity(source.fileName), { source, header, bytes });
    }
    if (hasRuntimeMarker) {
      const header = parseHeader(source.fileName, source.text, runtimePrefix, bytes);
      if (!header) throw mismatch(source.fileName, bytes);
      runtimes.push({ source, header, bytes });
    }
  }
  const tables: CatalogRuntimeTable[] = [];
  for (const runtime of runtimes) {
    const decodedRuntime = runtime.bytes.toString();
    const roundTrip = new TextEncoder().encode(decodedRuntime);
    const hasBom = runtime.bytes.length >= 3 && runtime.bytes[0] === 0xef && runtime.bytes[1] === 0xbb && runtime.bytes[2] === 0xbf;
    if (hasBom || roundTrip.length !== runtime.bytes.length || roundTrip.some((byte, index) => byte !== runtime.bytes[index]) ||
        /\r(?!\n)/.test(decodedRuntime) || !decodedRuntime.endsWith("\n"))
      throw mismatch(runtime.source.fileName, runtime.bytes);
    const companions = [...declarations.values()].filter(item => item.header.runtimeFile !== undefined &&
      pathIdentity(item.header.runtimeFile) === pathIdentity(path.basename(runtime.source.fileName)) &&
      pathIdentity(path.dirname(item.source.fileName)) === pathIdentity(path.dirname(runtime.source.fileName)));
    for (const declaration of companions) {
      const header = declaration.header;
      if (header.formatVersion !== runtime.header.formatVersion || header.generatorVersion !== runtime.header.generatorVersion ||
          header.contentHash !== runtime.header.contentHash ||
          (catalogText !== undefined && header.contentHash !== catalogHash(runtime.source.fileName, catalogText, runtime.bytes)) ||
          header.runtimeSha256 !== createHash("sha256").update(decodedRuntime.replace(/\r\n/g, "\n"), "utf8").digest("hex"))
        throw mismatch(runtime.source.fileName, runtime.bytes);
    }
    if (companions.length === 0) throw mismatch(runtime.source.fileName, runtime.bytes);
    tables.push(runtimeTable(runtime.source, runtime.header, runtime.bytes));
  }
  return tables;
}
