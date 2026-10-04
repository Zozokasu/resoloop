// Generated file; do not edit by hand.
// Regenerate: dotnet run --project tools/RLoop.ContractGen -- tools/resoloop-jsx/src/generated

import type { ApplySlotSpec, ApplyComponentSpec } from "./apply-types.js";

export interface CopyHelpers {
  assertFiniteNumbers(value: unknown, path: string): void;
  scopeValue(value: any, scope: string): any;
}

export const SLOT_SCALAR_PROPS = ["position", "rotation", "scale", "managedFields", "preserveWorldTransform", "migrateFrom", "relocationTransform", "runtimeRelocatable", "tag"] as const;

export function copySlot(props: Record<string, any>, key: string, path: string, scope: string, isRoot: boolean, helpers: CopyHelpers): ApplySlotSpec {
  const spec: ApplySlotSpec = { name: props.name, key };
  if (isRoot && props.parent !== undefined) {
    spec.parent = props.parent;
  }
  if (props.position !== undefined) {
    helpers.assertFiniteNumbers(props.position, `${path}.position`);
    spec.position = props.position;
  }
  if (props.rotation !== undefined) {
    helpers.assertFiniteNumbers(props.rotation, `${path}.rotation`);
    spec.rotation = props.rotation;
  }
  if (props.scale !== undefined) {
    helpers.assertFiniteNumbers(props.scale, `${path}.scale`);
    spec.scale = props.scale;
  }
  if (props.managedFields !== undefined) {
    helpers.assertFiniteNumbers(props.managedFields, `${path}.managedFields`);
    spec.managedFields = props.managedFields;
  }
  if (props.preserveWorldTransform !== undefined) {
    helpers.assertFiniteNumbers(props.preserveWorldTransform, `${path}.preserveWorldTransform`);
    spec.preserveWorldTransform = props.preserveWorldTransform;
  }
  if (props.migrateFrom !== undefined) {
    helpers.assertFiniteNumbers(props.migrateFrom, `${path}.migrateFrom`);
    spec.migrateFrom = scope && typeof props.migrateFrom === "string" ? helpers.scopeValue(`$slot:${props.migrateFrom}`, scope).slice(6) : props.migrateFrom;
  }
  if (props.relocationTransform !== undefined) {
    helpers.assertFiniteNumbers(props.relocationTransform, `${path}.relocationTransform`);
    spec.relocationTransform = props.relocationTransform;
  }
  if (props.runtimeRelocatable !== undefined) {
    helpers.assertFiniteNumbers(props.runtimeRelocatable, `${path}.runtimeRelocatable`);
    spec.runtimeRelocatable = props.runtimeRelocatable;
  }
  if (props.tag !== undefined) {
    spec.tag = props.tag;
  }
  return spec;
}

export const COMPONENT_SCALAR_PROPS = ["fields", "migrateFrom", "initialFields", "identityFields", "propertyModes", "fieldAliases"] as const;

export function copyComponent(props: Record<string, any>, key: string, path: string, scope: string, isRoot: boolean, helpers: CopyHelpers): ApplyComponentSpec {
  const spec: ApplyComponentSpec = { type: props.type, key };
  if (props.fields !== undefined) {
    helpers.assertFiniteNumbers(props.fields, `${path}.fields`);
    spec.fields = helpers.scopeValue(props.fields, scope);
  }
  if (props.migrateFrom !== undefined) {
    spec.migrateFrom = scope && typeof props.migrateFrom === "string" ? helpers.scopeValue(`$component:${props.migrateFrom}`, scope).slice(11) : props.migrateFrom;
  }
  if (props.initialFields !== undefined) {
    helpers.assertFiniteNumbers(props.initialFields, `${path}.initialFields`);
    spec.initialFields = helpers.scopeValue(props.initialFields, scope);
  }
  if (props.identityFields !== undefined) {
    spec.identityFields = props.identityFields;
  }
  if (props.propertyModes !== undefined) {
    helpers.assertFiniteNumbers(props.propertyModes, `${path}.propertyModes`);
    spec.propertyModes = props.propertyModes;
  }
  if (props.fieldAliases !== undefined) {
    spec.fieldAliases = props.fieldAliases;
  }
  return spec;
}
