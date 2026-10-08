import { BuildError } from "./evaluate.js";
import type { ScalarRepresentation } from "./object-refs.js";

export interface CatalogMemberRuntime {
  readonly kind: "field" | "reference" | "other";
  readonly catalogKind: string;
  readonly confirmed: boolean;
  readonly memberType: string | null;
  readonly valueType: string | null;
  readonly targetType: string | null;
  readonly scalar: ScalarRepresentation | null;
  readonly fieldInterface: string | null;
  readonly fieldTarget: { readonly representation: ScalarRepresentation;
    readonly valueType: string; readonly targetType: string } | null;
}

export interface CatalogRuntimeTable {
  readonly formatVersion: "1";
  readonly generatorVersion: "3";
  readonly contentHash: string;
  readonly types: Readonly<Record<string, { readonly canonicalType: string;
    readonly confirmed: boolean; readonly membersComplete: boolean;
    readonly members: Readonly<Record<string, CatalogMemberRuntime>> }>>;
}

const representations = new Set<ScalarRepresentation>(["boolean", "int32", "string", "single"]);
const mismatchMessage = "Catalog runtime table does not match format/generator/descriptor shape; regenerate catalog types.";

function isRecord(value: unknown): value is Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) return false;
  const prototype = Object.getPrototypeOf(value);
  return prototype === Object.prototype || prototype === null;
}

function exactKeys(value: Record<string, unknown>, expected: readonly string[]): boolean {
  const keys = Reflect.ownKeys(value);
  return keys.length === expected.length && keys.every(key => typeof key === "string" && expected.includes(key)) &&
    expected.every(key => Object.hasOwn(value, key));
}

function nonempty(value: unknown): value is string {
  return typeof value === "string" && value.length > 0;
}

function isRepresentation(value: unknown): value is ScalarRepresentation {
  return typeof value === "string" && representations.has(value as ScalarRepresentation);
}

function isFieldInterface(value: string): boolean {
  const prefix = "[FrooxEngine]FrooxEngine.IField<";
  if (!value.startsWith(prefix) || !value.endsWith(">")) return false;
  const argument = value.slice(prefix.length, -1);
  if (!argument || argument.trim() !== argument) return false;
  let depth = 0;
  for (const character of argument) {
    if (character === "<") depth++;
    else if (character === ">") {
      if (depth === 0) return false;
      depth--;
    } else if (character === "," && depth === 0) return false;
  }
  return depth === 0;
}

function invalidTable(): never {
  throw new BuildError("APPLY_CATALOG_TYPES_HASH_MISMATCH", mismatchMessage);
}

function validateMember(value: unknown, ownerConfirmed: boolean): value is CatalogMemberRuntime {
  if (!isRecord(value) || !exactKeys(value, ["kind", "catalogKind", "confirmed", "memberType", "valueType",
    "targetType", "scalar", "fieldInterface", "fieldTarget"])) return false;
  if (!(value.kind === "field" || value.kind === "reference" || value.kind === "other") ||
      !nonempty(value.catalogKind) || typeof value.confirmed !== "boolean" ||
      !(value.memberType === null || nonempty(value.memberType)) ||
      !(value.valueType === null || nonempty(value.valueType)) ||
      !(value.targetType === null || nonempty(value.targetType)) ||
      !(value.scalar === null || isRepresentation(value.scalar)) ||
      !(value.fieldInterface === null || nonempty(value.fieldInterface))) return false;

  const fieldTargetValue = value.fieldTarget;
  let fieldTarget: Record<string, unknown> | undefined;
  let validFieldTarget = fieldTargetValue === null;
  if (isRecord(fieldTargetValue) && exactKeys(fieldTargetValue, ["representation", "valueType", "targetType"])) {
    fieldTarget = fieldTargetValue;
    validFieldTarget = isRepresentation(fieldTarget.representation) && nonempty(fieldTarget.valueType) &&
      nonempty(fieldTarget.targetType) && isFieldInterface(fieldTarget.targetType);
  }
  if (!validFieldTarget) return false;

  if ((!ownerConfirmed || !value.confirmed) &&
      (value.scalar !== null || value.fieldInterface !== null || fieldTargetValue !== null)) return false;
  if (value.kind !== "field" && (value.scalar !== null || value.fieldInterface !== null)) return false;
  if (value.fieldInterface !== null && (value.scalar === null || !isFieldInterface(value.fieldInterface))) return false;
  if (fieldTargetValue !== null && (value.kind !== "reference" || value.targetType === null || fieldTarget === undefined)) return false;
  return true;
}

function sameMember(left: unknown, right: unknown): boolean {
  if (!isRecord(left) || !isRecord(right)) return false;
  return ["kind", "catalogKind", "confirmed", "memberType", "valueType", "targetType", "scalar", "fieldInterface"]
    .every(key => left[key] === right[key]) &&
    (left.fieldTarget === null && right.fieldTarget === null ||
      isRecord(left.fieldTarget) && isRecord(right.fieldTarget) &&
      left.fieldTarget.representation === right.fieldTarget.representation &&
      left.fieldTarget.valueType === right.fieldTarget.valueType && left.fieldTarget.targetType === right.fieldTarget.targetType);
}

function sameTypeMetadata(left: Record<string, unknown>, right: Record<string, unknown>): boolean {
  if (left.confirmed !== right.confirmed || left.membersComplete !== right.membersComplete ||
      !isRecord(left.members) || !isRecord(right.members)) return false;
  const leftMembers = left.members;
  const rightMembers = right.members;
  const leftNames = Reflect.ownKeys(leftMembers);
  const rightNames = Reflect.ownKeys(rightMembers);
  return leftNames.length === rightNames.length && leftNames.every(name => typeof name === "string" &&
    Object.hasOwn(rightMembers, name) && sameMember(leftMembers[name], rightMembers[name]));
}

/** Validate an untrusted runtime descriptor before any catalog lookup is allowed. */
export function verifyRuntimeTable(value: unknown): CatalogRuntimeTable {
  if (!isRecord(value) || !exactKeys(value, ["formatVersion", "generatorVersion", "contentHash", "types"]) ||
      value.formatVersion !== "1" || value.generatorVersion !== "3" || !nonempty(value.contentHash) || !isRecord(value.types))
    return invalidTable();

  const types = value.types;
  for (const type of Reflect.ownKeys(types)) {
    if (typeof type !== "string") return invalidTable();
    const entry = types[type];
    if (!isRecord(entry) || !exactKeys(entry, ["canonicalType", "confirmed", "membersComplete", "members"]) ||
        !nonempty(entry.canonicalType) || typeof entry.confirmed !== "boolean" ||
        typeof entry.membersComplete !== "boolean" || !isRecord(entry.members)) return invalidTable();
    for (const member of Reflect.ownKeys(entry.members)) {
      if (typeof member !== "string" || !validateMember(entry.members[member], entry.confirmed)) return invalidTable();
    }
  }

  for (const type of Reflect.ownKeys(types)) {
    if (typeof type !== "string") return invalidTable();
    const entry = types[type] as Record<string, unknown>;
    const canonicalName = entry.canonicalType as string;
    if (!Object.hasOwn(types, canonicalName)) return invalidTable();
    const canonical = types[canonicalName];
    if (!isRecord(canonical) || canonical.canonicalType !== canonicalName ||
        type !== canonicalName && !sameTypeMetadata(entry, canonical)) return invalidTable();
  }
  return value as unknown as CatalogRuntimeTable;
}

/** Resolve only own, confirmed evidence; aliases use the canonical type entry. */
export function catalogMember(table: CatalogRuntimeTable, type: string, member: string): CatalogMemberRuntime {
  const unavailable = (): never => {
    throw new BuildError("APPLY_CATALOG_UNAVAILABLE",
      `Type '${type}', member '${member}': runtime catalog evidence is missing or unconfirmed.`);
  };
  if (!Object.hasOwn(table.types, type)) return unavailable();
  const requested = table.types[type];
  if (!requested.confirmed || !Object.hasOwn(table.types, requested.canonicalType)) return unavailable();
  const owner = table.types[requested.canonicalType];
  if (!owner.confirmed || !Object.hasOwn(owner.members, member)) return unavailable();
  const evidence = owner.members[member];
  if (!evidence.confirmed) return unavailable();
  return evidence;
}
