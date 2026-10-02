// Draft entry point ("resoloop-jsx/draft").
//
// Exports the exact same runtime functions as the main entry point, but with
// `key` optional in the prop types so prototypes can omit keys while a tree
// is being iterated on. Omitted keys are still rejected at evaluation time
// (EXPLICIT_KEY_REQUIRED) unless the build runs with --draft, in which case a
// legacy index-derived key is generated with a warning and a source guard
// that blocks validate/apply. Scope always requires explicit local keys.

import { Slot as SlotImpl, Component as ComponentImpl } from "./elements.js";
import type { SlotProps, ComponentProps, JsxNode } from "./elements.js";

export { Fragment } from "./jsx-runtime.js";
export { Scope, Field } from "./elements.js";
export { ref } from "./ref.js";
export { BuildError, evaluate } from "./evaluate.js";

/** SlotProps under the draft entry: identical except `key` is optional. */
export type DraftSlotProps = Omit<SlotProps, "key"> & { key?: string };

/** ComponentProps under the draft entry: identical except `key` is optional. */
export type DraftComponentProps<T extends string = string> = Omit<ComponentProps<T>, "key"> & { key?: string };

// Type-only casts. The exported values ARE the same function objects as the
// main entry point — jsx-runtime.ts recognizes markers by reference equality
// (type === Slot), so wrapping them in new functions would break detection.
export const Slot = SlotImpl as unknown as (props: DraftSlotProps) => JsxNode;
export const Component =
  ComponentImpl as unknown as <const T extends string>(props: DraftComponentProps<T>) => JsxNode;

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
  ApplyDocument,
  ApplyOwnershipSpec,
  ApplySlotSpec,
  ApplyComponentSpec,
  ApplyNodeSpec,
} from "./evaluate.js";
