import { BuildError } from "./evaluate.js";

declare const fieldValue: unique symbol;
declare const fieldRepresentation: unique symbol;
const handleIdentity: unique symbol = Symbol("resoloop.object.handle");

export type ScalarRepresentation = "boolean" | "int32" | "string" | "single";
export type ScalarValue = boolean | number | string;
export type RepresentationOf<T> = [T] extends [boolean] ? "boolean" :
  [T] extends [number] ? "int32" | "single" : [T] extends [string] ? "string" : never;
export type FieldRef<T extends ScalarValue, R extends ScalarRepresentation = RepresentationOf<T>> =
  string & { readonly [fieldValue]: (value: T) => T; readonly [fieldRepresentation]: R };
export type UncheckedReference = string & {
  readonly [fieldValue]?: never; readonly [fieldRepresentation]?: never;
};
export type PublicRefs = Readonly<Record<string, string>>;
export interface InstanceHandle<E extends PublicRefs> {
  readonly id: string;
  readonly exports: E;
  readonly [handleIdentity]: object;
}

export type DecodedReference =
  | { readonly kind: "target"; readonly selector: string; readonly componentType: string;
      readonly member: string; readonly representation: ScalarRepresentation; readonly catalogHash: string }
  | { readonly kind: "export"; readonly instanceId: string; readonly definitionId: string; readonly name: string };

let nextDefinitionNumber = 0;
const instanceBrand = handleIdentity;
const idPattern = /^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/;
const scalarRepresentations = new Set<ScalarRepresentation>(["boolean", "int32", "string", "single"]);
const controlCharacters = /[\u0000-\u001f\u007f]/;

export function newDefinitionIdentity(): string {
  nextDefinitionNumber++;
  return `definition-${nextDefinitionNumber}`;
}

function assertValidId(id: string): void {
  if (!idPattern.test(id))
    throw new BuildError("APPLY_OBJECT_INVALID", `Object '${id}': invalid instance ID.`);
}

function assertExportName(id: string, name: string): void {
  if (name.trim().length === 0)
    throw new BuildError("APPLY_OBJECT_EXPORT_INVALID", `Object '${id}' export '${name}': export name must be nonempty.`);
  if (controlCharacters.test(name))
    throw new BuildError("APPLY_OBJECT_EXPORT_INVALID", `Object '${id}' export '${name}': export name must be nonempty.`);
}

function brandOf(value: unknown): { id: string; definitionId: string } | undefined {
  if (value === null || typeof value !== "object") return undefined;
  const descriptor = Object.getOwnPropertyDescriptor(value, instanceBrand);
  if (!descriptor || descriptor.enumerable || descriptor.value === null || typeof descriptor.value !== "object" ||
      typeof (descriptor.value as { definitionId?: unknown }).definitionId !== "string") return undefined;
  const candidate = value as { id?: unknown };
  return typeof candidate.id === "string"
    ? { id: candidate.id, definitionId: (descriptor.value as { definitionId: string }).definitionId }
    : undefined;
}

function assertBrandedHandle(value: unknown): asserts value is InstanceHandle<PublicRefs> {
  if (!brandOf(value))
    throw new BuildError("APPLY_OBJECT_INVALID", "Object '<invalid>': handle is missing or unbranded.");
}

export function createHandle<E extends PublicRefs>(id: string, exports: E, definitionId: string): InstanceHandle<E> {
  assertValidId(id);
  if (definitionId.length === 0) throw new TypeError("definitionId must be nonempty");
  const copiedExports = Object.freeze({ ...exports }) as E;
  const handle = { id, exports: copiedExports } as Omit<InstanceHandle<E>, typeof handleIdentity> & Partial<InstanceHandle<E>>;
  Object.defineProperty(handle, instanceBrand, {
    configurable: false, enumerable: false, writable: false, value: Object.freeze({ definitionId }),
  });
  return Object.freeze(handle) as InstanceHandle<E>;
}

export function handleDefinitionIdentity(handle: InstanceHandle<PublicRefs>): string {
  assertBrandedHandle(handle);
  return brandOf(handle)!.definitionId;
}

export function exportReference<E extends PublicRefs, K extends keyof E & string>(
  handle: InstanceHandle<E>, name: K): E[K] {
  assertBrandedHandle(handle);
  assertValidId(handle.id);
  assertExportName(handle.id, name);
  if (!Object.hasOwn(handle.exports, name))
    throw new BuildError("APPLY_OBJECT_EXPORT_UNKNOWN", `Unknown Object export '${handle.id}.${name}'.`);
  const payload = { v: 1, instanceId: handle.id, definitionId: brandOf(handle)!.definitionId, name };
  return (`$export:${encodeURIComponent(JSON.stringify(payload))}`) as E[K];
}

function malformedTarget(): never {
  throw new BuildError("APPLY_OBJECT_EXPORT_INVALID", "Object '<invalid>' export '<invalid>': malformed typed target token.");
}

function malformedExport(): never {
  throw new BuildError("APPLY_OBJECT_EXPORT_UNKNOWN", "Unknown Object export '<invalid>'.");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function hasExactKeys(value: Record<string, unknown>, expected: readonly string[]): boolean {
  const keys = Object.keys(value);
  return keys.length === expected.length && expected.every(key => Object.hasOwn(value, key));
}

function decodeEnvelope(value: string): unknown {
  try { return JSON.parse(decodeURIComponent(value)); }
  catch { return undefined; }
}

/** Recognize only complete reference tokens; ordinary strings may share a prefix. */
export function isReferenceToken(value: unknown): boolean {
  if (typeof value !== "string") return false;
  if (value.startsWith("$object-target:")) {
    const payload = decodeEnvelope(value.slice("$object-target:".length));
    if (!isRecord(payload) || !hasExactKeys(payload, ["v", "selector", "componentType", "member", "representation", "catalogHash"]) ||
        payload.v !== 1 || typeof payload.selector !== "string" || typeof payload.componentType !== "string" ||
        typeof payload.member !== "string" || typeof payload.representation !== "string" ||
        typeof payload.catalogHash !== "string" || !payload.componentType || !payload.member || !payload.catalogHash ||
        !scalarRepresentations.has(payload.representation as ScalarRepresentation)) return false;
    const selectorBody = payload.selector.startsWith("$member:") ? payload.selector.slice("$member:".length) : "";
    const separator = selectorBody.lastIndexOf(".");
    return separator > 0 && separator < selectorBody.length - 1 && selectorBody.slice(separator + 1) === payload.member;
  }
  if (value.startsWith("$export:")) {
    const payload = decodeEnvelope(value.slice("$export:".length));
    return isRecord(payload) && hasExactKeys(payload, ["v", "instanceId", "definitionId", "name"]) && payload.v === 1 &&
      typeof payload.instanceId === "string" && typeof payload.definitionId === "string" && typeof payload.name === "string" &&
      idPattern.test(payload.instanceId) && !!payload.definitionId && payload.name.trim().length > 0 && !controlCharacters.test(payload.name);
  }
  return false;
}

export function targetReference<T extends ScalarValue, R extends ScalarRepresentation>(
  componentKey: string,
  member: string,
  componentType: string,
  descriptor: { readonly scalar: R; readonly fieldInterface: string },
  catalogHash: string,
): FieldRef<T, R> {
  if (!componentKey || !member || !componentType || !descriptor.fieldInterface || !catalogHash ||
      !scalarRepresentations.has(descriptor.scalar)) return malformedTarget();
  const selector = `$member:${componentKey}.${member}`;
  const payload = { v: 1, selector, componentType, member, representation: descriptor.scalar, catalogHash };
  return (`$object-target:${encodeURIComponent(JSON.stringify(payload))}`) as FieldRef<T, R>;
}

export function decodeReference(value: string, cache: Map<string, DecodedReference>): DecodedReference | undefined {
  const cached = cache.get(value);
  if (cached !== undefined) return cached;
  if (value.startsWith("$object-target:")) {
    const payload = decodeEnvelope(value.slice("$object-target:".length));
    if (!isRecord(payload) || !hasExactKeys(payload, ["v", "selector", "componentType", "member", "representation", "catalogHash"]) ||
        payload.v !== 1 || typeof payload.selector !== "string" || typeof payload.componentType !== "string" ||
        typeof payload.member !== "string" || typeof payload.representation !== "string" ||
        typeof payload.catalogHash !== "string" || !payload.componentType || !payload.member || !payload.catalogHash ||
        !scalarRepresentations.has(payload.representation as ScalarRepresentation)) return malformedTarget();
    const selectorBody = payload.selector.startsWith("$member:") ? payload.selector.slice("$member:".length) : "";
    const separator = selectorBody.lastIndexOf(".");
    if (separator <= 0 || separator === selectorBody.length - 1 ||
        selectorBody.slice(separator + 1) !== payload.member) return malformedTarget();
    const decoded: DecodedReference = {
      kind: "target", selector: payload.selector, componentType: payload.componentType, member: payload.member,
      representation: payload.representation as ScalarRepresentation, catalogHash: payload.catalogHash,
    };
    cache.set(value, decoded);
    return decoded;
  }
  if (value.startsWith("$export:")) {
    const payload = decodeEnvelope(value.slice("$export:".length));
    if (!isRecord(payload) || !hasExactKeys(payload, ["v", "instanceId", "definitionId", "name"]) || payload.v !== 1 ||
        typeof payload.instanceId !== "string" || typeof payload.definitionId !== "string" || typeof payload.name !== "string" ||
        !idPattern.test(payload.instanceId) || !payload.definitionId || payload.name.trim().length === 0 || controlCharacters.test(payload.name))
      return malformedExport();
    const decoded: DecodedReference = {
      kind: "export", instanceId: payload.instanceId, definitionId: payload.definitionId, name: payload.name,
    };
    cache.set(value, decoded);
    return decoded;
  }
  return undefined;
}
