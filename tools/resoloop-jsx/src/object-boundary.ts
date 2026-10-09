import { BuildError } from "./evaluate.js";
import { catalogMember, verifyRuntimeTable, type CatalogMemberRuntime, type CatalogRuntimeTable } from "./catalog-runtime.js";
import { copyBoundFieldMetadata } from "./bound-field.js";
import type { ObjectBuildContext, DeclaredTarget, ObjectPlacement } from "./object-expand.js";
import { decodeReference, isReferenceToken, type DecodedReference, type ScalarRepresentation } from "./object-refs.js";
import { copyOrigin, markBuildErrorOrigin, originOfElement, unknown, type Origin, type Source } from "./source-map.js";
import { scopeKey } from "./scope.js";

export type ExpectedReference =
  | { kind: "reference"; representation: ScalarRepresentation }
  | { kind: "value"; representation: ScalarRepresentation }
  | { kind: "other" };

interface Selector {
  kind: "slot" | "component" | "ref" | "member" | "slot-member" | "field";
  key: string;
  member?: string;
}

interface ResolvedTarget { selector: string; target: DeclaredTarget }
interface FieldEvidence { type: string; member: string; representation: ScalarRepresentation; table: CatalogRuntimeTable }

const representations = new Set<ScalarRepresentation>(["boolean", "int32", "string", "single"]);
const control = /[\u0000-\u001f\u007f]/;

function fail(code: string, message: string, origin?: Origin): never {
  const error = new BuildError(code, message);
  markBuildErrorOrigin(error, origin ?? { source: unknown });
  throw error;
}

function unavailable(type: string, member: string, origin?: Origin): never {
  return fail("APPLY_CATALOG_UNAVAILABLE",
    `Type '${type}', member '${member}': runtime catalog evidence is missing or unconfirmed.`, origin);
}

function mismatch(expected: string, observed: string, origin?: Origin): never {
  return fail("APPLY_OBJECT_TYPE_MISMATCH",
    `Object reference type mismatch: expected '${expected}', observed '${observed}'.`, origin);
}

function selectorOf(value: string): Selector | undefined {
  // StableSelectorSyntax in Core accepts any non-empty key/member text and
  // splits member selectors at their final dot, including across line breaks.
  const match = /^\$(slot|component|ref|member|slot-member|field):([\s\S]+)$/.exec(value);
  if (!match) return undefined;
  const kind = match[1] as Selector["kind"];
  if (kind === "member" || kind === "slot-member") {
    const dot = match[2].lastIndexOf(".");
    if (dot <= 0 || dot === match[2].length - 1) return undefined;
    return { kind, key: match[2].slice(0, dot), member: match[2].slice(dot + 1) };
  }
  return { kind, key: match[2] };
}

function objectScopeValue(value: string, scope: string): string {
  if (!scope) return value;
  const parsed = selectorOf(value);
  if (!parsed || parsed.key.includes("::")) return value;
  const prefix = value.slice(0, value.indexOf(":") + 1);
  return `${prefix}${scope}::${parsed.key}${parsed.member === undefined ? "" : `.${parsed.member}`}`;
}

function selectorKey(selector: Selector): string {
  return selector.kind === "member" ? `member:${selector.key}.${selector.member}` :
    selector.kind === "field" ? `field:${selector.key}` :
    `component:${selector.key}`;
}

function declaredTarget(selector: string, context: ObjectBuildContext): DeclaredTarget | undefined {
  const parsed = selectorOf(selector);
  if (!parsed) return undefined;
  if (parsed.kind === "slot" || parsed.kind === "slot-member") return context.targets.get(`slot:${parsed.key}`);
  if (parsed.kind === "component" || parsed.kind === "ref") return context.targets.get(`component:${parsed.key}`);
  return context.targets.get(selectorKey(parsed));
}

function missing(selector: string, parsed: Selector, origin?: Origin): never {
  if (parsed.kind === "field")
    return fail("APPLY_FIELD_ALIAS_NOT_FOUND", `Field alias '${parsed.key}' has no declared target`, origin);
  if (parsed.kind === "member")
    return fail("APPLY_MEMBER_REFERENCE_NOT_FOUND", `Member reference '${selector}' has no declared target`, origin);
  return fail("APPLY_SCOPE_INVALID",
    `Scoped reference '${selector}' has no declared target; use a qualified key for another scope`, origin);
}

function otherObject(key: string, owner: string | undefined, context: ObjectBuildContext): boolean {
  for (const id of context.objects.keys())
    if (id !== owner && key.startsWith(`${id}::`)) return true;
  return false;
}

function targetFor(selector: string, owner: string | undefined, context: ObjectBuildContext,
  origin?: Origin, allowExternal = true): ResolvedTarget | undefined {
  const parsed = selectorOf(selector);
  if (!parsed) return undefined;
  if (otherObject(parsed.key, owner, context))
    return fail("APPLY_OBJECT_PRIVATE_REFERENCE",
      `Private Object target '${selector}'; use ref.export(handle, name).`, origin);
  const qualified = selector;
  const scoped = parsed;
  const target = declaredTarget(qualified, context);
  if (target && target.owner !== owner) {
    if (target.owner !== undefined)
      return fail("APPLY_OBJECT_PRIVATE_REFERENCE",
        `Private Object target '${selector}'; use ref.export(handle, name).`, origin);
  }
  if (target) return { selector: qualified, target };
  if (owner !== undefined && scoped.key.startsWith(`${owner}::`)) return missing(qualified, scoped, origin);
  if (!allowExternal) return undefined;
  return undefined;
}

function decoded(value: string, context: ObjectBuildContext, origin?: Origin): DecodedReference {
  try {
    const result = decodeReference(value, context.decoded);
    if (!result) return fail("APPLY_OBJECT_EXPORT_INVALID", "Object '<invalid>' export '<invalid>': malformed typed target token.", origin);
    return result;
  } catch (error) {
    if (error instanceof BuildError) {
      markBuildErrorOrigin(error, origin);
      throw error;
    }
    throw error;
  }
}

function verifiedTables(context: ObjectBuildContext, origin?: Origin): CatalogRuntimeTable[] {
  return context.catalogs.map(table => {
    try { return verifyRuntimeTable(table); }
    catch (error) {
      if (error instanceof BuildError) markBuildErrorOrigin(error, origin ?? { source: unknown });
      throw error;
    }
  });
}

function canonical(table: CatalogRuntimeTable, type: string, member: string, origin?: Origin): string {
  if (!Object.hasOwn(table.types, type)) return unavailable(type, member, origin);
  const requested = table.types[type];
  if (!requested.confirmed || !Object.hasOwn(table.types, requested.canonicalType)) return unavailable(type, member, origin);
  const owner = table.types[requested.canonicalType];
  if (!owner.confirmed || owner.canonicalType !== requested.canonicalType) return unavailable(type, member, origin);
  return owner.canonicalType;
}

function sameMember(left: CatalogMemberRuntime, right: CatalogMemberRuntime): boolean {
  const leftTarget = left.fieldTarget;
  const rightTarget = right.fieldTarget;
  const sameTarget = leftTarget === null && rightTarget === null || leftTarget !== null && rightTarget !== null &&
    leftTarget.representation === rightTarget.representation && leftTarget.valueType === rightTarget.valueType &&
    leftTarget.targetType === rightTarget.targetType;
  return left.kind === right.kind && left.confirmed === right.confirmed && left.catalogKind === right.catalogKind &&
    left.memberType === right.memberType && left.valueType === right.valueType && left.targetType === right.targetType &&
    left.scalar === right.scalar && left.fieldInterface === right.fieldInterface && sameTarget;
}

function sameTable(left: CatalogRuntimeTable, right: CatalogRuntimeTable): boolean {
  if (left.formatVersion !== right.formatVersion || left.generatorVersion !== right.generatorVersion ||
      left.contentHash !== right.contentHash) return false;
  const leftTypes = Object.keys(left.types).sort();
  const rightTypes = Object.keys(right.types).sort();
  if (leftTypes.length !== rightTypes.length || leftTypes.some((name, index) => name !== rightTypes[index])) return false;
  for (const name of leftTypes) {
    const a = left.types[name];
    const b = right.types[name];
    if (a.canonicalType !== b.canonicalType || a.confirmed !== b.confirmed || a.membersComplete !== b.membersComplete) return false;
    const leftMembers = Object.keys(a.members).sort();
    const rightMembers = Object.keys(b.members).sort();
    if (leftMembers.length !== rightMembers.length || leftMembers.some((member, index) => member !== rightMembers[index])) return false;
    for (const member of leftMembers) if (!sameMember(a.members[member], b.members[member])) return false;
  }
  return true;
}

function memberEvidence(type: string, member: string, context: ObjectBuildContext, origin?: Origin):
  { canonicalType: string; descriptor: CatalogMemberRuntime; table: CatalogRuntimeTable } {
  const verified = verifiedTables(context, origin);
  const byHash = new Map<string, CatalogRuntimeTable>();
  for (const table of verified) {
    const firstForHash = byHash.get(table.contentHash);
    if (firstForHash && !sameTable(firstForHash, table)) return unavailable(type, member, origin);
    byHash.set(table.contentHash, firstForHash ?? table);
  }
  const tables = verified.filter(table => Object.hasOwn(table.types, type));
  if (tables.length === 0) return unavailable(type, member, origin);
  let first: { canonicalType: string; descriptor: CatalogMemberRuntime; table: CatalogRuntimeTable } | undefined;
  for (const table of tables) {
    const canonicalType = canonical(table, type, member, origin);
    let descriptor: CatalogMemberRuntime;
    try { descriptor = catalogMember(table, type, member); }
    catch (error) { if (error instanceof BuildError) { markBuildErrorOrigin(error, origin); throw error; } throw error; }
    if (!first) first = { canonicalType, descriptor, table };
    else if (first.canonicalType !== canonicalType || !sameMember(first.descriptor, descriptor))
      return unavailable(type, member, origin);
  }
  return first!;
}

function fieldEvidence(type: string, member: string, context: ObjectBuildContext, origin?: Origin): FieldEvidence {
  const found = memberEvidence(type, member, context, origin);
  const descriptor = found.descriptor;
  if (descriptor.kind !== "field" || descriptor.scalar === null || descriptor.fieldInterface === null)
    return unavailable(type, member, origin);
  return { type: found.canonicalType, member, representation: descriptor.scalar, table: found.table };
}

function componentType(target: DeclaredTarget): string | undefined {
  if (target.componentType) return target.componentType;
  const props = (target.element as { props?: Record<string, unknown> }).props;
  return typeof props?.type === "string" ? props.type : undefined;
}

function memberTarget(target: DeclaredTarget, context: ObjectBuildContext, origin?: Origin):
  { componentKey: string; type: string; member: string } {
  if (target.kind === "field" || target.kind === "member") {
    const member = target.member;
    const type = target.componentType;
    const owner = target.owner;
    if (member && type && owner !== undefined) {
      const componentKey = target.key.slice(0, target.key.lastIndexOf("."));
      return { componentKey, type, member };
    }
  }
  return unavailable(componentType(target) ?? "<unknown>", target.member ?? "<unknown>", origin);
}

function typedTarget(reference: Extract<DecodedReference, { kind: "target" }>, owner: string | undefined, scope: string,
  context: ObjectBuildContext, origin?: Origin): { resolved: ResolvedTarget; evidence: FieldEvidence } {
  const selector = objectScopeValue(reference.selector, scope);
  const parsed = selectorOf(selector);
  if (!parsed || parsed.kind !== "member")
    return fail("APPLY_OBJECT_EXPORT_INVALID", "Object '<invalid>' export '<invalid>': malformed typed target token.", origin);
  const resolved = targetFor(selector, owner, context, origin, false);
  if (!resolved) return missing(selector, parsed, origin);
  if (resolved.target.kind !== "member")
    return unavailable(resolved.target.componentType ?? "<unknown>", reference.member, origin);
  const actualType = componentType(resolved.target);
  if (!actualType) return unavailable("<unknown>", reference.member, origin);
  const tables = verifiedTables(context, origin).filter(table => table.contentHash === reference.catalogHash);
  if (tables.length === 0) return unavailable(actualType, reference.member, origin);
  if (tables.some(table => !sameTable(tables[0], table))) return unavailable(actualType, reference.member, origin);
  const table = tables[0];
  const claimedCanonical = canonical(table, reference.componentType, reference.member, origin);
  const actualCanonical = canonical(table, actualType, reference.member, origin);
  if (claimedCanonical !== actualCanonical) return mismatch("component", "component", origin);
  let descriptor: CatalogMemberRuntime;
  try { descriptor = catalogMember(table, actualType, reference.member); }
  catch (error) {
    if (error instanceof BuildError) { markBuildErrorOrigin(error, origin ?? { source: unknown }); throw error; }
    throw error;
  }
  if (descriptor.kind !== "field" || descriptor.scalar === null || descriptor.fieldInterface === null)
    return unavailable(actualType, reference.member, origin);
  if (reference.representation !== descriptor.scalar)
    return mismatch(`field:${reference.representation}`, `field:${descriptor.scalar}`, origin);
  return { resolved, evidence: { type: actualCanonical, member: reference.member, representation: descriptor.scalar, table } };
}

function expectedValue(type: string, member: string, context: ObjectBuildContext, origin?: Origin): ExpectedReference {
  const { descriptor } = memberEvidence(type, member, context, origin);
  if (descriptor.kind === "reference" && descriptor.fieldTarget)
    return { kind: "reference", representation: descriptor.fieldTarget.representation };
  if (descriptor.kind === "field" && descriptor.scalar !== null && descriptor.fieldInterface !== null)
    return { kind: "value", representation: descriptor.scalar };
  return { kind: "other" };
}

export function expectedReference(type: string, member: string, context: ObjectBuildContext): ExpectedReference {
  return expectedValue(type, member, context);
}

function expectedTypeCheck(expected: ExpectedReference, actual: FieldEvidence, origin?: Origin): void {
  if (expected.kind === "other") return;
  const expectedPrefix = expected.kind === "reference" ? "field" : "value";
  if (expected.representation !== actual.representation)
    return mismatch(`${expectedPrefix}:${expected.representation}`, `field:${actual.representation}`, origin);
  if (expected.kind === "value") return mismatch(`value:${expected.representation}`, `field:${actual.representation}`, origin);
}

function targetFieldEvidence(target: DeclaredTarget, context: ObjectBuildContext, origin?: Origin): FieldEvidence {
  const actual = memberTarget(target, context, origin);
  return fieldEvidence(actual.type, actual.member, context, origin);
}

function exportOrigin(placement: ObjectPlacement, name: string): Origin {
  const declared = originOfElement(placement.definition)?.attributes[name];
  const handle = originOfElement(placement.element)?.attributes.handle;
  const consumingSource = (handle as (Origin & { consumingSource?: Source }) | undefined)?.consumingSource;
  const primary = declared?.valueSource ?? unknown;
  return {
    source: primary, valueSource: primary,
    related: [...(declared?.related ?? []), ...(handle ? [consumingSource ?? handle.source, ...(handle.related ?? [])] : [])],
  };
}

function exportInvalid(placement: ObjectPlacement, name: string, reason: string): never {
  return fail("APPLY_OBJECT_EXPORT_INVALID", `Object '${placement.id}' export '${name}': ${reason}.`, exportOrigin(placement, name));
}

function validName(name: string): boolean { return name.trim().length > 0 && !control.test(name); }

export function validateObjectExports(context: ObjectBuildContext): void {
  for (const placement of context.objects.values()) {
    const exports = placement.definition.exports;
    for (const name of Object.keys(exports)) {
      if (!validName(name)) exportInvalid(placement, name, "export name must be nonempty");
      const value = exports[name];
      if (typeof value !== "string") exportInvalid(placement, name, "target is not a declared internal Slot, Component or Field");
      if (value.startsWith("$export:")) exportInvalid(placement, name, "re-exporting another Object is not supported");
      const origin = exportOrigin(placement, name);
      let selector: string;
      let target: DeclaredTarget | undefined;
      if (value.startsWith("$object-target:")) {
        let ref: DecodedReference;
        try { ref = decoded(value, context, origin); }
        catch { return exportInvalid(placement, name, "malformed typed target token"); }
        if (ref.kind !== "target") exportInvalid(placement, name, "malformed typed target token");
        selector = objectScopeValue(ref.selector, placement.id);
        const exportedTarget = declaredTarget(selector, context);
        if (!exportedTarget || exportedTarget.owner !== placement.id || exportedTarget.kind !== "member")
          exportInvalid(placement, name, "target is not a declared internal Slot, Component or Field");
        typedTarget(ref, placement.id, placement.id, context, origin);
        target = exportedTarget;
      } else {
        const parsed = selectorOf(value);
        if (!parsed) exportInvalid(placement, name, "target is not a declared internal Slot, Component or Field");
        if (parsed.kind === "slot-member")
          exportInvalid(placement, name, "target is not a declared internal Slot, Component or Field");
        selector = objectScopeValue(value, placement.id);
        const found = declaredTarget(selector, context);
        if (!found || found.owner !== placement.id ||
            !(found.kind === "slot" || found.kind === "component" || found.kind === "member" || found.kind === "field"))
          exportInvalid(placement, name, "target is not a declared internal Slot, Component or Field");
        target = found;
      }
      placement.exports.set(name, { selector, target });
    }
  }
}

function unknownExport(id: string, name: string, origin?: Origin): never {
  return fail("APPLY_OBJECT_EXPORT_UNKNOWN", `Unknown Object export '${id}.${name}'.`, origin);
}

function compareExportTarget(target: DeclaredTarget, expected: ExpectedReference | undefined,
  context: ObjectBuildContext, origin?: Origin): void {
  if (!expected) return;
  if (target.kind === "slot" || target.kind === "component") {
    if (expected.kind === "reference") return mismatch(`field:${expected.representation}`, target.kind, origin);
    if (expected.kind === "value") return mismatch(`value:${expected.representation}`, target.kind, origin);
  }
  if (expected.kind === "other") return unavailable(target.componentType ?? "<unknown>", target.member ?? "<unknown>", origin);
  const actual = targetFieldEvidence(target, context, origin);
  expectedTypeCheck(expected, actual, origin);
}

function containsReference(value: unknown, seen = new WeakSet<object>()): boolean {
  if (typeof value === "string") return isReferenceToken(value);
  if (value === null || typeof value !== "object" || seen.has(value)) return false;
  seen.add(value);
  return Array.isArray(value) ? value.some(item => containsReference(item, seen)) :
    Object.values(value as Record<string, unknown>).some(item => containsReference(item, seen));
}

function normalizeConsumerOrigin(origin: Origin | undefined): Origin | undefined {
  if (!origin) return undefined;
  return { source: unknown, valueSource: origin.valueSource ?? unknown, related: origin.related ?? [], children: origin.children };
}

function preflightPrivacy(value: unknown, owner: string | undefined, scope: string, origin: Origin | undefined,
  context: ObjectBuildContext, seen = new WeakSet<object>()): void {
  origin = normalizeConsumerOrigin(origin);
  if (typeof value === "string") {
    if (isReferenceToken(value) && value.startsWith("$object-target:")) {
      const ref = decoded(value, context, origin);
      if (ref.kind !== "target") return;
      const qualified = objectScopeValue(ref.selector, scope);
      const parsed = selectorOf(qualified);
      if (parsed && otherObject(parsed.key, owner, context))
        fail("APPLY_OBJECT_PRIVATE_REFERENCE", `Private Object target '${ref.selector}'; use ref.export(handle, name).`, origin);
    } else if (isReferenceToken(value) && value.startsWith("$export:")) {
      const ref = decoded(value, context, origin);
      if (ref.kind !== "export") return unknownExport("<invalid>", "<invalid>", origin);
      const placement = context.objects.get(ref.instanceId);
      if (!placement || placement.definition.definitionId !== ref.definitionId || !placement.exports.has(ref.name))
        return unknownExport(ref.instanceId, ref.name, origin);
    } else {
      const parsed = selectorOf(value);
      if (parsed) {
        const qualified = objectScopeValue(value, scope);
        const scoped = selectorOf(qualified);
        if (scoped && otherObject(scoped.key, owner, context))
          fail("APPLY_OBJECT_PRIVATE_REFERENCE", `Private Object target '${value}'; use ref.export(handle, name).`, origin);
      }
    }
    return;
  }
  if (value === null || typeof value !== "object" || seen.has(value)) return;
  seen.add(value);
  if (Array.isArray(value)) value.forEach((item, index) =>
    preflightPrivacy(item, owner, scope, origin?.children?.[String(index)], context, seen));
  else Object.entries(value as Record<string, unknown>).forEach(([key, item]) =>
    preflightPrivacy(item, owner, scope, origin?.children?.[key], context, seen));
}

export function resolveObjectValue(value: unknown, owner: string | undefined, scope: string,
  origin: Origin | undefined, expected: ExpectedReference | undefined,
  context: ObjectBuildContext, strictReferencePrefixes = false): unknown {
  origin = normalizeConsumerOrigin(origin);
  if (typeof value === "string") {
    if (value.startsWith("$object-target:") && (strictReferencePrefixes || isReferenceToken(value))) {
      const ref = decoded(value, context, origin);
      if (ref.kind !== "target") return fail("APPLY_OBJECT_EXPORT_INVALID", "Object '<invalid>' export '<invalid>': malformed typed target token.", origin);
      const checked = typedTarget(ref, owner, scope, context, origin);
      if (expected) {
        if (expected.kind === "other") return unavailable(checked.evidence.type, checked.evidence.member, origin);
        expectedTypeCheck(expected, checked.evidence, origin);
      }
      return checked.resolved.selector;
    }
    if (value.startsWith("$export:") && (strictReferencePrefixes || isReferenceToken(value))) {
      const ref = decoded(value, context, origin);
      if (ref.kind !== "export") return unknownExport("<invalid>", "<invalid>", origin);
      const placement = context.objects.get(ref.instanceId);
      if (!placement || placement.definition.definitionId !== ref.definitionId)
        return unknownExport(ref.instanceId, ref.name, origin);
      const exported = placement.exports.get(ref.name);
      if (!exported) return unknownExport(ref.instanceId, ref.name, origin);
      compareExportTarget(exported.target, expected, context, origin);
      return exported.selector;
    }
    const parsed = selectorOf(value);
    if (!parsed) return value;
    const qualified = objectScopeValue(value, scope);
    const resolved = targetFor(qualified, owner, context, origin);
    return resolved?.selector ?? qualified;
  }
  if (value === null || typeof value !== "object") return value;
  if (Array.isArray(value)) return value.map((item, index) => resolveObjectValue(item, owner, scope,
    origin?.children?.[String(index)], expected, context));
  return Object.fromEntries(Object.entries(value as Record<string, unknown>).map(([key, item]) =>
    [key, resolveObjectValue(item, owner, scope, origin?.children?.[key], expected, context)]));
}

function expectedFor(type: string | undefined, member: string, value: unknown, context: ObjectBuildContext,
  owner: string | undefined, scope: string, origin?: Origin): ExpectedReference | undefined {
  if (!type || !containsReference(value)) return undefined;
  preflightPrivacy(value, owner, scope, origin, context);
  const expected = expectedValue(type, member, context, origin);
  if (expected.kind === "other") return unavailable(type, member, origin);
  return expected;
}

function migrateValue(value: unknown, kind: "slot" | "component", owner: string | undefined, scope: string,
  origin: Origin | undefined, context: ObjectBuildContext): unknown {
  if (typeof value !== "string") return value;
  if (value.startsWith("$export:") || value.startsWith("$object-target:"))
    return resolveObjectValue(value, owner, scope, origin, undefined, context, true);
  const prefix = kind === "slot" ? "$slot:" : "$component:";
  const selectorInput = value.startsWith("$") ? value : `${prefix}${value}`;
  const scoped = objectScopeValue(selectorInput, scope);
  const parsed = selectorOf(scoped);
  if (parsed && otherObject(parsed.key, owner, context))
    fail("APPLY_OBJECT_PRIVATE_REFERENCE", `Private Object target '${selectorInput}'; use ref.export(handle, name).`, origin);
  return value.startsWith("$") ? scoped : parsed?.key ?? value;
}

function cloneChildren(value: unknown, context: ObjectBuildContext, scope: string, owner: string | undefined,
  parentType?: string): unknown {
  if (value === null || typeof value !== "object") return value;
  if (Array.isArray(value)) return value.map(item => cloneChildren(item, context, scope, owner, parentType));
  if (!isElement(value)) return value;
  return cloneElement(value, context, scope, owner, parentType);
}

function isElement(value: object): value is { kind: string; props?: Record<string, any> | null } {
  return typeof (value as { kind?: unknown }).kind === "string";
}

function cloneElement(value: { kind: string; props?: Record<string, any> | null }, context: ObjectBuildContext,
  scope: string, owner: string | undefined, fieldParentType?: string): object {
  const props = value.props ?? {};
  const origin = originOfElement(value);
  const clone: { kind: string; props: Record<string, any> } = { ...value, props: { ...props } };
  copyOrigin(value, clone);
  copyBoundFieldMetadata(value, clone);
  context.contexts.set(clone, { scope, owner });
  const propOrigin = (name: string): Origin | undefined => origin?.attributes[name];
  if (value.kind === "slot") {
    if (props.parent !== undefined) clone.props.parent = resolveObjectValue(props.parent, owner, scope,
      normalizeConsumerOrigin(propOrigin("parent")), undefined, context);
    if (props.migrateFrom !== undefined) clone.props.migrateFrom = migrateValue(props.migrateFrom, "slot", owner,
      scope, normalizeConsumerOrigin(propOrigin("migrateFrom")), context);
    if (props.children !== undefined) clone.props.children = cloneChildren(props.children, context, scope, owner);
  } else if (value.kind === "component") {
    const type = typeof props.type === "string" ? props.type : undefined;
    for (const section of ["fields", "initialFields"] as const) {
      const source = props[section];
      if (source === null || typeof source !== "object" || Array.isArray(source)) continue;
      clone.props[section] = Object.fromEntries(Object.entries(source).map(([member, data]) => {
        const childOrigin = propOrigin(section)?.children?.[member];
        const consumeOrigin = normalizeConsumerOrigin(childOrigin);
        const expected = expectedFor(type, member, data, context, owner, scope, consumeOrigin);
        return [member, resolveObjectValue(data, owner, scope, consumeOrigin, expected, context)];
      }));
    }
    if (props.migrateFrom !== undefined) clone.props.migrateFrom = migrateValue(props.migrateFrom, "component", owner,
      scope, normalizeConsumerOrigin(propOrigin("migrateFrom")), context);
    if (props.children !== undefined) clone.props.children = cloneChildren(props.children, context, scope, owner, type);
  } else if (value.kind === "field") {
    if (props.value !== undefined) {
      const member = typeof props.name === "string" ? props.name : "<unknown>";
      const consumeOrigin = normalizeConsumerOrigin(propOrigin("value"));
      const expected = expectedFor(fieldParentType, member, props.value, context, owner, scope, consumeOrigin);
      clone.props.value = resolveObjectValue(props.value, owner, scope, consumeOrigin, expected, context);
    }
  } else if (value.kind === "fragment") {
    if (props.children !== undefined)
      clone.props.children = cloneChildren(props.children, context, scope, owner, fieldParentType);
  } else if (value.kind === "scope") {
    const nestedScope = scopeKey(scope, props.instanceKey, "$.instanceKey");
    context.contexts.set(clone, { scope: nestedScope, owner });
    if (props.children !== undefined)
      clone.props.children = cloneChildren(props.children, context, nestedScope, owner, fieldParentType);
  } else if (value.kind === "object") {
    const placement = [...context.objects.values()].find(item => item.element === value);
    const id = placement?.id ?? (typeof props.handle?.id === "string" ? props.handle.id : "<invalid>");
    context.contexts.set(clone, { scope: id, owner: id });
    if (props.body !== undefined) clone.props.body = cloneChildren(props.body, context, id, id);
  }
  return clone;
}

export function validateObjectBoundaries(context: ObjectBuildContext): void {
  if (!context.needsPass) return;
  context.root = cloneChildren(context.root, context, "", undefined);
}
