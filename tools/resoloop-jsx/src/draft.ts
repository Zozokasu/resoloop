// Draft entry point ("resoloop-jsx/draft").
//
// Exports the exact same runtime functions as the main entry point, but with
// `key` optional in the prop types so prototypes can omit keys while a tree
// is being iterated on. Omitted keys are still rejected at evaluation time
// (EXPLICIT_KEY_REQUIRED) unless the build runs with --draft, in which case a
// legacy index-derived key is generated with a warning and a source guard
// that blocks validate/apply. Scope always requires explicit local keys.

import type { SlotProps, ComponentProps } from "./elements.js";

export { Fragment } from "./jsx-runtime.js";
export { Scope, Field } from "./elements.js";
export { ref } from "./ref.js";
export { BuildError, evaluate } from "./evaluate.js";

/** @experimental Define typed Object bodies and definition-owned handles. */
export { defineObject } from "./object-definition.js";
/** @experimental Bind fields and references to runtime catalog declarations.
 * Static types use catalog metadata; parent, placement, and boundary checks run during evaluation.
 */
export { componentFields } from "./bound-field.js";

/** Slot props under the draft entry are the main entry's slot props. */
export type DraftSlotProps = SlotProps;

/** Component props under the draft entry are the main entry's component props. */
export type DraftComponentProps<T extends string = string> = ComponentProps<T>;

export { Slot, Component } from "./elements.js";

export type {
  JsonValue,
  ScopeProps,
  FieldProps,
  FieldElement,
  ScopeElement,
  ManagedField,
  RelocationTransform,
  JsxElement,
  SlotElement,
  ComponentElement,
  FragmentElement,
  JsxChild,
  JsxNode,
} from "./elements.js";

export type {
  FieldRef,
  InstanceHandle,
  UncheckedReference,
} from "./object-refs.js";

export type { ObjectDefinition } from "./object-definition.js";

export type { CatalogRuntimeTable } from "./catalog-runtime.js";

export type {
  ApplyDocument,
  ApplyOwnershipSpec,
  ApplySlotSpec,
  ApplyComponentSpec,
  ApplyNodeSpec,
} from "./evaluate.js";
