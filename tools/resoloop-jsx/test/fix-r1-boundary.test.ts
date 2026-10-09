import { test } from "node:test";
import { equal } from "node:assert/strict";
import { collectObjects, validateObjectBoundaries } from "../src/object-expand.js";
import { validateObjectExports } from "../src/object-boundary.js";
import { defineObject } from "../src/object-definition.js";
import { exportReference } from "../src/object-refs.js";
import { BuildError } from "../src/evaluate.js";
import { objectTestRuntimeTable } from "./object-test-helpers.js";

const slot = (key: string, children?: unknown) => ({ kind: "slot", props: { key, name: key, children } }) as any;
const component = (key: string, fields: Record<string, unknown> = {}) =>
  ({ kind: "component", props: { key, type: "Synthetic.Device", fields } }) as any;

function prepare(root: unknown): unknown {
  const context = collectObjects(root, [objectTestRuntimeTable()]);
  validateObjectExports(context);
  validateObjectBoundaries(context);
  return context.root;
}

function assertPrivate(selector: string, key: string, declareKey = true): void {
  let caught: unknown;
  try { prepare(slot("page", [object("desk", declareKey ? key : "light"), component("reader", { Link: selector })])); }
  catch (error) { caught = error; }
  if (!(caught instanceof BuildError)) throw new Error(`Expected BuildError for ${JSON.stringify(selector)}`);
  equal(caught.code, "APPLY_OBJECT_PRIVATE_REFERENCE");
}

function object(id: string, key = "light"): any {
  const definition = defineObject<{}>()({
    exports: {},
    body: () => slot("root", [component(key, { Enabled: true })]) as never,
  });
  return definition({ handle: definition.instance(id) });
}

test("Object privacy parsing matches Core for line terminators and control characters", () => {
  for (const separator of ["\n", "\r", "\u2028", "\u2029", "\t", "\0", "\u001b", "\u007f", "."]) {
    const key = `li${separator}ght`;
    assertPrivate(`$member:desk::${key}.Enabled`, key);
  }
  assertPrivate("$member:desk::li:ght.Enabled", "li:ght", false);
});

test("ordinary public exports and references outside an Object remain accepted", () => {
  const key = "li\nght";
  const definition = defineObject<{}>()({
    exports: { enabled: `$member:${key}.Enabled` },
    body: () => slot("root", [component(key, { Enabled: true })]) as never,
  });
  const handle = definition.instance("desk");
  const producer = definition({ handle });
  const consumer = component("reader", { Target: exportReference(handle, "enabled") });
  const result = prepare(slot("page", [producer, consumer])) as any;
  equal(result.props.children[1].props.fields.Target, `$member:desk::${key}.Enabled`);

  const local = defineObject<{}>()({ exports: {}, body: () => slot("root", [
    component(key, { Enabled: true }), component("reader", { Link: `$member:${key}.Enabled` }),
  ]) as never });
  const internal = prepare(slot("page", [local({ handle: local.instance("desk") })])) as any;
  equal(internal.props.children[0].props.body.props.children[1].props.fields.Link, `$member:desk::${key}.Enabled`);

  const outside = prepare(slot("page", [object("desk"), component("reader", {
    Link: "$member:other::li\nght.Enabled",
  })])) as any;
  equal(outside.props.children[1].props.fields.Link, "$member:other::li\nght.Enabled");
});
