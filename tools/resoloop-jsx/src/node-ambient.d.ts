// Minimal ambient declarations for the Node.js builtins this package uses.
// The package intentionally has no @types/node dependency (dependency budget:
// typescript only), so only the members actually called are declared here,
// with intentionally loose signatures.

declare module "node:fs" {
  export function mkdtempSync(prefix: string): string;
  export function mkdirSync(path: string, options?: { recursive?: boolean }): void;
  export function symlinkSync(target: string, path: string, type?: string): void;
  export function writeFileSync(path: string, data: string, options?: { encoding?: string; flag?: string }): void;
  export function readFileSync(path: string): any;
  export function readFileSync(path: string, options: { encoding?: string }): string;
  export function renameSync(oldPath: string, newPath: string): void;
  export function rmSync(path: string, options?: { recursive?: boolean; force?: boolean }): void;
  export function existsSync(path: string): boolean;
  export function readdirSync(path: string, options?: { withFileTypes?: boolean }): any[];
  export function statSync(path: string): { mtimeMs: number; isFile(): boolean; isDirectory(): boolean };
}

declare module "node:crypto" {
  export function createHash(name: string): { update(data: any, encoding?: string): any; digest(encoding: string): string };
  export function randomUUID(): string;
}

declare module "node:path" {
  export function dirname(p: string): string;
  export function join(...parts: string[]): string;
  export function resolve(...parts: string[]): string;
  export function basename(p: string, ext?: string): string;
  export function extname(p: string): string;
  export function relative(from: string, to: string): string;
  export function isAbsolute(p: string): boolean;
  export const sep: string;
}

declare module "node:url" {
  export function pathToFileURL(path: string): { href: string };
  export function fileURLToPath(url: string | { href: string }): string;
}

declare module "node:child_process" {
  export function spawnSync(
    command: string,
    args?: string[],
    options?: { cwd?: string; encoding?: string; shell?: boolean }
  ): { status: number | null; stdout: string; stderr: string; error?: any };
}

declare module "node:test" {
  export function test(name: string, fn: () => any): any;
  export function test(name: string, options: any, fn: () => any): any;
  export function describe(name: string, fn: () => void): any;
}

declare module "node:assert/strict" {
  export function equal(actual: any, expected: any, message?: string): void;
  export function deepEqual(actual: any, expected: any, message?: string): void;
  export function ok(value: any, message?: string): void;
  export function match(string: string, regexp: RegExp, message?: string): void;
  export function throws(fn: () => any, error?: any, message?: string): void;
  export function fail(message?: string): never;
}

declare const process: {
  argv: string[];
  cwd(): string;
  exit(code?: number): never;
  stdout: { write(data: string): boolean };
  stderr: { write(data: string): boolean };
};

interface ImportMeta {
  url: string;
}
