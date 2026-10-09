import { BuildError } from "./evaluate.js";
import type { CatalogRuntimeTable } from "./catalog-runtime.js";
import { boundFieldMetadata, copyBoundFieldMetadata } from "./bound-field.js";
import type { ObjectDescriptor } from "./object-definition.js";
import type { ObjectElement } from "./elements.js";
import { handleDefinitionIdentity, type DecodedReference } from "./object-refs.js";
import { copyOrigin, markBuildErrorOrigin, originOfElement, unknown, type Origin } from "./source-map.js";
import { scopeKey } from "./scope.js";
import { validateObjectBoundaries as applyObjectBoundaries, validateObjectExports } from "./object-boundary.js";

export interface DeclaredTarget {
  readonly key: string;
  readonly kind: "slot" | "component" | "member" | "field";
  readonly owner?: string;
  readonly element: object;
  readonly componentType?: string;
  readonly member?: string;
}

export interface ObjectPlacement {
  readonly id: string;
  readonly definition: ObjectDescriptor;
  readonly element: object;
  readonly exports: Map<string, { selector: string; target: DeclaredTarget }>;
}

export interface ObjectBuildContext {
  root: unknown;
  readonly catalogs: readonly CatalogRuntimeTable[];
  readonly objects: Map<string, ObjectPlacement>;
  readonly targets: Map<string, DeclaredTarget>;
  readonly contexts: WeakMap<object, { scope: string; owner?: string }>;
  readonly decoded: Map<string, DecodedReference>;
  needsPass: boolean;
}

/** Apply boundary validation only after callers validate the export declarations. */
export function validateObjectBoundaries(context: ObjectBuildContext): void {
  if (context.needsPass) applyObjectBoundaries(context);
}

interface RawElement {
  kind: string;
  props?: Record<string, any> | null;
}

const idPattern = /^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/;
const reservedPrefixes = ["$export:", "$object-target:"];

function isElement(value: unknown): value is RawElement {
  return value !== null && typeof value === "object" && !Array.isArray(value) &&
    typeof (value as { kind?: unknown }).kind === "string";
}

function errorOrigin(element: object, attribute?: string): Origin {
  const origin = originOfElement(element);
  const prop = attribute ? origin?.attributes[attribute] : undefined;
  return {
    source: attribute === "handle" || attribute === "body"
      ? prop?.source ?? unknown
      : prop?.source ?? origin?.source ?? unknown,
    related: [...(prop?.related ?? []), ...(origin?.related ?? [])],
  };
}

function objectError(id: string, reason: string, element?: object, attribute?: string): never {
  const error = new BuildError("APPLY_OBJECT_INVALID", `Object '${id}': ${reason}.`);
  if (element) markBuildErrorOrigin(error, errorOrigin(element, attribute));
  throw error;
}

function duplicateObjectError(id: string, second: object, first: object): never {
  const error = new BuildError("APPLY_OBJECT_DUPLICATE", `Object instance '${id}' occurs twice.`);
  const secondOrigin = errorOrigin(second, "handle");
  const firstOrigin = errorOrigin(first, "handle");
  markBuildErrorOrigin(error, {
    source: secondOrigin.source,
    related: [...(secondOrigin.related ?? []), firstOrigin.source, ...(firstOrigin.related ?? [])],
  });
  throw error;
}

function scopedKey(scope: string, value: unknown, path: string, element: object, attribute: string): string {
  try { return scopeKey(scope, value, path); }
  catch (error) {
    if (error instanceof Error) markBuildErrorOrigin(error, errorOrigin(element, attribute));
    throw error;
  }
}

function isReferenceToken(value: unknown): value is string {
  return typeof value === "string" && reservedPrefixes.some(prefix => value.startsWith(prefix));
}

function scanValues(value: unknown, seen: WeakSet<object>, visit: (element: RawElement) => void,
  activate: () => void): void {
  if (typeof value === "string") {
    if (isReferenceToken(value)) activate();
    return;
  }
  if (value === null || typeof value !== "object") return;
  if (seen.has(value)) return;
  seen.add(value);
  if (Array.isArray(value)) {
    for (const item of value) scanValues(item, seen, visit, activate);
    return;
  }
  if (isElement(value)) {
    visit(value);
    if (boundFieldMetadata(value)) activate();
  }
  for (const key of Object.keys(value))
    scanValues((value as Record<string, unknown>)[key], seen, visit, activate);
}

function rootsForBody(value: unknown, scope: string, owner: string,
  contexts: WeakMap<object, { scope: string; owner?: string }>, path: string): unknown[] {
  if (value === null || value === undefined || typeof value === "boolean") return [];
  if (Array.isArray(value)) return value.flatMap((item, index) =>
    rootsForBody(item, scope, owner, contexts, `${path}[${index}]`));
  if (!isElement(value)) return [value];
  contexts.set(value, { scope, owner });
  if (value.kind === "fragment") return rootsForBody(value.props?.children, scope, owner, contexts, path);
  if (value.kind === "scope") {
    const nested = scopedKey(scope, value.props?.instanceKey, `${path}.instanceKey`, value, "instanceKey");
    contexts.set(value, { scope: nested, owner });
    return rootsForBody(value.props?.children, nested, owner, contexts, path);
  }
  return [value];
}

function containsObject(value: unknown, seen = new WeakSet<object>()): boolean {
  if (value === null || typeof value !== "object") return false;
  if (seen.has(value)) return false;
  seen.add(value);
  if (Array.isArray(value)) return value.some(item => containsObject(item, seen));
  if (!isElement(value)) return false;
  if (value.kind === "object") return true;
  if (["fragment", "scope", "slot", "component"].includes(value.kind))
    return containsObject(value.props?.children, seen);
  return false;
}

function walkObjectValues(value: unknown, visit: (element: RawElement) => void,
  active = new WeakSet<object>()): void {
  if (value === null || typeof value !== "object") return;
  if (active.has(value)) return;
  active.add(value);
  if (Array.isArray(value)) {
    for (const item of value) walkObjectValues(item, visit, active);
  } else if (isElement(value)) {
    visit(value);
    const children = value.kind === "object" ? value.props?.body
      : ["fragment", "scope", "slot", "component"].includes(value.kind) ? value.props?.children
      : undefined;
    if (children !== undefined) walkObjectValues(children, visit, active);
  }
  active.delete(value);
}

function flattenEntries(value: unknown, scope: string, owner: string | undefined,
  contexts: WeakMap<object, { scope: string; owner?: string }>, path: string,
  reserveScope: (element: RawElement, scope: string, owner: string | undefined, path: string) => void):
  Array<{ element: RawElement; scope: string; owner?: string }> {
  if (value === null || value === undefined || typeof value === "boolean") return [];
  if (Array.isArray(value)) return value.flatMap((item, index) =>
    flattenEntries(item, scope, owner, contexts, `${path}[${index}]`, reserveScope));
  if (!isElement(value)) return [];
  if (value.kind === "fragment") return flattenEntries(value.props?.children, scope, owner, contexts, path, reserveScope);
  if (value.kind === "scope") {
    contexts.set(value, { scope, owner });
    reserveScope(value, scope, owner, path);
    return [{ element: value, scope, owner }];
  }
  contexts.set(value, { scope, owner });
  return [{ element: value, scope, owner }];
}

/** Collect Object placements and declared stable targets without changing source elements. */
export function collectObjects(root: unknown, catalogs: readonly CatalogRuntimeTable[]): ObjectBuildContext {
  const context: ObjectBuildContext = {
    root, catalogs, objects: new Map(), targets: new Map(), contexts: new WeakMap(), decoded: new Map(), needsPass: false,
  };
  const placements: ObjectPlacement[] = [];
  const activate = (): void => { context.needsPass = true; };

  walkObjectValues(root, element => {
    if (element.kind === "object" || boundFieldMetadata(element)) activate();
    if (element.kind !== "object") return;
    const objectElement = element as ObjectElement;
    const handle = objectElement.props?.handle as { id?: unknown } | undefined;
    const id = typeof handle?.id === "string" ? handle.id : "<invalid>";
    let handleDefinition: string;
    try { handleDefinition = handleDefinitionIdentity(objectElement.props?.handle as never); }
    catch { return objectError(id, "handle is missing or unbranded", element as object, "handle"); }
    const definition = objectElement.props?.definition;
    if (!definition || handleDefinition !== definition.definitionId)
      return objectError(id, "handle does not belong to this definition", element as object, "handle");
    if (!idPattern.test(id)) return objectError(id, "invalid instance ID", element as object, "handle");
    const existing = context.objects.get(id);
    if (existing) return duplicateObjectError(id, element as object, existing.element);
    const placement: ObjectPlacement = {
      id, definition, element: element as object, exports: new Map(),
    };
    context.objects.set(id, placement);
    placements.push(placement);
  });

  // Inspect all values separately from the object walk so reserved tokens activate the pass too.
  scanValues(root, new WeakSet(), () => {}, activate);
  if (!context.needsPass) return context;

  for (const placement of placements) {
    const body = (placement.element as ObjectElement).props?.body;
    const roots = rootsForBody(body, placement.id, placement.id, context.contexts, `$.objects[${placement.id}]`);
    if (roots.length !== 1 || !isElement(roots[0]) || roots[0].kind !== "slot")
      objectError(placement.id, "body must contain exactly one root Slot", placement.definition, "body");
    if (containsObject(body)) objectError(placement.id, "nested Objects are not supported in roadmap 5", placement.definition, "body");
  }

  const reservedIds = [...context.objects.keys()];
  const slotKeys = new Map<string, string>();
  const componentKeys = new Map<string, string>();
  const aliases = new Map<string, object>();
  const scopeKeys = new Set<string>();
  const pendingAliases: Array<{ alias: string; member: string; componentKey: string; componentType?: string;
    owner?: string; element: object }> = [];

  function reserved(key: string, owner: string | undefined, element: object, scopeNamespace = false): void {
    if (owner !== undefined) return;
    const id = reservedIds.find(candidate => key.startsWith(`${candidate}::`) || scopeNamespace && key === candidate);
    if (id) throw new BuildError("DUPLICATE_KEY", `Stable key '${key}' occupies reserved Object namespace '${id}::'.`);
    void element;
  }

  function registerStable(kind: "slot" | "component", key: string, path: string,
    element: object, owner: string | undefined): void {
    reserved(key, owner, element);
    const own = kind === "slot" ? slotKeys : componentKeys;
    const other = kind === "slot" ? componentKeys : slotKeys;
    const first = own.get(key);
    if (first !== undefined)
      throw new BuildError("DUPLICATE_KEY", `${kind} key "${key}" is used more than once (first at ${first}, again at ${path})`);
    if (key.includes("::") && other.has(key))
      throw new BuildError("DUPLICATE_KEY", `Scoped stable key '${key}' is used by both a Slot and a Component`);
    own.set(key, path);
    context.targets.set(`${kind}:${key}`, { key, kind, owner, element });
  }

  function registerAlias(alias: string, member: string, componentKey: string | undefined,
    componentType: string | undefined, element: object, owner: string | undefined): void {
    reserved(alias, owner, element);
    const first = aliases.get(alias);
    if (first) throw new BuildError("APPLY_FIELD_ALIAS_DUPLICATE", `Field alias '${alias}' is declared more than once`);
    aliases.set(alias, element);
    if (componentKey === undefined) return;
    const memberTarget = context.targets.get(`member:${componentKey}.${member}`);
    if (memberTarget) {
      context.targets.set(`field:${alias}`, { key: alias, kind: "field", owner, element,
        componentType, member });
      return;
    }
    pendingAliases.push({ alias, member, componentKey, componentType, owner, element });
  }

  function fieldChildren(value: unknown, componentKey: string | undefined, componentType: string | undefined,
    scope: string, owner: string | undefined, path: string, members: Set<string>, fieldDeclarations: Set<string>): void {
    if (value === null || value === undefined || typeof value === "boolean") return;
    if (Array.isArray(value)) {
      value.forEach((item, index) => fieldChildren(item, componentKey, componentType, scope, owner,
        `${path}[${index}]`, members, fieldDeclarations));
      return;
    }
    if (!isElement(value) || value.kind === "fragment") {
      if (isElement(value) && value.kind === "fragment") {
        context.contexts.set(value, { scope, owner });
        fieldChildren(value.props?.children, componentKey, componentType, scope, owner, path, members, fieldDeclarations);
      }
      return;
    }
    if (value.kind !== "field") return;
    context.contexts.set(value, { scope, owner });
    const props = value.props ?? {};
    const member = props.name;
    if (typeof member !== "string" || !member.trim()) return;
    if (fieldDeclarations.has(member))
      throw new BuildError("APPLY_FIELD_DUPLICATE", `Member '${member}' at ${path} is declared more than once`);
    members.add(member);
    fieldDeclarations.add(member);
    if (componentKey !== undefined)
      context.targets.set(`member:${componentKey}.${member}`, { key: `${componentKey}.${member}`, kind: "member",
        owner, element: value, componentType, member });
    if (props.key !== undefined) {
      if (typeof props.key === "string") reserved(scope ? `${scope}::${props.key}` : props.key, owner, value);
      const alias = scopedKey(scope, props.key, `${path}.fieldAliases`, value, "key");
      registerAlias(alias, member, componentKey, componentType, value, owner);
    }
  }

  function walk(value: unknown, scope: string, owner: string | undefined, path: string): void {
    if (value === null || value === undefined || typeof value === "boolean") return;
    if (Array.isArray(value)) {
      value.forEach((item, index) => walk(item, scope, owner, `${path}[${index}]`));
      return;
    }
    if (!isElement(value)) return;
    context.contexts.set(value, { scope, owner });
    if (value.kind === "fragment") { walk(value.props?.children, scope, owner, path); return; }
    if (value.kind === "scope") {
      const nested = scopedKey(scope, value.props?.instanceKey, `${path}.instanceKey`, value, "instanceKey");
      reserved(nested, owner, value, true);
      if (scopeKeys.has(nested)) throw new BuildError("DUPLICATE_KEY", `Instance scope '${nested}' is repeated at ${path}`);
      scopeKeys.add(nested);
      context.contexts.set(value, { scope: nested, owner });
      walk(value.props?.children, nested, owner, path);
      return;
    }
    if (value.kind === "object") {
      const placement = [...context.objects.values()].find(item => item.element === value);
      if (placement) walk((value as ObjectElement).props?.body, placement.id, placement.id, `$.objects[${placement.id}]`);
      return;
    }
    const props = value.props ?? {};
    if (value.kind === "slot" || value.kind === "component") {
      const keyValue = props.key;
      let key: string | undefined;
      if (scope || owner !== undefined) key = scopedKey(scope, keyValue, path, value, "key");
      else if (typeof keyValue === "string" && keyValue.trim().length > 0) key = keyValue;
      if (key !== undefined) registerStable(value.kind, key, path, value, owner);
      if (value.kind === "slot") {
        const children = flattenEntries(props.children, scope, owner, context.contexts, `${path}.children`,
          (scopeElement, parentScope, parentOwner, scopePath) => {
            const nested = scopedKey(parentScope, scopeElement.props?.instanceKey,
              `${scopePath}.instanceKey`, scopeElement, "instanceKey");
            reserved(nested, parentOwner, scopeElement, true);
          });
        let componentRank = 0;
        let slotRank = 0;
        children.forEach(child => {
          if (child.element.kind === "component") walk(child.element, child.scope, child.owner, `${path}.components[${componentRank++}]`);
          else if (child.element.kind === "slot") walk(child.element, child.scope, child.owner, `${path}.children[${slotRank++}]`);
          else walk(child.element, child.scope, child.owner, `${path}.children[${slotRank++}]`);
        });
        return;
      }
      const componentKey = key;
      const componentType = typeof props.type === "string" ? props.type : undefined;
      const fieldDeclarations = new Set<string>();
      for (const section of [props.fields, props.initialFields])
        if (section !== null && typeof section === "object" && !Array.isArray(section))
          Object.keys(section).forEach(member => fieldDeclarations.add(member));
      const members = new Set(fieldDeclarations);
      if (props.propertyModes !== null && typeof props.propertyModes === "object" && !Array.isArray(props.propertyModes))
        Object.keys(props.propertyModes).forEach(member => members.add(member));
      if (componentKey !== undefined) {
        context.targets.set(`component:${componentKey}`, { key: componentKey, kind: "component", owner, element: value });
        for (const member of members)
          context.targets.set(`member:${componentKey}.${member}`, { key: `${componentKey}.${member}`, kind: "member",
            owner, element: value, componentType, member });
      }
      if (props.fieldAliases && typeof props.fieldAliases === "object" && !Array.isArray(props.fieldAliases))
        for (const [alias, member] of Object.entries(props.fieldAliases))
          if (typeof member === "string") registerAlias(scope ? scopedKey(scope, alias, `${path}.fieldAliases`, value, "fieldAliases") : alias,
            member, componentKey, componentType, value, owner);
      fieldChildren(props.children, componentKey, componentType, scope, owner, path, members, fieldDeclarations);
    }
  }

  walk(root, "", undefined, "$");

  for (const alias of pendingAliases) {
    if (!context.targets.has(`member:${alias.componentKey}.${alias.member}`)) continue;
    context.targets.set(`field:${alias.alias}`, { key: alias.alias, kind: "field", owner: alias.owner,
      element: alias.element, componentType: alias.componentType, member: alias.member });
  }

  return context;
}

/** Expand roadmap-5 Objects into absolute, source-only scopes before evaluation. */
export function expandObjects(root: unknown, catalogs: readonly CatalogRuntimeTable[]): unknown {
  const context = collectObjects(root, catalogs);
  if (!context.needsPass) return root;
  validateObjectExports(context);
  validateObjectBoundaries(context);

  function expand(value: unknown): unknown {
    if (Array.isArray(value)) return value.map(expand);
    if (!isElement(value)) return value;
    const props = value.props ?? {};
    if (value.kind === "object") {
      const handle = props.handle as { id?: unknown } | undefined;
      const id = typeof handle?.id === "string" ? handle.id : "<invalid>";
      const scopeElement = {
        kind: "scope", absoluteScope: true,
        props: { instanceKey: id, children: expand(props.body) },
      };
      copyOrigin(value, scopeElement);
      return scopeElement;
    }
    const childBearing = value.kind === "slot" || value.kind === "component" ||
      value.kind === "fragment" || value.kind === "scope";
    if (!childBearing || props.children === undefined) return value;
    const children = expand(props.children);
    if (children === props.children) return value;
    const clone = { ...value, props: { ...props, children } };
    copyOrigin(value, clone);
    copyBoundFieldMetadata(value, clone);
    return clone;
  }

  return expand(context.root);
}
