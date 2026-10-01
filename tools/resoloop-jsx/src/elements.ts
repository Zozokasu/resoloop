// Marker functions and prop types for resoloop-jsx.
//
// <Slot> and <Component> are never invoked at runtime: the JSX runtime
// (jsx-runtime.ts) recognizes them by reference equality inside
// createElement and turns them into plain element objects instead.
// They exist as real function values only so TypeScript can type-check
// JSX attributes against SlotProps / ComponentProps.

/** JSON-serializable value. Mirrors the C# side's JsonElement usage in
 *  ApplyComponentSpec.Fields / InitialFields. */
export type JsonValue =
  | string
  | number
  | boolean
  | null
  | JsonValue[]
  | { [key: string]: JsonValue };

/** Mirrors C# ApplySlotSpec.ManagedFields (position | rotation | scale). */
export type ManagedField = "position" | "rotation" | "scale";

/** Mirrors C# ApplySlotSpec.RelocationTransform (local | world). */
export type RelocationTransform = "local" | "world";

/** Props of <Slot>. Scalar fields mirror C# ApplySlotSpec; `key` is the
 *  ResoLoop stable key (ApplySlotSpec.Key) and is required at the type level.
 *  Use the "resoloop-jsx/draft" entry point to omit it while prototyping. */
export interface SlotProps {
  /** ApplySlotSpec.Name (required) */
  name: string;
  /** ApplySlotSpec.Key (required) */
  key: string;
  /** ApplySlotSpec.Parent — only meaningful on the root <Slot> */
  parent?: string;
  /** ApplySlotSpec.Position — exactly 3 numbers */
  position?: [number, number, number];
  /** ApplySlotSpec.Rotation — exactly 4 numbers (quaternion) */
  rotation?: [number, number, number, number];
  /** ApplySlotSpec.Scale — exactly 3 numbers */
  scale?: [number, number, number];
  /** ApplySlotSpec.ManagedFields */
  managedFields?: ManagedField[];
  /** ApplySlotSpec.PreserveWorldTransform */
  preserveWorldTransform?: boolean;
  /** ApplySlotSpec.MigrateFrom */
  migrateFrom?: string;
  /** ApplySlotSpec.RelocationTransform ("local" default) */
  relocationTransform?: RelocationTransform;
  /** ApplySlotSpec.RuntimeRelocatable */
  runtimeRelocatable?: boolean;
  /** JSX children: nested <Slot>/<Component> elements, arrays, fragments,
   *  or falsy values to skip. */
  children?: JsxChild;
}

/** Props of <Component>. Mirrors C# ApplyComponentSpec; `key` is required at
 *  the type level (use "resoloop-jsx/draft" to omit it). <Component> never
 *  takes children. */
export interface ComponentProps {
  /** ApplyComponentSpec.Type — fully-qualified Resonite Component type name */
  type: string;
  /** ApplyComponentSpec.Key (required) */
  key: string;
  /** ApplyComponentSpec.Fields */
  fields?: Record<string, JsonValue>;
  /** ApplyComponentSpec.MigrateFrom */
  migrateFrom?: string;
  /** ApplyComponentSpec.InitialFields */
  initialFields?: Record<string, JsonValue>;
  /** ApplyComponentSpec.IdentityFields */
  identityFields?: string[];
}

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
export type JsxElement = SlotElement | ComponentElement | FragmentElement | ScopeElement;

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

export function Component(_props: ComponentProps): JsxNode {
  throw new Error(
    "resoloop-jsx: <Component> is a compile-time marker and must not be invoked directly"
  );
}

export function Scope(_props: ScopeProps): JsxNode {
  throw new Error("resoloop-jsx: <Scope> is a compile-time marker");
}
