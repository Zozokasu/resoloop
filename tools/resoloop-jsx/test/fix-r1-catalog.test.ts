import { test } from "node:test";
import { deepEqual, equal } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile } from "../src/cli.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
test("treats catalog marker text in ordinary source as ordinary TSX", async () => {
  const root = fs.mkdtempSync(path.join(packageRoot, ".fix-r1-catalog-"));
  const entry = path.join(root, "main.tsx");
  const source = '// note: resoloop-catalog-types: ordinary comment\nimport {Slot} from "resoloop-jsx"; const help="resoloop-catalog-fields:"; // resoloop-catalog-types: inline comment\nexport default <Slot key="root" name={help}/>;';
  fs.writeFileSync(entry, source);
  try {
    const result = await buildFile(entry, { projectRoot: root });
    equal(result.exitCode, 0, result.errors.join("\n"));
    deepEqual(result.errors, []);
    deepEqual(result.warnings, []);
    deepEqual(result.document, {
      schemaVersion: "1", ownership: { key: "root" },
      slot: { name: "resoloop-catalog-fields:", key: "root" }, components: [], children: [],
      authoring: { projectRoot: root, source: "main.tsx", ownershipSource: "root-key" },
    });
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
