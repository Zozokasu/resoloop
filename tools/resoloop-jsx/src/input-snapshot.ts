import * as fs from "node:fs";
import * as path from "node:path";
import { createHash } from "node:crypto";
import * as ts from "typescript";

export const hashText = (text: string): string => createHash("sha256").update(text, "utf8").digest("hex");
const hashFile = (file: string): string => createHash("sha256").update(fs.readFileSync(file)).digest("hex");
export interface InputFile { path: string; sha256: string; role: "source" | "catalog" }

/** Import graph only: this deliberately makes no claim about environment/time or arbitrary calls. */
export function snapshotInputs(program: ts.Program, projectRoot: string, catalog: string): InputFile[] {
  const files: InputFile[] = [];
  const runtime = ts.resolveModuleName("resoloop-jsx", program.getRootFileNames()[0], program.getCompilerOptions(), ts.sys).resolvedModule;
  const runtimeRoot = runtime ? path.dirname(path.resolve(runtime.resolvedFileName)) : undefined;
  const within = (root: string, file: string): boolean => {
    const relative = path.relative(root, file);
    return relative !== ".." && !relative.startsWith(".." + path.sep) && !path.isAbsolute(relative);
  };
  for (const source of program.getSourceFiles()) {
    const file = path.resolve(source.fileName);
    const bytes = fs.readFileSync(file);
    const sha256 = createHash("sha256").update(bytes).digest("hex");
    if (bytes.toString("utf8").replace(/^\uFEFF/, "") !== source.text) throw new Error(`inputChanged: ${file}`);
    if (!program.isSourceFileDefaultLibrary(source)) {
      const relative = path.relative(projectRoot, file);
      if (!(runtimeRoot && within(runtimeRoot, file)) &&
          (!within(projectRoot, file) || relative.split(path.sep).includes("node_modules")))
        throw new Error(`inputUnknown: source outside project: ${file}`);
    }
    if (!program.isSourceFileDefaultLibrary(source)) {
      const moduleName = (name: string): void => {
        if (["resoloop-jsx", "resoloop-jsx/jsx-runtime", "resoloop-jsx/draft"].includes(name)) return;
        if (!name.startsWith("./") && !name.startsWith("../")) throw new Error(`inputUnknown: external module ${name}`);
        const resolved = ts.resolveModuleName(name, file, program.getCompilerOptions(), ts.sys).resolvedModule;
        if (!resolved || !program.getSourceFile(resolved.resolvedFileName)) throw new Error(`inputUnknown: unresolved module ${name}`);
      };
      const visit = (node: ts.Node): void => {
        if ((ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) && node.moduleSpecifier && ts.isStringLiteral(node.moduleSpecifier))
          moduleName(node.moduleSpecifier.text);
        if (ts.isImportEqualsDeclaration(node) && ts.isExternalModuleReference(node.moduleReference) &&
            node.moduleReference.expression && ts.isStringLiteral(node.moduleReference.expression)) moduleName(node.moduleReference.expression.text);
        if (ts.isCallExpression(node) && (node.expression.kind === ts.SyntaxKind.ImportKeyword ||
            ts.isIdentifier(node.expression) && node.expression.text === "require"))
          throw new Error("inputUnknown: dynamic import/require is unsupported in bundle inputs");
        ts.forEachChild(node, visit);
      };
      visit(source);
    }
    files.push({ path: file, sha256, role: "source" });
  }
  const catalogAbs = path.resolve(catalog);
  if (files.some(f => f.path === catalogAbs)) throw new Error("inputUnknown: catalog is also a source file");
  files.push({ path: catalogAbs, sha256: hashFile(catalogAbs), role: "catalog" });
  return files.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
}

export function verifyInputs(files: InputFile[]): void {
  for (const file of files) if (hashFile(file.path) !== file.sha256) throw new Error(`inputChanged: ${file.path}`);
}
