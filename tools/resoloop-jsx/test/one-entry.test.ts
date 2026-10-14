import { test } from "node:test";
import { equal, match, ok } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile } from "../src/cli.js";

const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const fixturesDir = path.join(packageRoot, "test/fixtures");
const baselineDir = path.join(fixturesDir, "one-entry");
const source = `import { Slot, Component } from "resoloop-jsx";
export const ownership = { key: "entry" };
export default (
  <Slot name="Root">
    <Slot name="Child">
      <Component type="Probe" />
    </Slot>
  </Slot>
);
`;

function workspace() {
  const root = fs.mkdtempSync(path.join(packageRoot, ".one-entry-test-"));
  const entry = path.join(root, "main.tsx");
  const output = path.join(root, "out.json");
  fs.writeFileSync(entry, source);
  return { root, entry, output, cleanup: () => fs.rmSync(root, { recursive: true, force: true }) };
}

function normalized(text: string): string {
  const escapedRoot = JSON.stringify(fixturesDir).slice(1, -1);
  return text.replace(/\r\n/g, "\n").replaceAll(escapedRoot, "__FIXTURES_DIR__");
}

test("main entry accepts omitted Slot and Component keys for creation and matches draft alias", async () => {
  const w = workspace();
  try {
    const main = await buildFile(w.entry, { target: "creation", output: w.output, projectRoot: w.root });
    equal(main.exitCode, 0, main.errors.join("\n"));
    ok(!main.errors.some((message) => /error TS\d+/.test(message)));
    const mainBytes = fs.readFileSync(w.output, { encoding: "utf8" });

    fs.writeFileSync(w.entry, source.replace('from "resoloop-jsx"', 'from "resoloop-jsx/draft"'));
    const draft = await buildFile(w.entry, { target: "creation", output: w.output, projectRoot: w.root });
    equal(draft.exitCode, 0, draft.errors.join("\n"));
    equal(fs.readFileSync(w.output, { encoding: "utf8" }), mainBytes);
  } finally { w.cleanup(); }
});

test("same creation and draft fixtures retain their saved output", async () => {
  const cases = [
    ["creation-keys", path.join(fixturesDir, "creation-keys/main.tsx"), { target: "creation" as const }],
    ["creation-contract-siblings", path.join(fixturesDir, "creation-contract/siblings.tsx"), { target: "creation" as const }],
    ["missing-key-draft", path.join(fixturesDir, "missing-key-draft.tsx"), { draft: true }],
  ] as const;
  const output = path.join(baselineDir, ".one-entry-output.json");
  try {
    for (const [name, entry, options] of cases) {
      const result = await buildFile(entry, { ...options, output, projectRoot: fixturesDir });
      equal(result.exitCode, 0, `${name}: ${result.errors.join("\n")}`);
      equal(normalized(fs.readFileSync(output, { encoding: "utf8" })), fs.readFileSync(path.join(baselineDir, `${name}.json`), { encoding: "utf8" }).replace(/\r\n/g, "\n"));
    }
  } finally { fs.rmSync(output, { force: true }); }
});

test("ordinary target reports missing key at its TSX location without writing output", async () => {
  const w = workspace();
  try {
    const result = await buildFile(w.entry, { output: w.output, projectRoot: w.root });
    equal(result.exitCode, 1);
    const messages = result.errors.join("\n");
    match(messages, /EXPLICIT_KEY_REQUIRED/);
    match(messages, /main\.tsx:4:3/);
    ok(!/error TS\d+/.test(messages));
    ok(!fs.existsSync(w.output));
  } finally { w.cleanup(); }
});

test("creation rejects draft option with usage error and no output", async () => {
  const w = workspace();
  try {
    const result = await buildFile(w.entry, { target: "creation", draft: true, output: w.output, projectRoot: w.root });
    equal(result.exitCode, 1);
    match(result.errors.join("\n"), /cannot be combined with --draft/);
    ok(!fs.existsSync(w.output));
  } finally { w.cleanup(); }
});
