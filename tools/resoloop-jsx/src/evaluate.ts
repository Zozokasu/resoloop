// Evaluates a resolved resoloop-jsx element tree into a schema-v1
// ApplyDocument object (the JSON shape consumed by `resoloop validate` /
// `resoloop apply`, see src/RLoop.Core/ApplyWorkflow.cs).

import type { JsonValue } from "./elements.js";

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

/* ---- schema-v1 output types (field comments name the C# member) ---- */

/** Mirrors C# ApplyOwnershipSpec. */
export interface ApplyOwnershipSpec {
  /** ApplyOwnershipSpec.Key */
  key: string;
}

/** Mirrors C# ApplySlotSpec. */
export interface ApplySlotSpec {
  /** ApplySlotSpec.Name (required) */
  name: string;
  /** ApplySlotSpec.Parent (root slot only for JSX-authored documents) */
  parent?: string;
  /** ApplySlotSpec.Position */
  position?: [number, number, number];
  /** ApplySlotSpec.Rotation */
  rotation?: [number, number, number, number];
  /** ApplySlotSpec.Scale */
  scale?: [number, number, number];
  /** ApplySlotSpec.Key */
  key?: string;
  /** ApplySlotSpec.ManagedFields */
  managedFields?: ("position" | "rotation" | "scale")[];
  /** ApplySlotSpec.PreserveWorldTransform */
  preserveWorldTransform?: boolean;
  /** ApplySlotSpec.MigrateFrom */
  migrateFrom?: string;
  /** ApplySlotSpec.RelocationTransform */
  relocationTransform?: "local" | "world";
  /** ApplySlotSpec.RuntimeRelocatable */
  runtimeRelocatable?: boolean;
}

/** Mirrors C# ApplyComponentSpec. */
export interface ApplyComponentSpec {
  /** ApplyComponentSpec.Type (required) */
  type: string;
  /** ApplyComponentSpec.Fields */
  fields?: Record<string, JsonValue>;
  /** ApplyComponentSpec.Key */
  key?: string;
  /** ApplyComponentSpec.MigrateFrom */
  migrateFrom?: string;
  /** ApplyComponentSpec.InitialFields */
  initialFields?: Record<string, JsonValue>;
  /** ApplyComponentSpec.IdentityFields */
  identityFields?: string[];
}

/** Mirrors C# ApplyNodeSpec (every non-root <Slot> compiles to this). */
export interface ApplyNodeSpec {
  /** ApplyNodeSpec.Slot */
  slot: ApplySlotSpec;
  /** ApplyNodeSpec.Components */
  components?: ApplyComponentSpec[];
  /** ApplyNodeSpec.Children */
  children?: ApplyNodeSpec[];
}

/** Mirrors C# ApplyDocument (subset supported by resoloop-jsx). */
export interface ApplyDocument {
  /** ApplyDocument.SchemaVersion — always the literal string "1" */
  schemaVersion: "1";
  /** ApplyDocument.Ownership */
  ownership: ApplyOwnershipSpec;
  authoring?: { projectRoot: string; source: string; ownershipSource?: string };
  /** ApplyDocument.Slot */
  slot: ApplySlotSpec;
  /** ApplyDocument.Components */
  components: ApplyComponentSpec[];
  /** ApplyDocument.Children */
  children: ApplyNodeSpec[];
}

/* ---- element internals ---- */

interface RawElement {
  kind: string;
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
function flattenChildren(value: any, path: string, out: RawElement[]): void {
  if (value === null || value === undefined || value === false || value === true)
    return;
  if (Array.isArray(value)) {
    for (const item of value) flattenChildren(item, path, out);
    return;
  }
  if (isElement(value)) {
    if (value.kind === "fragment") {
      flattenChildren(value.props?.children, path, out);
      return;
    }
    if (value.kind === "slot" || value.kind === "component") {
      out.push(value);
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
  // Single global set per kind across the whole document, matching the C#
  // validator's HashSet over the entire recursive walk.
  const slotKeys = new Map<string, string>(); // key -> first path seen
  const componentKeys = new Map<string, string>();

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
    path: string
  ): string {
    const key = props.key;
    if (typeof key === "string" && key.trim().length > 0) return key;
    if (!draft)
      throw new BuildError(
        "EXPLICIT_KEY_REQUIRED",
        `${kind} "${displayName}" at ${path} requires an explicit 'key' prop (or rebuild with --draft)`
      );
    const generated = `${parentKey ?? "root"}/${slug(displayName)}#${siblingIndex}`;
    warnings.push(
      `warning: generated key "${generated}" for ${kind} "${displayName}" (--draft)`
    );
    return generated;
  }

  const SLOT_SCALAR_PROPS = [
    "position",
    "rotation",
    "scale",
    "managedFields",
    "preserveWorldTransform",
    "migrateFrom",
    "relocationTransform",
    "runtimeRelocatable",
  ] as const;

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
    const key = resolveKey("component", props, type, parentKey, siblingIndex, path);
    registerKey(componentKeys, key, "component", path);
    const spec: ApplyComponentSpec = { type, key };
    if (props.fields !== undefined) {
      assertFiniteNumbers(props.fields, `${path}.fields`);
      spec.fields = props.fields;
    }
    if (props.migrateFrom !== undefined) spec.migrateFrom = props.migrateFrom;
    if (props.initialFields !== undefined) {
      assertFiniteNumbers(props.initialFields, `${path}.initialFields`);
      spec.initialFields = props.initialFields;
    }
    if (props.identityFields !== undefined) spec.identityFields = props.identityFields;
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
    const key = resolveKey("slot", props, name, parentKey, siblingIndex, path);
    registerKey(slotKeys, key, "slot", path);

    const flat: RawElement[] = [];
    flattenChildren(props.children, `${path}.children`, flat);

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

    const spec: ApplySlotSpec = { name, key };
    if (isRoot && props.parent !== undefined) spec.parent = props.parent;
    for (const field of SLOT_SCALAR_PROPS)
      if (props[field] !== undefined) {
        assertFiniteNumbers(props[field], `${path}.${field}`);
        (spec as any)[field] = props[field];
      }

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
  flattenChildren(root, "$", roots);
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
  return { document, warnings };
}
