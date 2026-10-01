// Generated file; do not edit by hand.
// Regenerate: dotnet run --project tools/RLoop.ContractGen -- tools/resoloop-jsx/src/generated

export type JsonValue = string | number | boolean | null | JsonValue[] | { [key: string]: JsonValue };
export type ManagedField = "position" | "rotation" | "scale";
export type RelocationTransform = "local" | "world";

export interface ApplyAssertionSpec {
  target: string;
  expected?: JsonValue;
  exists?: boolean;
  phase?: string;
  kind?: string;
  name?: string;
  componentType?: string;
  count?: number;
  delta?: number;
}

export interface ApplyAssetSpec {
  kind: string;
  source: string;
  options?: Record<string, JsonValue>;
}

export interface ApplyAuthoringSpec {
  projectRoot: string;
  source: string;
  ownershipSource?: string;
}

export interface ApplyCameraSpec {
  position: [number, number, number];
  target: [number, number, number];
  fieldOfView?: number;
  width?: number;
  height?: number;
  output?: string;
  representative?: boolean;
}

export interface ApplyComponentSpec {
  type: string;
  fields?: Record<string, JsonValue>;
  key?: string;
  migrateFrom?: string;
  initialFields?: Record<string, JsonValue>;
  identityFields?: string[];
}

export interface ApplyDocument {
  schemaVersion: "1";
  ownership: ApplyOwnershipSpec;
  slot: ApplySlotSpec;
  components: ApplyComponentSpec[];
  children: ApplyNodeSpec[];
  assets?: Record<string, ApplyAssetSpec>;
  cameras?: Record<string, ApplyCameraSpec>;
  tests?: ApplyTestSpec[];
  authoring?: ApplyAuthoringSpec;
  $draftKeys?: true;
}

export interface ApplyNodeSpec {
  slot: ApplySlotSpec;
  components?: ApplyComponentSpec[];
  children?: ApplyNodeSpec[];
}

export interface ApplyOwnershipSpec {
  key: string;
}

export interface ApplyProbeSpec {
  target?: string;
  method?: string;
  arguments?: Record<string, JsonValue>;
  safe?: boolean;
  kind?: string;
  value?: JsonValue;
  restore?: boolean;
  values?: Record<string, JsonValue>;
}

export interface ApplySlotSpec {
  name: string;
  parent?: string;
  position?: [number, number, number];
  rotation?: [number, number, number, number];
  scale?: [number, number, number];
  key?: string;
  managedFields?: (ManagedField)[];
  preserveWorldTransform?: boolean;
  migrateFrom?: string;
  relocationTransform?: RelocationTransform;
  runtimeRelocatable?: boolean;
}

export interface ApplyTestSpec {
  name: string;
  assertions?: ApplyAssertionSpec[];
  probe?: ApplyProbeSpec;
  timeoutMs?: number;
  pollMs?: number;
}

export interface SlotScalarProps {
  name: string;
  parent?: string;
  position?: [number, number, number];
  rotation?: [number, number, number, number];
  scale?: [number, number, number];
  key: string;
  managedFields?: (ManagedField)[];
  preserveWorldTransform?: boolean;
  migrateFrom?: string;
  relocationTransform?: RelocationTransform;
  runtimeRelocatable?: boolean;
}

export interface ComponentScalarProps {
  type: string;
  fields?: Record<string, JsonValue>;
  key: string;
  migrateFrom?: string;
  initialFields?: Record<string, JsonValue>;
  identityFields?: string[];
}
