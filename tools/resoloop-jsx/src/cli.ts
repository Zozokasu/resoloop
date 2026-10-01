#!/usr/bin/env node
// resoloop-jsx build <entry.tsx> -o <out.json> [--draft] [--project-root PATH]
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
import { pathToFileURL } from "node:url";
import * as ts from "typescript";
import { evaluate, BuildError } from "./evaluate.js";
import type { ApplyDocument } from "./evaluate.js";
import { sourceTransformer } from "./source-transform.js";
import { buildErrorLocation } from "./source-map.js";
import { snapshotInputs } from "./input-snapshot.js";
import { reserveBundleOutput, publishBundle } from "./bundle.js";

export interface BuildOptions {
  draft?: boolean;
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
}

/** Longest common directory prefix of two normalized paths. */
function commonDir(a: string, b: string): string {
  const left = path.resolve(a).split(/[\\/]/);
  const right = path.resolve(b).split(/[\\/]/);
  const out: string[] = [];
  for (let i = 0; i < Math.min(left.length, right.length); i++) {
    if (left[i] !== right[i]) break;
    out.push(left[i]);
  }
  return out.length === 0 ? a : out.join(path.sep);
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
  const outDir = fs.mkdtempSync(path.join(path.dirname(entryAbs), ".resoloop-jsx-"));
  try {
    const bundleOutput = opts.bundle ? reserveBundleOutput(opts.output!) : undefined;
    // rootDir is required for self-referencing package resolution (TS2209);
    // constraining it to the entry's directory also keeps emit predictable.
    const rootDir = path.dirname(entryAbs);
    const program = ts.createProgram([entryAbs], {
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

    const inputs = opts.bundle ? snapshotInputs(program, projectRoot, opts.catalog!) : undefined;
    const catalogText = opts.bundle ? fs.readFileSync(path.resolve(opts.catalog!), { encoding: "utf8" }) : undefined;
    const preEmit = ts.getPreEmitDiagnostics(program);
    if (preEmit.length > 0)
      return {
        exitCode: 2,
        errors: [formatDiagnostics(preEmit)],
        warnings,
      };

    const emitResult = program.emit(undefined, undefined, undefined, false, opts.bundle ? sourceTransformer(program, inputs!) : undefined);
    if (emitResult.diagnostics.length > 0)
      return {
        exitCode: 2,
        errors: [formatDiagnostics(emitResult.diagnostics)],
        warnings,
      };

    // Locate the emitted entry JS: outDir mirrors the path of each input
    // relative to the program's common source directory (the longest common
    // directory prefix of all emitted inputs).
    const inputDirs = program
      .getSourceFiles()
      .filter((f) => !f.isDeclarationFile)
      .map((f) => path.dirname(f.fileName));
    const commonRoot = inputDirs.reduce(commonDir, path.dirname(entryAbs));
    const emittedJs = path
      .join(outDir, path.relative(commonRoot, entryAbs))
      .replace(/\.[^.\\/]+$/, ".js");

    let mod: any;
    try {
      mod = await import(pathToFileURL(emittedJs).href);
    } catch (err: any) {
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
      draft: opts.draft === true,
      ...(Object.prototype.hasOwnProperty.call(mod, "ownership") ? { ownership: mod.ownership } : {}),
    });
    document.authoring = {
      projectRoot,
      source: path.relative(projectRoot, entryAbs).split(path.sep).join("/"),
      ownershipSource: Object.prototype.hasOwnProperty.call(mod, "ownership") ? "entry-export" : "root-key",
    };
    warnings.push(...draftWarnings);

    if (bundleOutput !== undefined) {
      publishBundle(document, inputs!, catalogText!, opts.buildId!, bundleOutput);
    } else if (opts.output !== undefined) {
      const outAbs = path.resolve(opts.output);
      const parent = path.dirname(outAbs);
      if (!fs.existsSync(parent)) fs.mkdirSync(parent, { recursive: true });
      fs.writeFileSync(outAbs, JSON.stringify(document, null, 2) + "\n", {
        encoding: "utf8",
      });
    }
    return { exitCode: 0, errors, warnings, document };
  } catch (err: any) {
    if (err instanceof BuildError)
      return { exitCode: 1, errors: [`${err.code}: ${err.message}${opts.bundle ? `; TSX ${buildErrorLocation(err)}` : ""}`], warnings };
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
    "usage: resoloop-jsx build <entry.tsx> -o <out.json> [--draft] [--project-root PATH]",
    "",
    "  build <entry.tsx>   JSX entry module; must `export default` a single <Slot>",
    "  -o, --output PATH   schema-v1 ApplyDocument output JSON path (required)",
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
