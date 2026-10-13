import { BuildError } from "./evaluate.js";
import type { CatalogRuntimeTable } from "./catalog-runtime.js";
import { catalogMember, verifyRuntimeTable } from "./catalog-runtime.js";
import { boundFieldMetadata, copyBoundFieldMetadata } from "./bound-field.js";
import { fieldBindingDescriptor, variableRefDescriptor, variableSetDescriptor } from "./variables-internal.js";
import { copyOrigin, markBuildError, markBuildErrorOrigin, originOfElement, unknown } from "./source-map.js";
import { scopeKey } from "./scope.js";

type Element = { kind: string; scope?: string; absoluteScope?: boolean; props?: Record<string, any> | null };
type SlotInfo = { element: Element; parent?: SlotInfo; scope: string; localKey?: string; path: string; space?: Element };
type Declaration = ReturnType<typeof variableSetDescriptor> extends infer T ? Exclude<T, undefined> : never;
type Placement = { set: object; descriptor: Declaration; owner: SlotInfo; element: Element; scope: string; path: string; container?: Element };
type Binding = { element: Element; component: Element; slot?: SlotInfo; container?: Element; descriptor: NonNullable<ReturnType<typeof fieldBindingDescriptor>>; bound: NonNullable<ReturnType<typeof boundFieldMetadata>>; scope: string; path: string };

const SPACE = "[FrooxEngine]FrooxEngine.DynamicVariableSpace";
const PROVIDER = "[FrooxEngine]FrooxEngine.DynamicValueVariable<";
const DRIVER = "[FrooxEngine]FrooxEngine.DynamicValueVariableDriver<bool>";
const DYNAMIC_FIELD = "[FrooxEngine]FrooxEngine.DynamicField<bool>";
const identity = (value: unknown): value is object => value !== null && (typeof value === "object" || typeof value === "function");
const isElement = (value: unknown): value is Element => identity(value) && !Array.isArray(value) && typeof (value as any).kind === "string";

function fail(code: string, message: string, element?: object, attribute?: string, useValueSource = false): never {
  const error = new BuildError(code, message);
  const origin = element ? originOfElement(element) : undefined;
  const detail = attribute ? origin?.attributes[attribute] : origin?.attributes.declaration;
  const source = attribute === "value" || useValueSource ? detail?.valueSource ?? unknown : detail?.source ?? unknown;
  markBuildErrorOrigin(error, { source, valueSource: source, related: [...(detail?.related ?? []), ...(origin?.related ?? [])] });
  throw error;
}

function canonicalType(type: unknown, catalogs: readonly CatalogRuntimeTable[]): string | undefined {
  if (typeof type !== "string") return undefined;
  if (type === SPACE || type === "FrooxEngine.DynamicVariableSpace") return SPACE;
  if (type.startsWith(PROVIDER) && type.endsWith(">")) return type;
  if (type.startsWith("FrooxEngine.DynamicValueVariable<") && type.endsWith(">")) return type;
  for (const candidate of catalogs) {
    const table = verifyRuntimeTable(candidate);
    const entry = table.types[type];
    if (entry?.confirmed) return entry.canonicalType;
  }
  return undefined;
}

function tuple(parts: string[]): string {
  return parts.map(part => { const encoded = encodeURIComponent(part); return `${encoded.length}_${encoded}`; }).join("_");
}

function copyElement(original: Element, props: Record<string, any>): Element {
  const clone = { ...original, props };
  copyOrigin(original, clone);
  copyBoundFieldMetadata(original, clone);
  return clone;
}

/** Expand authenticated Variable declarations and catalog-bound Field bindings without mutating author nodes. */
export function expandVariables(root: unknown, catalogs: readonly CatalogRuntimeTable[]): unknown {
  function findToken(value: unknown, seen = new WeakSet<object>()):
    { kind: "ref"; descriptor: NonNullable<ReturnType<typeof variableRefDescriptor>> } |
    { kind: "binding"; descriptor: NonNullable<ReturnType<typeof fieldBindingDescriptor>> } | undefined {
    if (!identity(value) || seen.has(value)) return undefined;
    seen.add(value);
    const ref = variableRefDescriptor(value);
    if (ref) return { kind: "ref", descriptor: ref };
    const binding = fieldBindingDescriptor(value);
    if (binding) return { kind: "binding", descriptor: binding };
    if (Array.isArray(value)) {
      for (const child of value) { const found = findToken(child, seen); if (found) return found; }
      return undefined;
    }
    for (const child of Object.values(value)) { const found = findToken(child, seen); if (found) return found; }
    return undefined;
  }
  function ordinaryProps(element: Element): [string, unknown][] {
    const ignored = new Set(["children"]);
    if (element.kind === "variables") ignored.add("declaration");
    if (element.kind === "field") ignored.add("value");
    if (element.kind === "component") { ignored.add("fields"); ignored.add("initialFields"); }
    return Object.entries(element.props ?? {}).filter(([name]) => !ignored.has(name));
  }
  function hasVariableApi(value: unknown, seen = new WeakSet<object>()): boolean {
    if (value === null || typeof value !== "object") return false;
    if (seen.has(value)) return false;
    seen.add(value);
    if (Array.isArray(value)) return value.some(item => hasVariableApi(item, seen));
    if (!isElement(value)) return false;
    if (value.kind === "variables" && variableSetDescriptor(value.props?.declaration)) return true;
    const props = value.props ?? {};
    if (ordinaryProps(value).some(([, prop]) => !!findToken(prop))) return true;
    if (value.kind === "variables") return false;
    if (value.kind === "field") return !!findToken(props.value);
    if (value.kind === "component" && (findToken(props.fields) || findToken(props.initialFields))) return true;
    if (["fragment", "scope", "slot", "component"].includes(value.kind)) return hasVariableApi(props.children, seen);
    return false;
  }
  if (!hasVariableApi(root)) return root;
  const placements: Placement[] = [];
  const bindings: Binding[] = [];
  const slots: SlotInfo[] = [];
  let active = false;
  const activeNodes = new WeakSet<object>();

  function walk(value: unknown, parentSlot: SlotInfo | undefined, scope: string, component: Element | undefined, path: string, container?: Element): void {
    if (value === null || value === undefined || typeof value === "boolean") return;
    if (Array.isArray(value)) { value.forEach((item, index) => walk(item, parentSlot, scope, component, `${path}[${index}]`, container)); return; }
    if (!isElement(value) || activeNodes.has(value)) return;
    activeNodes.add(value);
    for (const [attribute, prop] of ordinaryProps(value)) {
      const token = findToken(prop);
      if (!token) continue;
      active = true;
      if (token.kind === "ref")
        fail("CREATION_VARIABLE_UNRESOLVED", `Variable '${token.descriptor.scopeKey}.${token.descriptor.name}' is unresolved or outside its declared Space.`, value, attribute, true);
      fail("CREATION_VARIABLE_TYPE_UNSUPPORTED", "Variable bindings must be the value of a catalog-bound Field.", value, attribute, true);
    }
    if (value.kind === "fragment") { walk(value.props?.children, parentSlot, scope, component, path, component ? container : value); activeNodes.delete(value); return; }
    if (value.kind === "scope") {
      let nested: string;
      try { nested = scopeKey(value.absoluteScope === true ? "" : scope, value.props?.instanceKey, `${path}.instanceKey`); }
      catch (error) { if (error instanceof Error) { markBuildError(error, value, "instanceKey"); throw error; } throw error; }
      walk(value.props?.children, parentSlot, nested, component, path, value); activeNodes.delete(value); return;
    }
    if (value.kind === "slot") {
      const props = value.props ?? {};
      const localKey = typeof props.key === "string" && props.key.trim() ? props.key : undefined;
      const info: SlotInfo = { element: value, parent: parentSlot, scope, localKey, path };
      slots.push(info);
      walk(props.children, info, scope, undefined, `${path}.children`);
      activeNodes.delete(value);
      return;
    }
    if (value.kind === "variables" && variableSetDescriptor(value.props?.declaration)) {
      active = true;
      const descriptor = variableSetDescriptor(value.props?.declaration);
      if (!descriptor) fail("CREATION_VARIABLE_UNRESOLVED", "Variable declaration is unresolved; use defineVariables(...).", value, "declaration");
      if (!parentSlot) fail("CREATION_VARIABLE_UNRESOLVED", `Variable '${descriptor.scopeKey}.<set>' is unresolved or outside its declared Space.`, value);
      placements.push({ set: descriptor.set, descriptor, owner: parentSlot, element: value, scope, path, container });
      activeNodes.delete(value);
      return;
    }
    if (value.kind === "component") {
      const componentProps = value.props ?? {};
      const token = findToken(componentProps.fields) ?? findToken(componentProps.initialFields);
      if (token?.kind === "ref") {
        const descriptor = token.descriptor;
        fail("CREATION_VARIABLE_UNRESOLVED", `Variable '${descriptor.scopeKey}.${descriptor.name}' is unresolved or outside its declared Space.`, value);
      }
      if (token) {
        active = true;
        fail("CREATION_VARIABLE_TYPE_UNSUPPORTED", "Variable bindings must be the value of a catalog-bound Field.", value);
      }
      const next = value;
      walk(value.props?.children, parentSlot, scope, next, `${path}.children`, container);
      activeNodes.delete(value);
      return;
    }
    if (value.kind === "field") {
      const valueToken = value.props?.value;
      const binding = fieldBindingDescriptor(valueToken);
      const bare = variableRefDescriptor(valueToken);
      const nestedToken = findToken(valueToken);
      if (nestedToken) active = true;
      if (bare && !binding) fail("CREATION_VARIABLE_UNRESOLVED", "Variable reference requires drive(...) or bind(...).", value, "value");
      if (!bare && !binding && nestedToken?.kind === "ref")
        fail("CREATION_VARIABLE_UNRESOLVED", `Variable '${nestedToken.descriptor.scopeKey}.${nestedToken.descriptor.name}' is unresolved or outside its declared Space.`, value, "value");
      if (!bare && !binding && nestedToken?.kind === "binding")
        fail("CREATION_VARIABLE_TYPE_UNSUPPORTED", "Variable bindings must be the value of a catalog-bound Field.", value, "value");
      if (binding) {
        if (!component || !parentSlot) fail("CREATION_VARIABLE_TYPE_UNSUPPORTED", "Variable bindings must be the value of a catalog-bound Field.", value, "value");
        const bound = boundFieldMetadata(value);
        if (!bound) fail("CREATION_VARIABLE_TYPE_UNSUPPORTED", "Variable bindings must be the value of a catalog-bound Field.", value, "value");
        bindings.push({ element: value, component, slot: parentSlot, container, descriptor: binding, bound, scope, path });
      }
      activeNodes.delete(value);
      return;
    }
    const props = value.props ?? {};
    const type = canonicalType(props.type, catalogs);
    if (type === SPACE) active = true;
    if (type?.startsWith(PROVIDER) || type?.startsWith("FrooxEngine.DynamicValueVariable<")) active = true;
    // Detect token leakage in ordinary JSON-bearing props without traversing element children twice.
    walk(props.children, parentSlot, scope, component ?? value, `${path}.children`, container);
    activeNodes.delete(value);
  }
  walk(root, undefined, "", undefined, "$");
  if (!active) return root;

  const placementBySet = new Map<object, Placement>();
  const placementByOwner = new Map<SlotInfo, Placement>();
  const declarationByRef = new Map<object, Placement>();
  for (const placement of placements) {
    const prior = placementBySet.get(placement.set);
    if (prior) fail("CREATION_VARIABLE_DUPLICATE", `Variable declaration '${placement.descriptor.scopeKey}' occurs more than once in a document or Slot.`, placement.element, "declaration");
    placementBySet.set(placement.set, placement);
    const ownerKey = placement.owner.localKey;
    if (!ownerKey) fail("EXPLICIT_KEY_REQUIRED", `slot "${placement.owner.element.props?.name}" at ${placement.owner.path} requires an explicit 'key' prop`, placement.owner.element);
    const sibling = placementByOwner.get(placement.owner);
    if (sibling) fail("CREATION_VARIABLE_DUPLICATE", `Variable declaration '${placement.descriptor.scopeKey}' occurs more than once in a document or Slot.`, placement.element, "declaration");
    placementByOwner.set(placement.owner, placement);
    for (const variable of placement.descriptor.variables) {
      declarationByRef.set(variable.set, placement);
    }
  }

  // Index raw Spaces and providers by physical Slot identity, not by SpaceName text.
  for (const slot of slots) {
    const components = directElements(slot.element.props?.children).filter(child => child.kind === "component");
    for (const component of components) if (canonicalType(component.props?.type, catalogs) === SPACE) slot.space = component;
  }
  for (const placement of placements) {
    if (placement.owner.space) fail("CREATION_SPACE_SHADOWED", `Variable Space '${placement.descriptor.scopeKey}' is shadowed by a DynamicVariableSpace.`, placement.element, "declaration");
    if (slots.some(slot => slot.parent === placement.owner && slot.element.props?.name === "DynamicVariables" && slot.space))
      fail("CREATION_SPACE_SHADOWED", `Variable Space '${placement.descriptor.scopeKey}' is shadowed by a DynamicVariableSpace.`, placement.element, "declaration");
  }
  const rawNames = new Map<SlotInfo, Map<string, Element>>();
  for (const slot of slots) {
    const direct = directElements(slot.element.props?.children);
    for (const component of direct.filter(child => child.kind === "component")) {
      const type = canonicalType(component.props?.type, catalogs);
      if (!type?.startsWith(PROVIDER) && !type?.startsWith("FrooxEngine.DynamicValueVariable<")) continue;
      const name = componentFieldValue(component, "VariableName");
      if (typeof name !== "string") continue;
      const space = nearestNamespace(slot);
      if (!space) continue;
      const names = rawNames.get(space) ?? new Map<string, Element>();
      const spaceName = (space.space && componentFieldValue(space.space, "SpaceName")) ??
        placements.find(placement => placement.owner === space)?.descriptor.scopeKey ?? space.scope;
      if (names.has(name)) fail("CREATION_VARIABLE_DUPLICATE", `Variable '${spaceName}.${name}' is declared more than once in a Space.`, component);
      names.set(name, component); rawNames.set(space, names);
    }
  }

  const wireByField = new Map<Element, Binding>();
  const wiresBySlot = new Map<SlotInfo, Binding[]>();
  for (const binding of bindings) {
    const { descriptor, bound, slot, component } = binding;
    const currentMember = binding.element.props?.name;
    if (currentMember !== bound.member) {
      if (typeof currentMember === "string") {
        for (const candidate of catalogs) {
          let table: CatalogRuntimeTable;
          try { table = verifyRuntimeTable(candidate); }
          catch { continue; }
          if (table.contentHash !== bound.catalogHash) continue;
          let currentDescriptor: ReturnType<typeof catalogMember>;
          try { currentDescriptor = catalogMember(table, bound.type, currentMember); }
          catch { continue; }
          if (currentDescriptor.kind === "field" && currentDescriptor.confirmed === true &&
              currentDescriptor.scalar !== null && currentDescriptor.scalar !== "boolean" &&
              typeof currentDescriptor.fieldInterface === "string")
            fail("CREATION_VARIABLE_TYPE_MISMATCH", `Variable 'bool' cannot bind to Field '${bound.type}.${currentMember}' of type '${currentDescriptor.scalar}'.`, binding.element, "value");
        }
      }
      const memberName = typeof currentMember === "string" ? currentMember : String(currentMember);
      fail("CREATION_VARIABLE_TYPE_UNSUPPORTED", `Variable binding target '${bound.type}.${memberName}' requires a confirmed bool Field.`, binding.element, "value");
    }
    if (bound.descriptor.kind === "field" && bound.descriptor.confirmed === true && bound.descriptor.scalar !== null &&
        bound.descriptor.scalar !== "boolean" && typeof bound.descriptor.fieldInterface === "string")
      fail("CREATION_VARIABLE_TYPE_MISMATCH", `Variable 'bool' cannot bind to Field '${bound.type}.${bound.member}' of type '${bound.descriptor.scalar}'.`, binding.element, "value");
    if (bound.descriptor.kind !== "field" || bound.descriptor.confirmed !== true || bound.descriptor.scalar !== "boolean" || !bound.descriptor.fieldInterface)
      fail("CREATION_VARIABLE_TYPE_UNSUPPORTED", `Variable binding target '${bound.type}.${bound.member}' requires a confirmed bool Field.`, binding.element, "value");
    if (!slot || !binding.element.props?.name) fail("CREATION_VARIABLE_TYPE_UNSUPPORTED", "Variable bindings must be the value of a catalog-bound Field.", binding.element, "value");
    const found = declarationByRef.get(descriptor.set);
    if (!found || !found.descriptor.variables.some(variable => variable.name === descriptor.name))
      fail("CREATION_VARIABLE_UNRESOLVED", `Variable '${descriptor.scopeKey}.${descriptor.name}' is unresolved or outside its declared Space.`, binding.element, "value");
    let ancestor: SlotInfo | undefined = slot;
    let ownerIsAncestor = false;
    while (ancestor) {
      if (ancestor !== found.owner && placements.some(other => other.owner === ancestor))
        fail("CREATION_VARIABLE_UNRESOLVED", `Variable '${descriptor.scopeKey}.${descriptor.name}' is unresolved or outside its declared Space.`, binding.element, "value");
      if (ancestor === found.owner) { ownerIsAncestor = true; break; }
      ancestor = ancestor.parent;
    }
    if (!ownerIsAncestor) fail("CREATION_VARIABLE_UNRESOLVED", `Variable '${descriptor.scopeKey}.${descriptor.name}' is unresolved or outside its declared Space.`, binding.element, "value");
    let shadow = false;
    for (let current: SlotInfo | undefined = slot; current; current = current.parent) {
      if (current.space) { shadow = true; break; }
      if (current === found.owner) break;
    }
    if (shadow) fail("CREATION_SPACE_SHADOWED", `Variable Space '${descriptor.scopeKey}' is shadowed by a DynamicVariableSpace.`, binding.element, "value");
    const memberName = binding.element.props?.name;
    const mapped = [component.props?.fields, component.props?.initialFields].some(section =>
      section !== null && typeof section === "object" && Object.hasOwn(section, memberName));
    const targetFields = directElements(component.props?.children).filter(child => child.kind === "field" && child.props?.name === memberName);
    if (mapped || targetFields.length > 1) fail("CREATION_DRIVEN_FIELD_DECLARED", `Variable binding target '${memberName}' is declared more than once.`, binding.element, "value");
    const priorWire = wireByField.get(binding.element);
    if (priorWire) fail("CREATION_DRIVEN_FIELD_DECLARED", `Variable binding target '${binding.element.props?.name}' is declared more than once.`, binding.element, "value");
    wireByField.set(binding.element, binding);
    const list = wiresBySlot.get(slot) ?? []; list.push(binding); wiresBySlot.set(slot, list);
  }

  for (const placement of placements) {
    const names = rawNames.get(placement.owner) ?? new Map<string, Element>();
    for (const variable of placement.descriptor.variables) if (names.has(variable.name))
      fail("CREATION_VARIABLE_DUPLICATE", `Variable '${placement.descriptor.scopeKey}.${variable.name}' is declared more than once in a Space.`, placement.element, "declaration");
  }

  function directElements(value: unknown): Element[] {
    if (value === null || value === undefined || typeof value === "boolean") return [];
    if (Array.isArray(value)) return value.flatMap(directElements);
    if (!isElement(value)) return [];
    if (value.kind === "fragment" || value.kind === "scope") return directElements(value.props?.children);
    return [value];
  }
  function componentFieldValue(component: Element, member: string): unknown {
    const props = component.props ?? {};
    if (props.fields !== null && typeof props.fields === "object" && Object.hasOwn(props.fields, member)) return props.fields[member];
    if (props.initialFields !== null && typeof props.initialFields === "object" && Object.hasOwn(props.initialFields, member)) return props.initialFields[member];
    return directElements(props.children).find(field => field.kind === "field" && field.props?.name === member)?.props?.value;
  }

  function nearestNamespace(slot: SlotInfo): SlotInfo | undefined {
    for (let current: SlotInfo | undefined = slot; current; current = current.parent) {
      if (current.space) return current;
      if (placements.some(placement => placement.owner === current)) return current;
    }
    return undefined;
  }

  const generatedBySlot = new Map<SlotInfo, Element[]>();
  const generatedByContainer = new Map<Element, Element[]>();
  function addGenerated(slot: SlotInfo, container: Element | undefined, elements: Element[]): void {
    if (container) {
      const existing = generatedByContainer.get(container) ?? []; existing.push(...elements); generatedByContainer.set(container, existing);
    } else {
      const existing = generatedBySlot.get(slot) ?? []; existing.push(...elements); generatedBySlot.set(slot, existing);
    }
  }
  function generatedSlot(key: string, name: string, children: unknown[]): Element {
    return { kind: "slot", props: { key, name, children } };
  }
  for (const placement of placements) {
    const { descriptor, owner } = placement;
    const local = owner.localKey!;
    const spaceKey = `__rlv_${tuple(["space", descriptor.scopeKey, local])}`;
    const providerSlotKey = `__rlv_${tuple(["variables", descriptor.scopeKey, local])}`;
    const providers = descriptor.variables.map(variable => ({ kind: "component", props: {
      key: `__rlv_${tuple(["variable", descriptor.scopeKey, local, variable.name])}`,
      type: PROVIDER + "bool>", fields: { VariableName: variable.name, Value: variable.defaultValue },
      identityFields: ["VariableName"],
    } } as Element));
    const space = { kind: "component", props: { key: spaceKey, type: SPACE,
      fields: { SpaceName: descriptor.scopeKey, OnlyDirectBinding: false } } } as Element;
    const providerSlot = generatedSlot(providerSlotKey, "DynamicVariables", providers);
    addGenerated(owner, placement.container, [space, providerSlot]);
  }
  for (const [slot, slotBindings] of wiresBySlot) for (const binding of slotBindings) {
    const descriptor = binding.descriptor;
    if (typeof binding.component.props?.key !== "string" || binding.component.props.key.trim().length === 0) {
      const componentPath = binding.path.replace(/\.children(?:\[\d+\])*$/, "");
      fail("EXPLICIT_KEY_REQUIRED", `component "${binding.component.props?.type}" at ${componentPath} requires an explicit 'key' prop`, binding.component);
    }
    const localTargetKey = binding.component.props!.key as string;
    const targetKey = binding.scope
      ? scopeKey(binding.scope, localTargetKey, `${binding.path}.component.key`)
      : localTargetKey;
    const member = binding.element.props?.name as string;
    const wireTuple = ["wire", descriptor.scopeKey, descriptor.name, targetKey, member];
    const slotKey = `__rlv_${tuple(wireTuple)}`;
    const childKey = `__rlv_${tuple(["binding", descriptor.scopeKey, descriptor.name, targetKey, member])}`;
    const target = `$member:${targetKey}.${member}`;
    const component = { kind: "component", props: { key: childKey,
      type: descriptor.mode === "drive" ? DRIVER : DYNAMIC_FIELD,
      fields: descriptor.mode === "drive"
        ? { VariableName: descriptor.name, DefaultValue: descriptor.defaultValue, Target: target }
        : { VariableName: descriptor.name, TargetField: target },
    } } as Element;
    const wireSlot = generatedSlot(slotKey, slotKey, [component]);
    addGenerated(slot, binding.container, [wireSlot]);
  }

  const bindingByField = new WeakMap<object, Binding>();
  const drivenModes = new WeakMap<object, Record<string, "driver-owned" | "runtime">>();
  for (const binding of bindings) {
    bindingByField.set(binding.element, binding);
    const modes = drivenModes.get(binding.component) ?? {};
    modes[binding.element.props?.name] = binding.descriptor.mode === "drive" ? "driver-owned" : "runtime";
    drivenModes.set(binding.component, modes);
  }
  const visiting = new WeakMap<object, unknown>();
  function transform(value: unknown, parentSlot?: SlotInfo): unknown {
    if (Array.isArray(value)) return value.map(item => transform(item, parentSlot));
    if (!isElement(value)) return value;
    if (value.kind === "variables" && variableSetDescriptor(value.props?.declaration)) return undefined;
    const fieldBinding = value.kind === "field" ? bindingByField.get(value) : undefined;
    if (fieldBinding) {
      const props = value.props ?? {};
      const clone = copyElement(value, { ...props, value: undefined, mode: fieldBinding.descriptor.mode === "drive" ? "driver-owned" : "runtime" });
      return clone;
    }
    const cached = visiting.get(value); if (cached !== undefined) return cached;
    const slot = value.kind === "slot" ? slots.find(item => item.element === value) : parentSlot;
    const props = value.props ?? {};
    if (props.children === undefined) return value;
    const transformed = transformChildren(props.children, slot);
    const additions = value.kind === "slot" && slot ? generatedBySlot.get(slot) ?? [] :
      (value.kind === "scope" || value.kind === "fragment") ? generatedByContainer.get(value) ?? [] : [];
    const all = [...transformed, ...additions];
    const modes = value.kind === "component" ? drivenModes.get(value) : undefined;
    const clone = copyElement(value, { ...props,
      ...(modes ? { propertyModes: { ...props.propertyModes, ...modes } } : {}),
      children: Array.isArray(props.children) ? all : transformed.length === 1 && additions.length === 0 ? transformed[0] : all });
    visiting.set(value, clone);
    return clone;
  }
  function transformChildren(value: unknown, parentSlot?: SlotInfo): unknown[] {
    if (value === null || value === undefined || typeof value === "boolean") return [];
    if (Array.isArray(value)) return value.flatMap(item => transformChildren(item, parentSlot));
    if (!isElement(value)) return [value];
    if (value.kind === "variables" && variableSetDescriptor(value.props?.declaration)) return [];
    const transformed = transform(value, parentSlot);
    return transformed === undefined ? [] : [transformed];
  }
  const result = transform(root);
  return result;
}
