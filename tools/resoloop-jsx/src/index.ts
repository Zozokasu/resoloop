// Public entry point ("resoloop-jsx").

/** Optional C#-generated catalog declarations augment this registry. */
export interface CatalogComponentRegistry {}

export { Slot, Component, Scope, Field } from "./elements.js";
export { Fragment } from "./jsx-runtime.js";
export { ref } from "./ref.js";
export { BuildError, evaluate } from "./evaluate.js";

/** @experimental Define typed Object bodies and definition-owned handles. */
export { defineObject } from "./object-definition.js";
/** @experimental Bind fields and references to runtime catalog declarations.
 * Static types use catalog metadata; parent, placement, and boundary checks run during evaluation.
 */
export { componentFields } from "./bound-field.js";

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

// Experimental type-only exports used by catalog declarations and runtime tables.
export type {
  FieldRef,
  UncheckedReference,
  ScalarRepresentation,
  ScalarValue,
  RepresentationOf,
  InstanceHandle,
} from "./object-refs.js";

/** @experimental Object definition call and instance API. */
export type { ObjectDefinition } from "./object-definition.js";

export type {
  CatalogRuntimeTable,
  CatalogMemberRuntime,
} from "./catalog-runtime.js";
