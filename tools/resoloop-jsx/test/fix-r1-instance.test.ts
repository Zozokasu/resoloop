import { test } from "node:test";
import { equal } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile } from "../src/cli.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

test("invalid Def.instance ID points to the handle expression", async () => {
  const root = fs.mkdtempSync(path.join(packageRoot, ".fix-r1-instance-"));
  const entry = path.join(root, "main.tsx");
  const source = [
    'import { Slot, defineObject } from "resoloop-jsx";',
    'const Def = defineObject<{}>()({ exports: {}, body: () => <Slot key="body" name="Body" /> });',
    'export default <Slot key="root" name="Scene"><Def handle={Def.instance("bad.id")} /></Slot>;',
  ].join("\n");
  fs.writeFileSync(entry, source);
  try {
    const result = await buildFile(entry, { projectRoot: root });
    equal(result.exitCode, 1);
    const expressionColumn = source.split("\n")[2].indexOf('Def.instance("bad.id")') + 1;
    equal(result.errors[0], `APPLY_OBJECT_INVALID: Object 'bad.id': invalid instance ID.; TSX ${entry}:3:${expressionColumn}`);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test("an imported Object definition instance call points to its handle expression", async () => {
  const root = fs.mkdtempSync(path.join(packageRoot, ".fix-r1-instance-import-"));
  const entry = path.join(root, "main.tsx");
  const source = [
    'import { Slot } from "resoloop-jsx";',
    'import { Def } from "./def.js";',
    'export default <Slot key="root" name="Scene"><Def handle={Def.instance("bad.id")} /></Slot>;',
  ].join("\n");
  fs.writeFileSync(path.join(root, "def.tsx"), [
    'import { Slot, defineObject } from "resoloop-jsx";',
    'export const Def = defineObject<{}>()({ exports: {}, body: () => <Slot key="body" name="Body" /> });',
  ].join("\n"));
  fs.writeFileSync(entry, source);
  try {
    const result = await buildFile(entry, { projectRoot: root });
    equal(result.exitCode, 1);
    const expressionColumn = source.split("\n")[2].indexOf('Def.instance("bad.id")') + 1;
    equal(result.errors[0], `APPLY_OBJECT_INVALID: Object 'bad.id': invalid instance ID.; TSX ${entry}:3:${expressionColumn}`);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
