import { test } from "node:test";
import { equal, match, ok } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile } from "../src/cli.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

test("module object default export explains project package.json type module", async () => {
  // This standalone TSX deliberately supplies its own element rather than importing
  // ESM from CommonJS, so it reaches the runtime wrapping error instead of TS1479.
  const root = fs.mkdtempSync(path.join(packageRoot, ".l2-module-wrapper-"));
  try {
    fs.writeFileSync(path.join(root, "package.json"), '{"type":"commonjs"}');
    const entry = path.join(root, "main.tsx"), output = path.join(root, "out.json");
    fs.writeFileSync(entry, 'export const ownership = { key: "root" }; export default { kind: "slot", props: { key: "root", name: "Root" } };');
    const result = await buildFile(entry, { output });
    equal(result.exitCode, 1);
    match(result.errors.join("\n"), /ROOT_MUST_BE_SINGLE_SLOT/);
    match(result.errors.join("\n"), /project の package\.json に "type": "module" が必要/);
    ok(!fs.existsSync(output));
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test("external TSX project resolves local node_modules runtime with type module", async () => {
  // Outside the authoring package, with its own package boundary and local dependency.
  const root = fs.mkdtempSync(path.join(path.dirname(packageRoot), ".l2-external-"));
  try {
    fs.writeFileSync(path.join(root, "package.json"), '{"private":true,"type":"module"}');
    fs.mkdirSync(path.join(root, "node_modules"));
    fs.symlinkSync(packageRoot, path.join(root, "node_modules/resoloop-jsx"), "junction");
    const entry = path.join(root, "main.tsx"), output = path.join(root, "out.json");
    fs.writeFileSync(entry, 'import { Slot } from "resoloop-jsx"; export const ownership = { key: "root" }; export default <Slot key="root" name="External" />;');
    const result = await buildFile(entry, { output });
    equal(result.exitCode, 0, result.errors.join("\n"));
    equal(result.document!.slot.name, "External");
    ok(fs.existsSync(output));
  } finally {
    // Remove the junction itself before the temporary project tree.
    fs.rmSync(path.join(root, "node_modules/resoloop-jsx"), { recursive: true, force: true });
    fs.rmSync(root, { recursive: true, force: true });
  }
});
