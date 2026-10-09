import type { CatalogMemberRuntime, CatalogRuntimeTable } from "../src/catalog-runtime.js";

const boolField: CatalogMemberRuntime = {
  kind: "field", catalogKind: "field", confirmed: true, memberType: "Boolean",
  valueType: "bool", targetType: null, scalar: "boolean",
  fieldInterface: "[FrooxEngine]FrooxEngine.IField<bool>", fieldTarget: null,
};
const intField: CatalogMemberRuntime = {
  kind: "field", catalogKind: "field", confirmed: true, memberType: "Int32",
  valueType: "int", targetType: null, scalar: "int32",
  fieldInterface: "[FrooxEngine]FrooxEngine.IField<int>", fieldTarget: null,
};
const stringField: CatalogMemberRuntime = {
  kind: "field", catalogKind: "field", confirmed: true, memberType: "String",
  valueType: "string", targetType: null, scalar: "string",
  fieldInterface: "[FrooxEngine]FrooxEngine.IField<string>", fieldTarget: null,
};
const singleField: CatalogMemberRuntime = {
  kind: "field", catalogKind: "field", confirmed: true, memberType: "Single",
  valueType: "float", targetType: null, scalar: "single",
  fieldInterface: "[FrooxEngine]FrooxEngine.IField<float>", fieldTarget: null,
};
const boolReference: CatalogMemberRuntime = {
  kind: "reference", catalogKind: "field-reference", confirmed: true, memberType: "BooleanField",
  valueType: null, targetType: "[FrooxEngine]FrooxEngine.BooleanFieldAlias", scalar: null,
  fieldInterface: null, fieldTarget: { representation: "boolean", valueType: "bool",
    targetType: "[FrooxEngine]FrooxEngine.IField<bool>" },
};
const uncertainTarget: CatalogMemberRuntime = {
  kind: "reference", catalogKind: "field-reference", confirmed: false, memberType: "UnknownField",
  valueType: null, targetType: "[FrooxEngine]FrooxEngine.UnconfirmedAlias", scalar: null,
  fieldInterface: null, fieldTarget: null,
};
const uncertainMember: CatalogMemberRuntime = {
  kind: "other", catalogKind: "unknown", confirmed: false, memberType: null,
  valueType: null, targetType: null, scalar: null, fieldInterface: null, fieldTarget: null,
};

const members = {
  Enabled: boolField,
  Count: intField,
  Label: stringField,
  Weight: singleField,
  Target: boolReference,
  Uncertain: uncertainMember,
  UnconfirmedTarget: uncertainTarget,
};
const canonicalType = {
  canonicalType: "Synthetic.Device", confirmed: true, membersComplete: false, members,
};

/** Handwritten evidence fixture. Values here are independent of runtime implementation output. */
export function objectTestRuntimeTable(includeAlias = true): CatalogRuntimeTable {
  const types: Record<string, CatalogRuntimeTable["types"][string]> = { "Synthetic.Device": canonicalType };
  if (includeAlias) types["Synthetic.DeviceAlias"] = { ...canonicalType };
  return {
    formatVersion: "1", generatorVersion: "3", contentHash: "handwritten-runtime-hash",
    types,
  };
}

export function tableWithOwnType(name: string, entry: CatalogRuntimeTable["types"][string]): CatalogRuntimeTable {
  const types: Record<string, CatalogRuntimeTable["types"][string]> = {};
  Object.defineProperty(types, name, { value: { ...entry, canonicalType: name }, enumerable: true, configurable: true, writable: true });
  return { ...objectTestRuntimeTable(), types };
}

export function tableWithOwnProtoMember(): CatalogRuntimeTable {
  const base = objectTestRuntimeTable();
  const members: Record<string, CatalogMemberRuntime> = { ...base.types["Synthetic.Device"].members };
  Object.defineProperty(members, "__proto__", { value: boolField, enumerable: true, configurable: true, writable: true });
  return {
    ...base,
    types: { "Synthetic.Device": { ...base.types["Synthetic.Device"], members } },
  };
}
