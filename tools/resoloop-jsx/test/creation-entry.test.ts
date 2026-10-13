import { test } from "node:test";
import { deepEqual, equal, match, ok } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { buildFile, parseArgs, type BuildOptions } from "../src/cli.js";

const here = path.dirname(fileURLToPath(import.meta.url));
const packageRoot = path.resolve(here, "..", "..");
const fixturesDir = path.join(packageRoot, "test", "fixtures");
const fixture = (name: string) => path.join(fixturesDir, "creation-entry", name);
const expectedBytes = (name: string) => {
  const expected = JSON.parse(fs.readFileSync(fixture(name), { encoding: "utf8" }));
  const replaceRoot = (value: any): any => {
    if (Array.isArray(value)) return value.map(replaceRoot);
    if (value !== null && typeof value === "object") {
      for (const key of Object.keys(value))
        value[key] = key === "projectRoot" && value[key] === "FIXTURE_ROOT" ? fixturesDir : replaceRoot(value[key]);
      return value;
    }
    return value;
  };
  return JSON.stringify(replaceRoot(expected), null, 2) + "\n";
};

test("creation target writes the six-field envelope byte-for-byte and repeats identically", async () => {
  const dir = fs.mkdtempSync(path.join(fixturesDir, ".creation-entry-test-"));
  try {
    const output = path.join(dir, "creation.json");
    for (let run = 0; run < 2; run++) {
      const result = await buildFile(fixture("basic.tsx"), {
        target: "creation", output, projectRoot: fixturesDir,
      });
      equal(result.exitCode, 0);
      deepEqual(result.errors, []);
      const actual = fs.readFileSync(output, { encoding: "utf8" });
      equal(actual, expectedBytes("basic.expected.json"));
      if (result.creation) {
        deepEqual(Object.keys(result.creation), [
          "schemaVersion", "kind", "creationVersion", "creationId", "definition", "authoring",
        ]);
        equal(result.document, result.creation.definition);
      } else {
        ok(false, "BuildResult.creation should be present for creation target");
      }
    }
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("exported ownership supplies creationId while root key remains in definition", async () => {
  const dir = fs.mkdtempSync(path.join(fixturesDir, ".creation-ownership-test-"));
  try {
    const output = path.join(dir, "creation.json");
    const result = await buildFile(fixture("ownership.tsx"), {
      target: "creation", output, projectRoot: fixturesDir,
    });
    equal(result.exitCode, 0);
    equal(result.creation?.creationId, "house");
    equal(result.creation?.definition.ownership.key, "house");
    equal(result.creation?.definition.slot.key, "room");
    equal(result.creation?.authoring.ownershipSource, "entry-export");
    ok(!("authoring" in result.creation!.definition));
    equal(fs.readFileSync(output, { encoding: "utf8" }), expectedBytes("ownership.expected.json"));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("root and ownership IDs reject missing, empty, overlong, and control-character candidates", async () => {
  for (const entry of ["no-key.tsx", "root-empty.tsx", "too-long.tsx", "control.tsx",
    "ownership-empty.tsx", "ownership-long.tsx", "ownership-control.tsx"]) {
    const result = await buildFile(fixture(entry), { target: "creation" });
    equal(result.exitCode, 1, entry);
    match(result.errors.join("\n"), /CREATION_ID_INVALID: creationId must be 1\.\.128 characters and contain no control characters/);
  }
});

test("target parsing reports missing and unsupported values", () => {
  equal(parseArgs(["build", "entry.tsx", "-o", "out.json", "--target"]).error, "--target requires a value");
  equal(parseArgs(["build", "entry.tsx", "-o", "out.json", "--target="]).error, "--target requires a value");
  equal(parseArgs(["build", "entry.tsx", "-o", "out.json", "--target", "-o"]).error, "--target requires a value");
  equal(parseArgs(["build", "entry.tsx", "-o", "out.json", "--target", "--draft"]).error, "--target requires a value");
  equal(parseArgs(["build", "entry.tsx", "-o", "out.json", "--target", "other"]).error,
    "unsupported --target value: other");
});

test("invalid buildFile target and creation combinations fail before writing", async () => {
  const dir = fs.mkdtempSync(path.join(fixturesDir, ".creation-entry-invalid-test-"));
  try {
    const output = path.join(dir, "out.json");
    const invalid = await buildFile(fixture("basic.tsx"), {
      target: "other" as BuildOptions["target"], output,
    });
    equal(invalid.exitCode, 1);
    match(invalid.errors[0], /unsupported --target value: other/);
    ok(!fs.existsSync(output));

    const combinations: BuildOptions[] = [
      { target: "creation", draft: true, output },
      { target: "creation", bundle: true, catalog: "catalog.json", buildId: "r1", output },
      { target: "creation", catalog: "catalog.json", output },
      { target: "creation", buildId: "r1", output },
    ];
    const options = ["--draft", "--bundle", "--catalog", "--build-id"];
    for (let i = 0; i < combinations.length; i++) {
      const result = await buildFile(fixture("basic.tsx"), combinations[i]);
      equal(result.exitCode, 1);
      equal(result.errors[0], `--target creation cannot be combined with ${options[i]} in this version`);
      ok(!fs.existsSync(output));
    }
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("ordinary output retains marker-like names and values byte-for-byte", async () => {
  const dir = fs.mkdtempSync(path.join(fixturesDir, ".ordinary-output-test-"));
  try {
    const output = path.join(dir, "ordinary.json");
    const result = await buildFile(fixture("sentinels.tsx"), { output, projectRoot: fixturesDir });
    equal(result.exitCode, 0);
    equal(fs.readFileSync(output, { encoding: "utf8" }), expectedBytes("sentinels.expected.json"));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
