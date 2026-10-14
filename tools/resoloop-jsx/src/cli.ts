#!/usr/bin/env node
// resoloop-jsx build <entry.tsx> -o <out.json> [--target creation] [--draft] [--project-root PATH]
//
// Type-checks a JSX-authored slot/component tree with the TypeScript
// compiler API, transpiles and evaluates it in-process, then emits a
// schema-v1 ApplyDocument JSON file.
//
// Exit codes:
//   0  success
//   1  input/build error (bad argv, missing entry, evaluation failure)
//   2  TypeScript diagnostics in the entry file

import * as fs from "node:fs";
import * as path from "node:path";
import { createHash } from "node:crypto";
import { pathToFileURL } from "node:url";
import * as ts from "typescript";
import { evaluate, BuildError } from "./evaluate.js";
import type { ApplyDocument } from "./evaluate.js";
import { sourceTransformer } from "./source-transform.js";
import { buildErrorLocation } from "./source-map.js";
import { snapshotInputs } from "./input-snapshot.js";
import type { InputFile } from "./input-snapshot.js";
import { reserveBundleOutput, publishBundle } from "./bundle.js";
import { catalogDeclaration, catalogRuntimeTables } from "./catalog-declarations.js";

export interface BuildOptions {
  draft?: boolean;
  target?: "creation";
  output?: string;
  projectRoot?: string;
  bundle?: boolean;
  catalog?: string;
  buildId?: string;
}

export interface BuildResult {
  exitCode: 0 | 1 | 2;
  errors: string[];
  warnings: string[];
  document?: ApplyDocument;
  creation?: CreationEnvelope;
}

export interface CreationEnvelope {
  schemaVersion: "creation-1";
  kind: "resoloop-creation";
  creationVersion: "1";
  creationId: string;
  definition: ApplyDocument;
  authoring: NonNullable<ApplyDocument["authoring"]>;
}

function formatDiagnostics(diagnostics: readonly ts.Diagnostic[]): string {
  // Keep the pretty context output but strip ANSI codes so callers (tests,
  // stderr redirection) see plain text.
  return ts
    .formatDiagnosticsWithColorAndContext(diagnostics, formatHost())
    .replace(/\x1B\[[0-9;]*m/g, "");
}

function formatHost(): ts.FormatDiagnosticsHost {
  return {
    getCurrentDirectory: () => process.cwd(),
    getCanonicalFileName: (f: string) => f,
    getNewLine: () => "\n",
  };
}

const sourceLocatedBuildCodes = new Set([
  "APPLY_OBJECT_INVALID", "APPLY_OBJECT_DUPLICATE", "APPLY_OBJECT_EXPORT_INVALID",
  "APPLY_OBJECT_EXPORT_UNKNOWN", "APPLY_OBJECT_TYPE_MISMATCH", "APPLY_OBJECT_PRIVATE_REFERENCE",
  "APPLY_FIELD_TYPE_MISMATCH", "APPLY_CATALOG_UNAVAILABLE", "APPLY_CATALOG_TYPES_HASH_MISMATCH",
  "CREATION_DRIVEN_FIELD_DECLARED", "CREATION_VARIABLE_DUPLICATE", "CREATION_VARIABLE_TYPE_MISMATCH",
  "CREATION_VARIABLE_TYPE_UNSUPPORTED", "CREATION_VARIABLE_UNRESOLVED", "CREATION_ID_INVALID", "CREATION_SPACE_SHADOWED",
  "EXPLICIT_KEY_REQUIRED",
]);

function pathIdentity(value: string): string {
  const resolved = path.resolve(value).replace(/\\/g, "/");
  return ts.sys.useCaseSensitiveFileNames ? resolved : resolved.toLowerCase();
}

/**
 * Type-check + transpile + evaluate `entryAbs`, then return the resulting
 * ApplyDocument (writing it to `opts.output` when given). Never throws for
 * expected failures; everything is folded into BuildResult.exitCode.
 */
export async function buildFile(
  entryPath: string,
  opts: BuildOptions = {}
): Promise<BuildResult> {
  const errors: string[] = [];
  const warnings: string[] = [];
  if (opts.target !== undefined && opts.target !== "creation")
    return { exitCode: 1, errors: [`unsupported --target value: ${String(opts.target)}`], warnings };
  if (opts.target === "creation") {
    const incompatible = opts.draft ? "--draft" : opts.bundle ? "--bundle" :
      opts.catalog !== undefined ? "--catalog" : opts.buildId !== undefined ? "--build-id" : undefined;
    if (incompatible !== undefined)
      return { exitCode: 1, errors: [`--target creation cannot be combined with ${incompatible} in this version`], warnings };
  }
  if (opts.bundle ? !opts.output || !opts.catalog?.trim() || !opts.buildId?.trim() : opts.catalog !== undefined || opts.buildId !== undefined)
    return { exitCode: 1, errors: ["--bundle requires output, --catalog FILE and --build-id R; catalog/build-id require --bundle"], warnings };
  const entryAbs = path.resolve(entryPath);
  if (!fs.existsSync(entryAbs))
    return {
      exitCode: 1,
      errors: [`entry file not found: ${entryPath}`],
      warnings,
    };

  if (opts.projectRoot !== undefined && opts.projectRoot.trim().length === 0)
    return { exitCode: 1, errors: ["--project-root requires a non-empty path"], warnings };
  let projectRoot = opts.projectRoot === undefined ? path.dirname(entryAbs) : path.resolve(opts.projectRoot);
  if (opts.projectRoot === undefined) {
    let current = path.dirname(entryAbs);
    while (true) {
      if (fs.existsSync(path.join(current, ".resoloop.json"))) { projectRoot = current; break; }
      const parent = path.dirname(current);
      if (parent === current) break;
      current = parent;
    }
  }

  // The compiled output must live under the entry's own directory so that
  // `import "resoloop-jsx"` in the emitted JS resolves — for entries inside
  // this package via self-reference, for external consumers via their own
  // node_modules lookup. Deleted in `finally`.
  const entryRelativeToProject = path.relative(projectRoot, entryAbs);
  const normalizedEntryRelative = entryRelativeToProject.replace(/\\/g, "/");
  const entryInsideProject = pathIdentity(entryAbs) === pathIdentity(projectRoot) ||
    (!path.isAbsolute(entryRelativeToProject) && normalizedEntryRelative !== ".." && !normalizedEntryRelative.startsWith("../"));
  const rootDir = entryInsideProject ? projectRoot : path.dirname(entryAbs);
  const outDir = fs.mkdtempSync(path.join(path.dirname(entryAbs), ".resoloop-jsx-"));
  try {
    const catalogText = opts.bundle ? fs.readFileSync(path.resolve(opts.catalog!), { encoding: "utf8" }) : undefined;
    const declaration = catalogDeclaration(projectRoot, catalogText);
    const program = ts.createProgram(declaration === undefined ? [entryAbs] : [entryAbs, declaration], {
      jsx: ts.JsxEmit.ReactJSX,
      jsxImportSource: "resoloop-jsx",
      module: ts.ModuleKind.NodeNext,
      moduleResolution: ts.ModuleResolutionKind.NodeNext,
      target: ts.ScriptTarget.ES2022,
      strict: true,
      esModuleInterop: true,
      skipLibCheck: true,
      rootDir,
      outDir,
      declaration: false,
    });

    const catalogs = catalogRuntimeTables(program, catalogText);
    const bundleOutput = opts.bundle ? reserveBundleOutput(opts.output!) : undefined;

    const inputs: InputFile[] = opts.bundle ? snapshotInputs(program, projectRoot, opts.catalog!) : program.getSourceFiles()
      .filter(source => !program.isSourceFileDefaultLibrary(source))
      .map(source => {
        const file = path.resolve(source.fileName);
        const sha256 = createHash("sha256").update(fs.readFileSync(file)).digest("hex");
        return { path: file, sha256, role: "source" as const };
      });
    const preEmit = ts.getPreEmitDiagnostics(program);
    if (preEmit.length > 0)
      return {
        exitCode: 2,
        errors: [formatDiagnostics(preEmit)],
        warnings,
      };

    const emitResult = program.emit(undefined, undefined, undefined, false, sourceTransformer(program, inputs));
    if (emitResult.diagnostics.length > 0)
      return {
        exitCode: 2,
        errors: [formatDiagnostics(emitResult.diagnostics)],
        warnings,
      };

    const emittedJs = path
      .join(outDir, path.relative(rootDir, entryAbs))
      .replace(/\.[^.\\/]+$/, ".js");

    let mod: any;
    try {
      mod = await import(pathToFileURL(emittedJs).href);
    } catch (err: any) {
      if (err instanceof BuildError && sourceLocatedBuildCodes.has(err.code)) throw err;
      return {
        exitCode: 1,
        errors: [
          `EVAL_FAILED: failed to load compiled module: ${err?.message ?? String(err)}`,
        ],
        warnings,
      };
    }

    if (mod?.default && typeof mod.default === "object" &&
        Object.prototype.hasOwnProperty.call(mod.default, "default") &&
        Object.prototype.hasOwnProperty.call(mod.default, "ownership"))
      throw new BuildError("ROOT_MUST_BE_SINGLE_SLOT", 'The default export is wrapped in a module object; project の package.json に "type": "module" が必要');

    const { document, warnings: draftWarnings } = evaluate(mod?.default, {
      catalogs,
      draft: opts.draft === true,
      creation: opts.target === "creation",
      ...(Object.prototype.hasOwnProperty.call(mod, "ownership") ? { ownership: mod.ownership } : {}),
    });
    const authoring = {
      projectRoot,
      source: path.relative(projectRoot, entryAbs).split(path.sep).join("/"),
      ownershipSource: Object.prototype.hasOwnProperty.call(mod, "ownership") ? "entry-export" : "root-key",
    };
    if (opts.target !== "creation") document.authoring = authoring;
    warnings.push(...draftWarnings);

    const creation: CreationEnvelope | undefined = opts.target === "creation" ? {
      schemaVersion: "creation-1",
      kind: "resoloop-creation",
      creationVersion: "1",
      creationId: document.ownership.key,
      definition: document,
      authoring,
    } : undefined;

    if (bundleOutput !== undefined) {
      publishBundle(document, inputs!, catalogText!, opts.buildId!, bundleOutput);
    } else if (opts.output !== undefined) {
      const outAbs = path.resolve(opts.output);
      const parent = path.dirname(outAbs);
      if (!fs.existsSync(parent)) fs.mkdirSync(parent, { recursive: true });
      fs.writeFileSync(outAbs, JSON.stringify(creation ?? document, null, 2) + "\n", {
        encoding: "utf8",
      });
    }
    return { exitCode: 0, errors, warnings, document, ...(creation === undefined ? {} : { creation }) };
  } catch (err: any) {
    if (err instanceof BuildError) {
      const located = opts.bundle || sourceLocatedBuildCodes.has(err.code);
      return { exitCode: 1, errors: [`${err.code}: ${err.message}${opts.bundle || located ? `; TSX ${buildErrorLocation(err)}` : ""}`], warnings };
    }
    return {
      exitCode: 1,
      errors: [`${err?.message ?? String(err)}`],
      warnings,
    };
  } finally {
    try {
      fs.rmSync(outDir, { recursive: true, force: true });
    } catch {
      // best effort
    }
  }
}

function usage(): string {
  return [
    "usage: resoloop-jsx build <entry.tsx> -o <out.json> [--target creation] [--draft] [--project-root PATH]",
    "",
    "  build <entry.tsx>   JSX entry module; must `export default` a single <Slot>",
    "  -o, --output PATH   output JSON path (required): schema-v1 ApplyDocument by default, creation envelope with --target creation",
    "  --target creation   emit a creation-1 envelope for plan/apply --mode regenerate",
    "                      creationId uses entry ownership.key, else the root Slot's explicit key; never a generated key",
    "                      omitted keys and same-name sibling Slots are allowed only for creation; explicit keys stay unique",
    "                      cannot combine with --draft, --bundle, --catalog, or --build-id; no creation bundle is available",
    "                      unsupported content stops in plan/apply preflight before connection",
    "                      experimental; TSX creation and same-name sibling regeneration are unverified live",
    "  --project-root PATH explicit project base (otherwise nearest source config)",
    "  --draft             allow missing keys; deterministic keys are generated",
    "                      and a warning is printed per generated key",
    "  --bundle --catalog FILE --build-id R   commit a request-bound build bundle",
    "                      output must be inside a new directory",
  ].join("\n");
}

export interface ParsedArgs {
  entry?: string;
  output?: string;
  projectRoot?: string;
  draft: boolean;
  target?: "creation";
  bundle?: boolean;
  catalog?: string;
  buildId?: string;
}

export function parseArgs(argv: string[]): { args?: ParsedArgs; error?: string } {
  if (argv.length === 0 || argv[0] !== "build")
    return { error: `expected the 'build' subcommand\n${usage()}` };
  const parsed: ParsedArgs = { draft: false };
  const rest = argv.slice(1);
  for (let i = 0; i < rest.length; i++) {
    const arg = rest[i];
    if (arg === "--bundle") {
      parsed.bundle = true;
    } else if (arg === "--target" || arg.startsWith("--target=")) {
      const value = arg.includes("=") ? arg.slice(arg.indexOf("=") + 1) : rest[++i];
      if (value === undefined || value.length === 0 || !arg.includes("=") && value.startsWith("-"))
        return { error: "--target requires a value" };
      if (value !== "creation") return { error: `unsupported --target value: ${value}` };
      parsed.target = "creation";
    } else if (arg === "--catalog" || arg === "--build-id" || arg.startsWith("--catalog=") || arg.startsWith("--build-id=")) {
      const name = arg.split("=")[0];
      const value = arg.includes("=") ? arg.slice(arg.indexOf("=") + 1) : rest[++i];
      if (!value?.trim() || value.startsWith("-")) return { error: `${name} requires a value` };
      if (name === "--catalog") parsed.catalog = value; else parsed.buildId = value;
    } else if (arg === "--draft") {
      parsed.draft = true;
    } else if (arg === "--project-root") {
      const value = rest[++i];
      if (!value || value.startsWith("-")) return { error: "--project-root requires a path argument" };
      parsed.projectRoot = value;
    } else if (arg.startsWith("--project-root=")) {
      parsed.projectRoot = arg.slice("--project-root=".length);
      if (!parsed.projectRoot.trim()) return { error: "--project-root requires a path argument" };
    } else if (arg === "-o" || arg === "--output") {
      const value = rest[++i];
      if (value === undefined) return { error: `${arg} requires a path argument` };
      parsed.output = value;
    } else if (arg.startsWith("--output=")) {
      parsed.output = arg.slice("--output=".length);
    } else if (arg.startsWith("-o=")) {
      parsed.output = arg.slice(3);
    } else if (arg.startsWith("-")) {
      return { error: `unknown option: ${arg}` };
    } else if (parsed.entry === undefined) {
      parsed.entry = arg;
    } else {
      return { error: `unexpected extra argument: ${arg}` };
    }
  }
  if (parsed.entry === undefined)
    return { error: `missing <entry.tsx>\n${usage()}` };
  if (parsed.output === undefined)
    return { error: `missing -o/--output <out.json>\n${usage()}` };
  if (parsed.target === "creation") {
    const incompatible = parsed.draft ? "--draft" : parsed.bundle ? "--bundle" :
      parsed.catalog !== undefined ? "--catalog" : parsed.buildId !== undefined ? "--build-id" : undefined;
    if (incompatible !== undefined)
      return { error: `--target creation cannot be combined with ${incompatible} in this version` };
  }
  if (parsed.bundle ? !parsed.catalog || !parsed.buildId : parsed.catalog !== undefined || parsed.buildId !== undefined)
    return { error: "--bundle requires --catalog FILE and --build-id R; catalog/build-id require --bundle" };
  return { args: parsed };
}

export async function main(argv: string[]): Promise<number> {
  const { args, error } = parseArgs(argv);
  if (args === undefined) {
    process.stderr.write(`${error}\n`);
    return 1;
  }
  const result = await buildFile(args.entry!, {
    draft: args.draft,
    target: args.target,
    output: args.output,
    projectRoot: args.projectRoot,
    bundle: args.bundle, catalog: args.catalog, buildId: args.buildId,
  });
  for (const warning of result.warnings) process.stderr.write(`${warning}\n`);
  for (const message of result.errors) process.stderr.write(`${message}\n`);
  if (result.exitCode === 0)
    process.stdout.write(`wrote ${path.resolve(args.output!)}\n`);
  return result.exitCode;
}

const invokedPath = process.argv[1]
  ? pathToFileURL(path.resolve(process.argv[1])).href
  : "";
if (import.meta.url === invokedPath) {
  main(process.argv.slice(2)).then(
    (code) => process.exit(code),
    (err) => {
      process.stderr.write(`${err?.message ?? String(err)}\n`);
      process.exit(1);
    }
  );
}
