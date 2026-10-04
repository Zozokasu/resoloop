// Public entry point ("resoloop-jsx").

/** Optional C#-generated catalog declarations augment this registry. */
export interface CatalogComponentRegistry {}

export { Slot, Component, Scope, Field } from "./elements.js";
export { Fragment } from "./jsx-runtime.js";
export { ref } from "./ref.js";
export { BuildError, evaluate } from "./evaluate.js";

export type {
  JsonValue,
  ManagedField,
  RelocationTransform,
  SlotProps,
  ComponentProps,
  ComponentFields,
  FieldProps,
  FieldElement,
  ScopeProps,
  ScopeElement,
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
