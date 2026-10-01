import { test } from "node:test";
import { deepEqual, equal, ok } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile, parseArgs } from "../src/cli.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");

test("project base follows entry config, explicit override, and source fallback regardless of output", async () => {
  const root = fs.mkdtempSync(path.join(packageRoot, ".project-test-"));
  try {
    const project = path.join(root, "project");
    const sourceDir = path.join(project, "content");
    const other = path.join(root, "other");
    fs.mkdirSync(sourceDir, { recursive: true });
    fs.mkdirSync(other);
    fs.writeFileSync(path.join(project, ".resoloop.json"), "{}");
    fs.writeFileSync(path.join(other, ".resoloop.json"), "{}");
    const entry = path.join(sourceDir, "main.tsx");
    fs.writeFileSync(entry, 'import { Slot } from "resoloop-jsx"; export const ownership = { key: "house-world" }; export default <Slot key="root" name="House" />;');
    for (const output of [path.join(project, "out/a.json"), path.join(other, "b.json"), path.join(root, "outside.json")]) {
      const result = await buildFile(entry, { output });
      equal(result.exitCode, 0, result.errors.join("\n"));
      deepEqual(result.document!.authoring, { projectRoot: project, source: "content/main.tsx", ownershipSource: "entry-export" });
      equal(result.document!.ownership.key, "house-world");
      equal(result.document!.slot.key, "root");
    }
    const override = await buildFile(entry, { projectRoot: other });
    deepEqual(override.document!.authoring, { projectRoot: other, source: "../project/content/main.tsx", ownershipSource: "entry-export" });
    fs.rmSync(path.join(project, ".resoloop.json"));
    const fallback = await buildFile(entry);
    equal(fallback.document!.authoring!.projectRoot, sourceDir);
    equal(fallback.document!.authoring!.source, "main.tsx");
    fs.writeFileSync(entry, 'import { Slot } from "resoloop-jsx"; export const ownership = undefined; export default <Slot key="root" name="House" />;');
    equal((await buildFile(entry)).exitCode, 1);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test("project-root argument forms and missing values", () => {
  equal(parseArgs(["build", "a.tsx", "-o", "b.json", "--project-root", "project"]).args!.projectRoot, "project");
  equal(parseArgs(["build", "a.tsx", "-o", "b.json", "--project-root=project"]).args!.projectRoot, "project");
  for (const args of [["--project-root"], ["--project-root="], ["--project-root", "--draft"]])
    ok(parseArgs(["build", "a.tsx", "-o", "b.json", ...args]).error);
});
