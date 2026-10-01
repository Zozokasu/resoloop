import { test } from "node:test";
import { deepEqual, equal, throws, ok } from "node:assert/strict";
import { evaluate, BuildError } from "../src/evaluate.js";
import { ref } from "../src/ref.js";
import { buildFile } from "../src/cli.js";
import { fileURLToPath } from "node:url";
import * as path from "node:path";

const slot = (key: string | undefined, children: any = []) => ({ kind: "slot", props: { key, name: key ?? "Draft", children } });
const component = (key: string, fields: any = {}) => ({ kind: "component", props: { key, type: "T", fields } });
const scope = (instanceKey: any, children: any) => ({ kind: "scope", props: { instanceKey, children } });
const error = (code: string) => (e: any) => e instanceof BuildError && e.code === code;

test("reusable functions have isolated keys and selectors; sibling insertion preserves them", async () => {
  const fixture = (name: string) => path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../../test/fixtures", `${name}.tsx`);
  const base = await buildFile(fixture("scoped"));
  const inserted = await buildFile(fixture("scoped-inserted"));
  equal(base.exitCode, 0, base.errors.join("\n"));
  equal(inserted.exitCode, 0, inserted.errors.join("\n"));
  deepEqual(base.document!.children, inserted.document!.children.slice(1));
  deepEqual(base.document!.components, inserted.document!.components);
  equal(base.document!.children[0].slot.key, "left::body");
  equal(base.document!.children[1].components![1].fields!.Member, "$member:right::state.Enabled");
  equal(base.document!.components[0].fields!.Target, "$component:left::state");
});

test("nested scopes resolve arrays, initialFields and aliases without mutating reused elements", () => {
  const part = { kind: "component", props: { key: "wire", type: "T",
    fields: { Links: ["$ref:wire", { Target: "$member:wire.Value" }] },
    initialFields: { Target: "$component:wire" }, migrateFrom: "old" } };
  const document = evaluate(slot("root", scope("a", slot("body", scope("b", part))))).document;
  equal(document.children[0].components![0].key, "a::b::wire");
  deepEqual(document.children[0].components![0].fields, { Links: ["$ref:a::b::wire", { Target: "$member:a::b::wire.Value" }] });
  equal(part.props.fields.Links[0], "$ref:wire");
  equal(document.children[0].components![0].initialFields!.Target, "$component:a::b::wire");
  equal(document.children[0].components![0].migrateFrom, "a::b::old");
  equal(ref.key("a", "b", "wire"), "a::b::wire");
});

test("local references never fall back to another scope or a global key", () => {
  throws(() => evaluate(slot("root", [component("target"), scope("a", component("wire", { Target: "$component:target" }))])), error("APPLY_SCOPE_INVALID"));
  throws(() => evaluate(slot("root", scope("a", component("wire", { Target: "$member:missing.Value" })))), error("APPLY_SCOPE_INVALID"));
  throws(() => evaluate(slot("root", component("wire", { Target: "$slot:missing::body" }))), error("APPLY_SCOPE_INVALID"));
});

test("qualified cross-scope references are deliberate absolute selectors", () => {
  const document = evaluate(slot("root", [scope("a", component("state")), scope("b", component("wire", { Target: "$component:a::state" }))])).document;
  equal(document.components[1].fields!.Target, "$component:a::state");
});

test("duplicate instances, local keys, flattened legacy collisions and cross-kind collisions fail", () => {
  for (const children of [
    [scope("a", component("x")), scope("a", component("y"))],
    scope("a", [component("x"), component("x")]),
    [component("a::x"), scope("a", component("x"))],
    scope("a", slot("x", component("x"))),
  ]) throws(() => evaluate(slot("root", children)), error("DUPLICATE_KEY"));
});

test("reserved colon, invalid instance segments and missing scoped keys fail even in draft", () => {
  for (const key of ["", " ", "a::b", "a:", ":a", null, 3])
    throws(() => evaluate(slot("root", scope(key, component("x")))), error("APPLY_SCOPE_INVALID"));
  throws(() => evaluate(slot("root", scope("a", component("x:y")))), error("APPLY_SCOPE_INVALID"));
  throws(() => evaluate(slot("root", scope("a", slot(undefined))), { draft: true }), error("APPLY_SCOPE_INVALID"));
});

test("legacy explicit keys stay literal; index-generated draft output is guarded", () => {
  equal(evaluate(slot("old:key::literal")).document.slot.key, "old:key::literal");
  ok(evaluate(slot(undefined), { draft: true }).document.$draftKeys);
  equal(evaluate(slot("root"), { draft: true }).document.$draftKeys, undefined);
});
