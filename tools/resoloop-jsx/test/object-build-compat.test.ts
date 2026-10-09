import { test } from "node:test";
import { deepEqual } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile } from "../src/cli.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

test("non-Object BuildError thrown during module import keeps legacy wrapped output", async () => {
  const root = fs.mkdtempSync(path.join(packageRoot, ".object-build-compat-"));
  const entry = path.join(root, "main.tsx");
  fs.writeFileSync(entry, 'import { evaluate } from "resoloop-jsx";\nevaluate(null);\nexport default null;\n');
  try {
    deepEqual(await buildFile(entry, { projectRoot: root }), {
      exitCode: 1,
      errors: ["EVAL_FAILED: failed to load compiled module: The entry module's default export must evaluate to exactly one <Slot> element; got none"],
      warnings: [],
    });
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});
