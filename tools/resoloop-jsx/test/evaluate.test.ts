// Whitebox unit tests for the evaluator: element objects are constructed
// directly (the internal `{kind, props}` shape), so no CLI / tsc pipeline
// is involved.

import { test, describe } from "node:test";
import { deepEqual, equal, match, ok, throws } from "node:assert/strict";
import { evaluate, BuildError } from "../src/evaluate.js";

const slot = (props: Record<string, any>) => ({ kind: "slot", props });
const component = (props: Record<string, any>) => ({ kind: "component", props });
const fragment = (children: any) => ({ kind: "fragment", props: { children } });

function buildError(code: string) {
  return (err: any) => err instanceof BuildError && err.code === code;
}

describe("key rules", () => {
  test("explicit keys are used as-is", () => {
    const { document } = evaluate(
      slot({ name: "Root", key: "my-root", children: [slot({ name: "Kid", key: "kid-1" })] })
    );
    equal(document.ownership.key, "my-root");
    equal(document.slot.key, "my-root");
    equal(document.children[0].slot.key, "kid-1");
  });

  test("missing key without --draft fails with EXPLICIT_KEY_REQUIRED", () => {
    const err = (() => {
      try {
        evaluate(slot({ name: "R", key: "r", children: [slot({ name: "NoKey" })] }));
      } catch (e) {
        return e as BuildError;
      }
      return undefined;
    })();
    ok(err instanceof BuildError);
    equal(err!.code, "EXPLICIT_KEY_REQUIRED");
    match(err!.message, /slot "NoKey"/);
    match(err!.message, /\$\.children\[0\]/);
  });

  test("component without key also fails without --draft", () => {
    throws(
      () => evaluate(slot({ name: "R", key: "r", children: [component({ type: "T" })] })),
      buildError("EXPLICIT_KEY_REQUIRED")
    );
  });

  test("duplicate slot keys are rejected globally, not per parent", () => {
    const root = slot({
      name: "R",
      key: "r",
      children: [
        slot({ name: "A", key: "a", children: [slot({ name: "X", key: "dup" })] }),
        slot({ name: "B", key: "b", children: [slot({ name: "Y", key: "dup" })] }),
      ],
    });
    const err = (() => {
      try {
        evaluate(root);
      } catch (e) {
        return e as BuildError;
      }
      return undefined;
    })();
    ok(err instanceof BuildError);
    equal(err!.code, "DUPLICATE_KEY");
    match(err!.message, /"dup"/);
    match(err!.message, /\$\.children\[0\]\.children\[0\]/);
    match(err!.message, /\$\.children\[1\]\.children\[0\]/);
  });

  test("duplicate component keys are rejected globally", () => {
    throws(
      () =>
        evaluate(
          slot({
            name: "R",
            key: "r",
            children: [
              component({ type: "T", key: "c" }),
              slot({ name: "Inner", key: "i", children: [component({ type: "U", key: "c" })] }),
            ],
          })
        ),
      buildError("DUPLICATE_KEY")
    );
  });

  test("slot and component key namespaces are independent", () => {
    const { document } = evaluate(
      slot({
        name: "R",
        key: "shared",
        children: [component({ type: "T", key: "shared" })],
      })
    );
    equal(document.slot.key, "shared");
    equal(document.components[0].key, "shared");
  });
});

describe("draft key generation", () => {
  test("root without key gets root/<slug>#0", () => {
    const { document, warnings } = evaluate(slot({ name: "My Root" }), { draft: true });
    equal(document.slot.key, "root/my_root#0");
    equal(document.ownership.key, "root/my_root#0");
    equal(warnings.length, 1);
    match(warnings[0], /warning: generated key "root\/my_root#0" for slot "My Root" \(--draft\)/);
  });

  test("nested keys derive from parent key, name slug and same-kind rank", () => {
    const root = slot({
      name: "R",
      key: "r",
      children: [
        component({ type: "FrooxEngine.Grabbable" }),
        slot({ name: "Child One" }),
        slot({ name: "Child Two" }),
        component({ type: "FrooxEngine.Spinner" }),
      ],
    });
    const { document, warnings } = evaluate(root, { draft: true });
    equal(document.components[0].key, "r/frooxengine_grabbable#0");
    equal(document.children[0].slot.key, "r/child_one#0");
    equal(document.children[1].slot.key, "r/child_two#1");
    equal(document.components[1].key, "r/frooxengine_spinner#1");
    equal(warnings.length, 4);
  });

  test("generated keys still participate in duplicate detection", () => {
    // The first child generates "r/child#0"; the second declares it explicitly.
    const root = slot({
      name: "R",
      key: "r",
      children: [
        slot({ name: "Child" }),
        slot({ name: "Other", key: "r/child#0" }),
      ],
    });
    const err = (() => {
      try {
        evaluate(root, { draft: true });
      } catch (e) {
        return e as BuildError;
      }
      return undefined;
    })();
    // "r/child#0" generated for the second child collides with the first's explicit key.
    ok(err instanceof BuildError);
    equal(err!.code, "DUPLICATE_KEY");
  });
});

describe("shape checks", () => {
  test("root must resolve to exactly one slot", () => {
    throws(() => evaluate(null), buildError("ROOT_MUST_BE_SINGLE_SLOT"));
    throws(() => evaluate([]), buildError("ROOT_MUST_BE_SINGLE_SLOT"));
    throws(
      () => evaluate([slot({ name: "A", key: "a" }), slot({ name: "B", key: "b" })]),
      buildError("ROOT_MUST_BE_SINGLE_SLOT")
    );
    throws(
      () => evaluate(component({ type: "T", key: "c" })),
      buildError("ROOT_MUST_BE_SINGLE_SLOT")
    );
  });

  test("fragment/array roots of exactly one slot are accepted", () => {
    const { document } = evaluate([fragment([slot({ name: "R", key: "r" })])]);
    equal(document.slot.key, "r");
  });

  test("non-element children are rejected with INVALID_CHILD", () => {
    throws(
      () => evaluate(slot({ name: "R", key: "r", children: ["oops"] })),
      (err: any) => err instanceof BuildError && err.code === "INVALID_CHILD" && /string/.test(err.message)
    );
  });

  test("fragments flatten in place; falsy children are dropped", () => {
    const { document } = evaluate(
      slot({
        name: "R",
        key: "r",
        children: [
          fragment([slot({ name: "A", key: "a" }), null, slot({ name: "B", key: "b" })]),
          false,
          undefined,
        ],
      })
    );
    deepEqual(
      document.children.map((c) => c.slot.key),
      ["a", "b"]
    );
  });

  test("duplicate sibling slot names are rejected independently of keys", () => {
    throws(
      () =>
        evaluate(
          slot({
            name: "R",
            key: "r",
            children: [slot({ name: "Same", key: "k1" }), slot({ name: "Same", key: "k2" })],
          })
        ),
      (err: any) =>
        err instanceof BuildError &&
        err.code === "DUPLICATE_SIBLING_NAME" &&
        /Same/.test(err.message)
    );
  });

  test("same name in different parents is fine", () => {
    const { document } = evaluate(
      slot({
        name: "Same",
        key: "r",
        children: [slot({ name: "Same", key: "child" })],
      })
    );
    equal(document.children[0].slot.name, "Same");
  });

  test("missing/blank slot name fails with SLOT_NAME_MISSING even in draft", () => {
    throws(() => evaluate(slot({ name: "  ", key: "r" })), buildError("SLOT_NAME_MISSING"));
    throws(() => evaluate(slot({ name: "  ", key: "r" }), { draft: true }), buildError("SLOT_NAME_MISSING"));
    throws(() => evaluate(slot({ key: "r" })), buildError("SLOT_NAME_MISSING"));
  });

  test("component without type fails with COMPONENT_TYPE_MISSING", () => {
    throws(
      () => evaluate(slot({ name: "R", key: "r", children: [component({ key: "c" })] })),
      buildError("COMPONENT_TYPE_MISSING")
    );
  });

  test("undefined props are omitted; defined props pass through", () => {
    const { document } = evaluate(
      slot({
        name: "R",
        key: "r",
        parent: "Root",
        position: [1, 2, 3],
        rotation: [0, 0, 0, 1],
        managedFields: ["position"],
        preserveWorldTransform: true,
        migrateFrom: "old-root",
        relocationTransform: "world",
        runtimeRelocatable: true,
        children: [slot({ name: "C", key: "c" })],
      })
    );
    deepEqual(document.slot, {
      name: "R",
      key: "r",
      parent: "Root",
      position: [1, 2, 3],
      rotation: [0, 0, 0, 1],
      managedFields: ["position"],
      preserveWorldTransform: true,
      migrateFrom: "old-root",
      relocationTransform: "world",
      runtimeRelocatable: true,
    });
    // `parent` is never emitted on nested slots.
    deepEqual(document.children[0].slot, { name: "C", key: "c" });
    // structural props never leak into the emitted spec
    ok(!("children" in document.slot));
  });
});
