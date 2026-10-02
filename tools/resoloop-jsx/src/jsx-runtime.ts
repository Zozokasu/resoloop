// JSX automatic-runtime entry point ("resoloop-jsx/jsx-runtime").
//
// TypeScript's `jsx: "react-jsx"` transform compiles
//   <Slot key="foo" name="Foo">...</Slot>
// into
//   _jsxs(Slot, { name: "Foo", children: ... }, "foo")
// i.e. the `key` attribute is passed as the THIRD positional argument and
// removed from the props object. createElement merges it back into props so
// the evaluator can uniformly read element.props.key.

import { captureElement, callComponent } from "./source-map.js";
import { Slot, Component, Scope, Field } from "./elements.js";
import type { JsxChild, JsxNode, JsxElement } from "./elements.js";

/** Marker for <>{...}</> fragments. Also recognized by reference equality;
 *  never actually invoked. */
export function Fragment(_props: { children?: JsxChild }): JsxNode {
  throw new Error(
    "resoloop-jsx: <Fragment> is a compile-time marker and must not be invoked directly"
  );
}

export function jsx(type: unknown, props: unknown, key?: string): JsxNode {
  return createElement(type, props, key);
}

export function jsxs(type: unknown, props: unknown, key?: string): JsxNode {
  return createElement(type, props, key);
}

function createElement(type: unknown, props: any, key?: string): JsxNode {
  const merged =
    key === undefined ? props ?? {} : { ...(props ?? {}), key };
  // Marker check must come before the generic function-component branch:
  // Slot/Component/Fragment are functions but must never be invoked.
  function marker(kind: string): JsxNode { const element = { kind, props: merged } as JsxNode; captureElement(element as object); return element; }
  if (type === Slot) return marker("slot");
  if (type === Component) return marker("component");
  if (type === Field) return marker("field");
  if (type === Fragment) return marker("fragment");
  if (type === Scope) return marker("scope");
  // User-defined function component: resolved eagerly at evaluation time.
  if (typeof type === "function") return callComponent(type as (props: any) => any, merged);
  throw new Error(
    `resoloop-jsx: unsupported JSX element type ${String(type)}`
  );
}

export namespace JSX {
  /** The type of a JSX element expression. */
  export type Element = JsxNode;
  /** No lowercase/intrinsic tags exist; declaring the empty interface makes
   *  <div> etc. a type error. */
  export interface IntrinsicElements {}
  /** `key` may additionally be written on elements whose props accept it. */
  export interface IntrinsicAttributes {
    key?: string;
  }
  /** Enables type-checking of JSX children into the `children` prop. */
  export interface ElementChildrenAttribute {
    children: {};
  }
}
