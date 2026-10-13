import { test } from "node:test";
import { deepEqual, equal, match, ok, throws } from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { fileURLToPath } from "node:url";
import { BuildError, evaluate } from "../src/evaluate.js";
import { buildFile } from "../src/cli.js";

const here = path.dirname(fileURLToPath(import.meta.url));
const packageRoot = path.resolve(here, "..", "..");
const fixturesDir = path.join(packageRoot, "test", "fixtures");
const fixture = (...parts: string[]) => path.join(fixturesDir, "creation-keys", ...parts);
const slot = (props: Record<string, unknown>, children: unknown[] = []) => ({ kind: "slot", props: { ...props, children } });
const component = (props: Record<string, unknown>) => ({ kind: "component", props });
const scope = (instanceKey: string, children: unknown[]) => ({ kind: "scope", props: { instanceKey, children } });
const code = (expected: string) => (error: unknown) => error instanceof BuildError && error.code === expected;
const creationOptions = { creation: true, ownership: { key: "owner" } };

test("creation fixture matches handwritten envelope and generates positional keys without draft metadata", async () => {
  const dir = fs.mkdtempSync(path.join(fixturesDir, ".creation-keys-test-"));
  try {
    const output = path.join(dir, "creation.json");
    const result = await buildFile(fixture("main.tsx"), {
      target: "creation", output, projectRoot: fixturesDir,
    });
    equal(result.exitCode, 0, result.errors.join("\n"));
    equal(result.warnings.length, 0);
    if (!result.creation) throw new Error("creation envelope should be present");
    equal(result.creation.creationId, "room");
    equal(result.creation.definition.slot.key, "root/room#0");
    ok(!("$draftKeys" in result.creation.definition));
    ok(!("authoring" in result.creation.definition));
    const expected = JSON.parse(fs.readFileSync(fixture("expected.handwritten.json"), { encoding: "utf8" }));
    expected.authoring.projectRoot = fixturesDir;
    equal(fs.readFileSync(output, { encoding: "utf8" }), JSON.stringify(expected, null, 2) + "\n");
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test("creation auto key is not an ownership ID candidate", () => {
  throws(() => evaluate(slot({ name: "Room" }), { creation: true }), code("CREATION_ID_INVALID"));
});

test("ownership key value errors use CREATION_ID_INVALID only in creation mode", () => {
  const root = slot({ key: "room", name: "Room" });
  for (const key of [1, null, undefined]) {
    throws(() => evaluate(root, { creation: true, ownership: { key } }), code("CREATION_ID_INVALID"));
    throws(() => evaluate(root, { ownership: { key } }), /ownership export must be an object containing one non-empty string key/);
  }
});

test("explicit duplicate slot and component keys remain rejected during creation", () => {
  throws(() => evaluate(slot({ key: "room", name: "Room" }, [
    slot({ key: "same", name: "Lamp" }), slot({ key: "same", name: "Lamp" }),
  ]), creationOptions), code("DUPLICATE_KEY"));
  throws(() => evaluate(slot({ key: "room", name: "Room" }, [
    component({ key: "same", type: "Probe" }), component({ key: "same", type: "Probe" }),
  ]), creationOptions), code("DUPLICATE_KEY"));
});

test("ordinary and draft key/name checks retain their existing behavior", () => {
  throws(() => evaluate(slot({ key: "room", name: "Room" }, [
    slot({ key: "left", name: "Lamp" }), slot({ key: "right", name: "Lamp" }),
  ])), code("DUPLICATE_SIBLING_NAME"));
  throws(() => evaluate(slot({ key: "room", name: "Room" }, [component({ type: "Probe" })])), code("EXPLICIT_KEY_REQUIRED"));
  const draft = evaluate(slot({ key: "room", name: "Room" }, [component({ type: "Probe" })]), { draft: true });
  equal(draft.document.$draftKeys, true);
  equal(draft.warnings.length, 1);
  match(draft.warnings[0], /generated key "room\/probe#0"/);
});

test("creation preserves marker-like name and field strings without adding draft markers", () => {
  const document = evaluate(slot({ key: "room", name: "$draftKeys-like" }, [
    component({ type: "Probe", fields: { marker: "$draftKeys-like" } }),
  ]), creationOptions).document;
  equal(document.slot.name, "$draftKeys-like");
  equal(document.components[0].fields?.marker, "$draftKeys-like");
  ok(!("$draftKeys" in document));
});

test("creation permits omitted keys under Scope and keeps explicit local keys scoped", () => {
  const document = evaluate(slot({ key: "root", name: "Room" }, [scope("part", [
    slot({ name: "Child" }, [component({ type: "Probe" })]),
    slot({ key: "named", name: "Named" }),
  ])]), creationOptions).document;
  equal(document.children[0].slot.key, "root/child#0");
  equal(document.children[0].components?.[0].key, "root/child#0/probe#0");
  equal(document.children[1].slot.key, "part::named");
  throws(() => evaluate(slot({ key: "root", name: "Room" }, [scope("part", [
    slot({ key: "named", name: "One" }), slot({ key: "named", name: "Two" }),
  ])]), creationOptions), code("DUPLICATE_KEY"));
});

test("creation positional keys shift when a sibling is inserted, including scoped siblings", () => {
  const keys = (names: string[]) => evaluate(slot({ key: "root", name: "Room" }, [
    scope("part", names.map(name => slot({ name }))),
  ]), creationOptions).document.children.map(child => child.slot.key);
  deepEqual(keys(["Lamp", "Lamp"]), ["root/lamp#0", "root/lamp#1"]);
  deepEqual(keys(["Beam", "Lamp", "Lamp"]), ["root/beam#0", "root/lamp#1", "root/lamp#2"]);
});

test("creation rejects blank root and ownership ID candidates", () => {
  throws(() => evaluate(slot({ key: "  ", name: "Room" }), { creation: true }), code("CREATION_ID_INVALID"));
  throws(() => evaluate(slot({ key: "room", name: "Room" }), { creation: true, ownership: { key: "  " } }), code("CREATION_ID_INVALID"));
});
