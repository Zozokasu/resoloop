// Marker functions and prop types for resoloop-jsx.
//
// <Slot> and <Component> are never invoked at runtime: the JSX runtime
// (jsx-runtime.ts) recognizes them by reference equality inside
// createElement and turns them into plain element objects instead.
// They exist as real function values only so TypeScript can type-check
// JSX attributes against SlotProps / ComponentProps.

import type { SlotScalarProps, ComponentScalarProps } from "./generated/apply-types.js";
import type { JsonValue } from "./generated/apply-types.js";
import type { CatalogComponentRegistry } from "./index.js";
export type { JsonValue, ManagedField, RelocationTransform } from "./generated/apply-types.js";

/** Generated scalar props plus JSX children, which are classified by the evaluator. */
export interface SlotProps extends SlotScalarProps {
  children?: JsxChild;
}
/** Catalog declarations supply value types; C# remains the semantic validator. */
export type ComponentFields<T extends string> = T extends keyof CatalogComponentRegistry
  ? CatalogComponentRegistry[T] extends { members: infer M; membersComplete: infer Complete }
    ? Partial<M> & (Complete extends true
        ? keyof M extends never ? Record<string, never> : unknown
        : Record<string, JsonValue>)
    : Record<string, JsonValue>
  : Record<string, JsonValue>;

export type ComponentProps<T extends string = string> =
  Omit<ComponentScalarProps, "type" | "fields" | "initialFields"> & {
    type: T;
    // Infer from `type` alone: invalid fields must not widen a known literal.
    fields?: ComponentFields<NoInfer<T>>;
    initialFields?: ComponentFields<NoInfer<T>>;
    children?: JsxChild;
  };
export interface FieldProps {
  name: string;
  value?: import("./generated/apply-types.js").JsonValue;
  mode?: "config" | "initial" | "runtime" | "driver-owned";
  key?: string;
}
export interface FieldElement { kind: "field"; props: FieldProps }

/** Internal element object produced by the JSX runtime for <Slot>. */
export interface SlotElement {
  kind: "slot";
  props: SlotProps;
}

/** Internal element object produced by the JSX runtime for <Component>. */
export interface ComponentElement {
  kind: "component";
  props: ComponentProps;
}

/** Internal element object produced by the JSX runtime for <Fragment>. */
export interface FragmentElement {
  kind: "fragment";
  props: { children?: JsxChild };
}

/** Explicit source-only boundary for a reusable subtree. */
export interface ScopeProps {
  /** Stable instance segment; no ':'. Keys inside are local to this scope. */
  instanceKey: string;
  children?: JsxChild;
}

export interface ScopeElement {
  kind: "scope";
  props: ScopeProps;
}

/** Any element object produced by the JSX runtime. */
export type JsxElement = SlotElement | ComponentElement | FragmentElement | ScopeElement | FieldElement;

/** Anything that may legally appear as a JSX child / function-component
 *  return value. Arrays may be nested arbitrarily; falsy values are dropped
 *  by the evaluator. */
export type JsxChild =
  | JsxElement
  | readonly JsxChild[]
  | boolean
  | null
  | undefined;

/** Result type of a JSX expression / function component. */
export type JsxNode = JsxChild;

export function Slot(_props: SlotProps): JsxNode {
  throw new Error(
    "resoloop-jsx: <Slot> is a compile-time marker and must not be invoked directly"
  );
}

export function Component<const T extends string>(_props: ComponentProps<T>): JsxNode {
  throw new Error(
    "resoloop-jsx: <Component> is a compile-time marker and must not be invoked directly"
  );
}

export function Scope(_props: ScopeProps): JsxNode {
  throw new Error("resoloop-jsx: <Scope> is a compile-time marker");
}

export function Field(_props: FieldProps): JsxNode {
  throw new Error("resoloop-jsx: <Field> is a compile-time marker");
}
