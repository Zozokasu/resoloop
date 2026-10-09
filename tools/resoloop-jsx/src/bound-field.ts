import type { CatalogComponentRegistry } from "./index.js";
import { catalogMember, verifyRuntimeTable, type CatalogMemberRuntime, type CatalogRuntimeTable } from "./catalog-runtime.js";
import { BuildError } from "./evaluate.js";
import type { FieldProps, JsxNode } from "./elements.js";
import { markBuildError, captureElement } from "./source-map.js";
import { isReferenceToken, targetReference, type FieldRef, type ScalarRepresentation } from "./object-refs.js";

type Registry<T extends keyof CatalogComponentRegistry & string> = CatalogComponentRegistry[T];
type Members<T extends keyof CatalogComponentRegistry & string> =
  Registry<T> extends { members: infer M } ? M : never;
type Metadata<T extends keyof CatalogComponentRegistry & string> =
  Registry<T> extends { memberMetadata: infer M } ? M : never;
type Complete<T extends keyof CatalogComponentRegistry & string> =
  Registry<T> extends { membersComplete: infer C } ? C : false;
type Names<T extends keyof CatalogComponentRegistry & string> = Complete<T> extends true
  ? keyof Members<T> & string : string;
type Value<T extends keyof CatalogComponentRegistry & string, K extends string> =
  K extends keyof Members<T> ? Members<T>[K] : import("./elements.js").JsonValue;
type RefNames<T extends keyof CatalogComponentRegistry & string> = {
  [K in keyof Metadata<T> & string]: Metadata<T>[K] extends {
    kind: "field"; confirmed: true; scalar: ScalarRepresentation; fieldInterface: string;
  } ? K : never
}[keyof Metadata<T> & string];
type Rep<T extends keyof CatalogComponentRegistry & string, K extends string> =
  K extends keyof Metadata<T> ? Metadata<T>[K] extends { scalar: infer R } ? R : never : never;
type RefValue<R> = R extends "boolean" ? boolean : R extends "string" ? string :
  R extends "int32" | "single" ? number : never;
type Policy<V> =
  | { mode?: "config" | "initial"; value: V }
  | { mode: "runtime" | "driver-owned"; value?: V };

export interface BoundFieldMetadata {
  readonly type: string;
  readonly member: string;
  readonly descriptor: CatalogMemberRuntime;
  readonly catalogHash: string;
}

const metadata = new WeakMap<object, BoundFieldMetadata>();
function unavailable(type: string, member: string): never {
  throw new BuildError("APPLY_CATALOG_UNAVAILABLE",
    `Type '${type}', member '${member}': runtime catalog evidence is missing or unconfirmed.`);
}

function canonicalType(table: CatalogRuntimeTable, type: string, member: string): string {
  if (!Object.hasOwn(table.types, type)) return unavailable(type, member);
  const requested = table.types[type];
  if (!requested.confirmed || !Object.hasOwn(table.types, requested.canonicalType)) return unavailable(type, member);
  const canonical = table.types[requested.canonicalType];
  if (!canonical.confirmed || canonical.canonicalType !== requested.canonicalType) return unavailable(type, member);
  return canonical.canonicalType;
}

function observedKind(value: unknown): string {
  if (value === null) return "null";
  if (Array.isArray(value)) return "array";
  return typeof value;
}

function validateScalarValue(value: unknown, representation: ScalarRepresentation, element: object): void {
  if (isReferenceToken(value)) return;
  const valid = representation === "boolean" ? typeof value === "boolean" :
    representation === "string" ? typeof value === "string" : typeof value === "number";
  if (valid) return;
  const error = new BuildError("APPLY_OBJECT_TYPE_MISMATCH",
    `Object reference type mismatch: expected 'value:${representation}', observed 'value:${observedKind(value)}'.`);
  markBuildError(error, element, "value");
  throw error;
}

/** @experimental Catalog-bound component field construction is experimental. */
export interface BoundComponentFields<T extends keyof CatalogComponentRegistry & string> {
  readonly type: T;
  Field<const K extends Names<T>>(
    props: Omit<FieldProps, "name" | "value" | "mode"> & { name: K } & Policy<Value<T, NoInfer<K>>>
  ): JsxNode;
  ref<const K extends RefNames<T>>(
    componentKey: string, member: K
  ): FieldRef<RefValue<Rep<T, K>>, Extract<Rep<T, K>, ScalarRepresentation>>;
}

/** @experimental Bind JSX Field creation and typed references to catalog evidence. */
export function componentFields<const T extends keyof CatalogComponentRegistry & string>(
  type: T,
  catalog: CatalogRuntimeTable,
): BoundComponentFields<T> {
  if (catalog === null || typeof catalog !== "object") return unavailable(type, "<type>");
  const table = verifyRuntimeTable(catalog);
  const canonical = canonicalType(table, type, "<type>");
  function createField<const K extends Names<T>>(
    props: Omit<FieldProps, "name" | "value" | "mode"> & { name: K } & Policy<Value<T, NoInfer<K>>>
  ): JsxNode {
    const descriptor = catalogMember(table, type, props.name);
    const fieldProps = { ...props, name: props.name } as unknown as FieldProps;
    const captured = { kind: "field", props: fieldProps };
    captureElement(captured);
    if (props.value !== undefined && descriptor.scalar !== null)
      validateScalarValue(props.value, descriptor.scalar, captured);
    metadata.set(captured, { type: canonical, member: props.name, descriptor, catalogHash: table.contentHash });
    return captured as JsxNode;
  }
  return Object.freeze({
    type,
    Field: createField,
    ref<const K extends RefNames<T>>(componentKey: string, member: K) {
      const descriptor = catalogMember(table, type, member);
      if (descriptor.kind !== "field" || descriptor.scalar === null || descriptor.fieldInterface === null)
        return unavailable(type, member);
      return targetReference(componentKey, member, canonical,
        { scalar: descriptor.scalar, fieldInterface: descriptor.fieldInterface }, table.contentHash) as
        FieldRef<RefValue<Rep<T, K>>, Extract<Rep<T, K>, ScalarRepresentation>>;
    },
  });
}

export function boundFieldMetadata(field: object): BoundFieldMetadata | undefined {
  return metadata.get(field);
}

export function copyBoundFieldMetadata(from: object, to: object): void {
  const value = metadata.get(from);
  if (value) metadata.set(to, value);
}

export function checkBoundFieldParent(
  element: object,
  parentType: string,
  catalogs: readonly CatalogRuntimeTable[],
): void {
  const bound = metadata.get(element);
  if (!bound) return;
  const candidate = catalogs.find(table => table.contentHash === bound.catalogHash);
  if (!candidate) return unavailable(bound.type, bound.member);
  const table = verifyRuntimeTable(candidate);
  const observed = canonicalType(table, parentType, bound.member);
  if (observed === bound.type) return;
  const error = new BuildError("APPLY_FIELD_TYPE_MISMATCH",
    `Bound Field requires Component '${bound.type}', observed '${observed}'.`);
  markBuildError(error, element, "name");
  throw error;
}
