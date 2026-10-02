// Evaluates a resolved resoloop-jsx element tree into a schema-v1
// ApplyDocument object (the JSON shape consumed by `resoloop validate` /
// `resoloop apply`, see src/RLoop.Core/ApplyWorkflow.cs).

import type { ApplyDocument, ApplyOwnershipSpec, ApplySlotSpec, ApplyComponentSpec, ApplyNodeSpec } from "./generated/apply-types.js";
import { copyComponent, copySlot } from "./generated/apply-copy.js";
export type { ApplyDocument, ApplyOwnershipSpec, ApplySlotSpec, ApplyComponentSpec, ApplyNodeSpec } from "./generated/apply-types.js";
import { copyOrigin, bindIr, markBuildError, markBuildErrorOrigin, originOfElement, bindIrOrigin, unknown, type Origin } from "./source-map.js";
import { scopeKey, scopeValue } from "./scope.js";

/** Tagged build error; `code` is printed by the CLI as `code: message`
 *  and maps to exit code 1. */
export class BuildError extends Error {
  readonly code: string;
  constructor(code: string, message: string) {
    super(message);
    this.name = "BuildError";
    this.code = code;
  }
}

/* ---- element internals ---- */

interface RawElement {
  kind: string;
  scope?: string;
  props?: Record<string, any> | null;
}

function isElement(value: any): value is RawElement {
  return (
    value !== null &&
    typeof value === "object" &&
    !Array.isArray(value) &&
    typeof value.kind === "string"
  );
}

function describeValue(value: any): string {
  if (value === null) return "null";
  if (Array.isArray(value)) return "array";
  return `${typeof value} ${JSON.stringify(value) ?? String(value)}`;
}

/** Recursively flatten a `children` value: arrays are inlined, fragments
 *  contribute their own children, falsy values are dropped. Anything else
 *  that is not a slot/component element is a hard error. */
function flattenChildren(value: any, path: string, out: RawElement[], scope = "", scopes = new Set<string>()): void {
  if (value === null || value === undefined || value === false || value === true)
    return;
  if (Array.isArray(value)) {
    for (const item of value) flattenChildren(item, path, out, scope, scopes);
    return;
  }
  if (isElement(value)) {
    if (value.kind === "fragment") {
      flattenChildren(value.props?.children, path, out, scope, scopes);
      return;
    }
    if (value.kind === "scope") {
      let nested: string;
      try { nested = scopeKey(scope, value.props?.instanceKey, `${path}.instanceKey`); }
      catch (error) { if (error instanceof Error) markBuildError(error, value, "instanceKey"); throw error; }
      if (scopes.has(nested)) {
        const error = new BuildError("DUPLICATE_KEY", `Instance scope '${nested}' is repeated at ${path}`);
        markBuildError(error, value, "instanceKey"); throw error;
      }
      scopes.add(nested);
      flattenChildren(value.props?.children, path, out, nested, scopes);
      return;
    }
    if (value.kind === "slot" || value.kind === "component") {
      const copy = { ...value, scope };
      copyOrigin(value, copy);
      out.push(copy);
      return;
    }
    throw new BuildError(
      "INVALID_CHILD",
      `Unknown element kind '${value.kind}' at ${path}`
    );
  }
  throw new BuildError(
    "INVALID_CHILD",
    `Expected a <Slot> or <Component> element at ${path}, got ${describeValue(value)}`
  );
}

function slug(s: string): string {
  return s.toLowerCase().replace(/[^a-z0-9_-]/g, "_");
}

/** Recursively verify that every number in a JSON-bound value is finite;
 *  JSON.stringify would otherwise silently emit NaN/Infinity as null. */
function assertFiniteNumbers(value: unknown, path: string): void {
  if (typeof value === "number") {
    if (!Number.isFinite(value))
      throw new BuildError(
        "NON_FINITE_NUMBER",
        `Non-finite number at ${path}: ${value}`
      );
    return;
  }
  if (Array.isArray(value)) {
    value.forEach((item, i) => assertFiniteNumbers(item, `${path}[${i}]`));
    return;
  }
  if (value !== null && typeof value === "object") {
    for (const [k, v] of Object.entries(value as Record<string, unknown>))
      assertFiniteNumbers(v, `${path}.${k}`);
  }
}

export interface EvaluateOptions {
  ownership?: unknown;
  draft?: boolean;
}

export interface EvaluateResult {
  document: ApplyDocument;
  warnings: string[];
}

interface ConvertedSlot {
  spec: ApplySlotSpec;
  components: ApplyComponentSpec[];
  children: ApplyNodeSpec[];
  key: string;
}

export function evaluate(
  root: unknown,
  options: EvaluateOptions = {}
): EvaluateResult {
  const hasOwnership = Object.prototype.hasOwnProperty.call(options, "ownership");
  const ownership = options.ownership;
  if (hasOwnership && (ownership === null || typeof ownership !== "object" || Array.isArray(ownership) ||
      Object.keys(ownership).length !== 1 || !("key" in ownership) ||
      typeof ownership.key !== "string" || ownership.key.trim().length === 0))
    throw new Error("ownership export must be an object containing one non-empty string key");
  const draft = options.draft === true;
  const warnings: string[] = [];
  const scopes = new Set<string>();
  let generatedKeys = false;
  // Single global set per kind across the whole document, matching the C#
  // validator's HashSet over the entire recursive walk.
  const slotKeys = new Map<string, string>(); // key -> first path seen
  const componentKeys = new Map<string, string>();
  const aliasKeys = new Set<string>();

  function registerKey(
    map: Map<string, string>,
    key: string,
    kind: "slot" | "component",
    path: string
  ): void {
    const first = map.get(key);
    if (first !== undefined)
      throw new BuildError(
        "DUPLICATE_KEY",
        `${kind} key "${key}" is used more than once (first at ${first}, again at ${path})`
      );
    map.set(key, path);
  }

  function resolveKey(
    kind: "slot" | "component",
    props: Record<string, any>,
    displayName: string,
    parentKey: string | undefined,
    siblingIndex: number,
    path: string,
    scope: string
  ): string {
    const key = props.key;
    if (scope) return scopeKey(scope, key, path);
    if (typeof key === "string" && key.trim().length > 0) return key;
    if (!draft)
      throw new BuildError(
        "EXPLICIT_KEY_REQUIRED",
        `${kind} "${displayName}" at ${path} requires an explicit 'key' prop (or rebuild with --draft)`
      );
    const generated = `${parentKey ?? "root"}/${slug(displayName)}#${siblingIndex}`;
    generatedKeys = true;
    warnings.push(
      `warning: generated key "${generated}" for ${kind} "${displayName}" (--draft)`
    );
    return generated;
  }

  function convertComponent(
    el: RawElement,
    parentKey: string,
    siblingIndex: number,
    path: string
  ): ApplyComponentSpec {
    const props = el.props ?? {};
    const type = props.type;
    if (typeof type !== "string" || type.trim().length === 0)
      throw new BuildError(
        "COMPONENT_TYPE_MISSING",
        `Component at ${path} requires a non-empty 'type' prop`
      );
    const key = resolveKey("component", props, type, parentKey, siblingIndex, path, el.scope ?? "");
    registerKey(componentKeys, key, "component", path);
    const spec = copyComponent(props, key, path, el.scope ?? "", false, { assertFiniteNumbers, scopeValue });
    const source = originOfElement(el);
    const attributes = { ...(source?.attributes ?? {}) };
    function addOrigin(section: string, member: string, origin: Origin | undefined): void {
      const old = attributes[section];
      attributes[section] = { ...old, source: old?.source ?? unknown,
        children: { ...old?.children, [member]: origin ?? { source: unknown } } };
    }
    const aliases = spec.fieldAliases;
    if (aliases && el.scope) {
      spec.fieldAliases = Object.fromEntries(Object.entries(aliases).map(([alias, member]) => [scopeKey(el.scope!, alias, `${path}.fieldAliases`), member]));
      const old = attributes.fieldAliases;
      if (old?.children) attributes.fieldAliases = { ...old, children: Object.fromEntries(Object.entries(old.children).map(([alias, value]) => [scopeKey(el.scope!, alias, path), value])) };
    }
    const members = new Set([...Object.keys(spec.fields ?? {}), ...Object.keys(spec.initialFields ?? {})]);
    function convertField(value: any): void {
      if (value === undefined || value === null || typeof value === "boolean") return;
      if (Array.isArray(value)) { value.forEach(convertField); return; }
      if (isElement(value) && value.kind === "fragment") { convertField(value.props?.children); return; }
      if (!isElement(value) || value.kind !== "field")
        throw new BuildError("INVALID_CHILD", `Component children at ${path} must be <Field> elements`);
      const field = value.props ?? {};
      const name = field.name;
      function fail(code: string, message: string, attribute: string): never {
        const error = new BuildError(code, message); markBuildError(error, value, attribute); throw error;
      }
      if (typeof name !== "string" || !name.trim()) fail("APPLY_FIELD_INVALID", `Field at ${path} requires a non-empty name`, "name");
      if (members.has(name)) fail("APPLY_FIELD_DUPLICATE", `Member '${name}' at ${path} is declared more than once`, "name");
      members.add(name);
      const mode = field.mode ?? spec.propertyModes?.[name] ?? "config";
      if (!["config", "initial", "runtime", "driver-owned"].includes(mode)) fail("APPLY_COMPONENT_FIELD_POLICY_CONFLICT", `Invalid Field mode '${mode}'`, "mode");
      if (spec.propertyModes?.[name] !== undefined && spec.propertyModes[name] !== mode)
        fail("APPLY_COMPONENT_FIELD_POLICY_CONFLICT", `Conflicting mode for '${name}'`, "mode");
      const fieldOrigin = originOfElement(value);
      if (field.mode !== undefined) {
        spec.propertyModes = { ...spec.propertyModes, [name]: mode };
        addOrigin("propertyModes", name, fieldOrigin?.attributes.mode);
      }
      if (field.value !== undefined) {
        assertFiniteNumbers(field.value, `${path}.${mode === "initial" ? "initialFields" : "fields"}.${name}`);
        const section = mode === "initial" ? "initialFields" : "fields";
        spec[section] = { ...spec[section], [name]: scopeValue(field.value, el.scope ?? "") };
        addOrigin(section, name, { ...fieldOrigin?.attributes.value,
          source: fieldOrigin?.attributes.name?.valueSource ?? unknown, related: fieldOrigin?.related });
      } else if (mode === "config" || mode === "initial")
        fail("APPLY_FIELD_INVALID", `Field '${name}' requires a value in '${mode}' mode`, "name");
      if (field.key !== undefined) {
        let alias: string;
        try { alias = scopeKey(el.scope ?? "", field.key, `${path}.fieldAliases`); }
        catch (error) { if (error instanceof Error) markBuildError(error, value, "key"); throw error; }
        if (Object.hasOwn(spec.fieldAliases ?? {}, alias)) fail("APPLY_FIELD_ALIAS_DUPLICATE", `Field alias '${alias}' is declared more than once`, "key");
        spec.fieldAliases = { ...spec.fieldAliases, [alias]: name };
        addOrigin("fieldAliases", alias, fieldOrigin?.attributes.key);
      }
    }
    convertField(props.children);
    for (const alias of Object.keys(spec.fieldAliases ?? {})) {
      if (aliasKeys.has(alias)) {
        const error = new BuildError("APPLY_FIELD_ALIAS_DUPLICATE", `Field alias '${alias}' is declared more than once`);
        markBuildErrorOrigin(error, attributes.fieldAliases?.children?.[alias]); throw error;
      }
      aliasKeys.add(alias);
    }
    for (const [member, mode] of Object.entries(spec.propertyModes ?? {})) {
      if (!member.trim() || !["config", "initial", "runtime", "driver-owned"].includes(mode) ||
          mode === "config" && Object.hasOwn(spec.initialFields ?? {}, member) ||
          mode === "initial" && Object.hasOwn(spec.fields ?? {}, member))
        throw new BuildError("APPLY_COMPONENT_FIELD_POLICY_CONFLICT", `Invalid propertyModes declaration at ${path}.propertyModes.${member}`);
    }
    bindIr(el, spec);
    bindIrOrigin(spec, { source: source?.source ?? unknown, attributes, related: source?.related });
    return spec;
  }

  function convertSlot(
    el: RawElement,
    parentKey: string | undefined,
    siblingIndex: number,
    path: string,
    isRoot: boolean
  ): ConvertedSlot {
    const props = el.props ?? {};
    const name = props.name;
    if (typeof name !== "string" || name.trim().length === 0)
      throw new BuildError(
        "SLOT_NAME_MISSING",
        `Slot at ${path} requires a non-empty 'name' prop`
      );
    const key = resolveKey("slot", props, name, parentKey, siblingIndex, path, el.scope ?? "");
    registerKey(slotKeys, key, "slot", path);

    const flat: RawElement[] = [];
    flattenChildren(props.children, `${path}.children`, flat, el.scope ?? "", scopes);

    // Reject repeated sibling slot names under this parent, independent of
    // keys (mirrors APPLY_SIBLING_NAME_DUPLICATE in the C# validator).
    const seenNames = new Map<string, number>();
    let nameRank = 0;
    for (const child of flat) {
      if (child.kind !== "slot") continue;
      const childName = child.props?.name;
      if (typeof childName === "string") {
        const firstAt = seenNames.get(childName);
        if (firstAt !== undefined)
          throw new BuildError(
            "DUPLICATE_SIBLING_NAME",
            `Sibling slot name "${childName}" is repeated under ${path} ` +
              `(children[${firstAt}] and children[${nameRank}]); choose distinct sibling names`
          );
        seenNames.set(childName, nameRank);
      }
      nameRank++;
    }

    const spec = copySlot(props, key, path, el.scope ?? "", isRoot, { assertFiniteNumbers, scopeValue });

    bindIr(el, spec);
    const components: ApplyComponentSpec[] = [];
    const children: ApplyNodeSpec[] = [];
    let componentRank = 0;
    let slotRank = 0;
    // Convert in document order: components land in this node's components[]
    // and slots in children[].
    for (const child of flat) {
      if (child.kind === "component") {
        components.push(
          convertComponent(child, key, componentRank, `${path}.components[${componentRank}]`)
        );
        componentRank++;
      } else {
        const converted = convertSlot(
          child,
          key,
          slotRank,
          `${path}.children[${slotRank}]`,
          false
        );
        const node: ApplyNodeSpec = { slot: converted.spec };
        if (converted.components.length > 0) node.components = converted.components;
        if (converted.children.length > 0) node.children = converted.children;
        children.push(node);
        slotRank++;
      }
    }
    return { spec, components, children, key };
  }

  const roots: RawElement[] = [];
  flattenChildren(root, "$", roots, "", scopes);
  if (roots.length !== 1 || roots[0].kind !== "slot")
    throw new BuildError(
      "ROOT_MUST_BE_SINGLE_SLOT",
      `The entry module's default export must evaluate to exactly one <Slot> element; ` +
        `got ${roots.length === 0 ? "none" : `${roots.length} (${roots.map((r) => r.kind).join(", ")})`}`
    );

  const converted = convertSlot(roots[0], undefined, 0, "$", true);
  const document: ApplyDocument = {
    schemaVersion: "1",
    ownership: hasOwnership ? ownership as ApplyOwnershipSpec : { key: converted.key },
    slot: converted.spec,
    components: converted.components,
    children: converted.children,
  };
  if (generatedKeys) document.$draftKeys = true;
  // The compiler rejects a flattened scoped key colliding with a legacy key,
  // including the other kind. Keep legacy unscoped namespaces unchanged here.
  for (const key of slotKeys.keys())
    if (key.includes("::") && componentKeys.has(key))
      throw new BuildError("DUPLICATE_KEY", `Scoped stable key '${key}' is used by both a Slot and a Component`);
  function checkReferences(value: any): void {
    if (typeof value === "string") {
      const match = /^(\$(slot|component|ref|member|slot-member):)(.+)$/.exec(value);
      if (!match) return;
      const body = match[3];
      const member = match[2] === "member" || match[2] === "slot-member";
      const key = member ? body.slice(0, body.lastIndexOf(".")) : body;
      if (key.includes("::") && !(match[2].startsWith("slot") ? slotKeys : componentKeys).has(key))
        throw new BuildError("APPLY_SCOPE_INVALID", `Scoped reference '${value}' has no declared target; use a qualified key for another scope`);
    } else if (Array.isArray(value)) value.forEach(checkReferences);
    else if (value !== null && typeof value === "object") Object.values(value).forEach(checkReferences);
  }
  function checkNode(node: { components?: ApplyComponentSpec[]; children?: ApplyNodeSpec[] }): void {
    for (const component of node.components ?? []) {
      checkReferences(component.fields);
      checkReferences(component.initialFields);
    }
    for (const child of node.children ?? []) checkNode(child);
  }
  checkNode(document);
  return { document, warnings };
}
