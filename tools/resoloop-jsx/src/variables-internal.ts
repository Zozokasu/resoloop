import type { JsxNode } from "./elements.js";
import { BuildError } from "./evaluate.js";
import { captureElement } from "./source-map.js";

export type Kind = "bool" | "int32" | "single" | "string";
export type Value<K extends Kind> = K extends "bool" ? boolean : K extends "string" ? string : number;
export interface VariableType<K extends Kind> { readonly kind: K }
export interface Declaration<K extends Kind> { readonly type: VariableType<K>; readonly defaultValue: Value<K> }

const refBrand: unique symbol = Symbol("VariableRef");
const bindingBrand: unique symbol = Symbol("FieldBinding");

export interface VariableRef<K extends Kind> {
  readonly kind: K;
  readonly [refBrand]: (kind: K) => K;
}

export interface FieldBinding<K extends Kind> {
  readonly ref: VariableRef<K>;
  readonly mode: "drive" | "bind";
  readonly [bindingBrand]: (kind: K) => K;
}

export interface VariableSet<V extends Record<string, Declaration<Kind>>> {
  readonly vars: { readonly [P in keyof V]: V[P] extends Declaration<infer K> ? VariableRef<K> : never };
}

export const types = Object.freeze({
  bool: Object.freeze({ kind: "bool" as const }),
});

interface VariableMetadata {
  readonly set: object;
  readonly scopeKey: string;
  readonly name: string;
  readonly type: VariableType<Kind>;
  readonly defaultValue: Value<Kind>;
}

interface SetMetadata {
  readonly scopeKey: string;
  readonly variables: readonly VariableMetadata[];
}

const setMetadata = new WeakMap<object, SetMetadata>();
const refMetadata = new WeakMap<object, VariableMetadata>();
const bindingMetadata = new WeakMap<object, { readonly ref: object; readonly mode: "drive" | "bind" }>();
const identifierPattern = /^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/;

function invalidIdentifier(id: string): never {
  throw new BuildError("CREATION_ID_INVALID",
    `Variable identifier '${id}' must match ^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$.`);
}

function checkIdentifier(id: unknown): asserts id is string {
  if (typeof id !== "string" || !identifierPattern.test(id)) invalidIdentifier(String(id));
}

export function variable<K extends Kind>(type: VariableType<K>, defaultValue: Value<NoInfer<K>>): Declaration<K> {
  if (type?.kind !== "bool") {
    const kind = type && typeof type.kind === "string" ? type.kind : String(type?.kind);
    throw new BuildError("CREATION_VARIABLE_TYPE_UNSUPPORTED",
      `Variable type '${kind}' is unsupported; only bool is supported.`);
  }
  if (typeof defaultValue !== "boolean")
    throw new BuildError("CREATION_VARIABLE_TYPE_MISMATCH", "Variable default for 'bool' must be boolean.");
  const stableType = Object.freeze({ kind: "bool" as const });
  return Object.freeze({ type: stableType, defaultValue }) as Declaration<K>;
}

export function defineVariables<const V extends Record<string, Declaration<Kind>>>(input: {
  scopeKey: string;
  variables: V;
}): VariableSet<V> {
  checkIdentifier(input.scopeKey);
  const variables = Object.create(null) as Record<string, VariableRef<Kind>>;
  const descriptors: VariableMetadata[] = [];
  for (const name of Object.keys(input.variables)) {
    checkIdentifier(name);
    const declaration = Object.getOwnPropertyDescriptor(input.variables, name)?.value as Declaration<Kind> | undefined;
    if (!declaration || declaration.type?.kind !== "bool") {
      const kind = declaration?.type && typeof declaration.type.kind === "string"
        ? declaration.type.kind : String(declaration?.type?.kind);
      throw new BuildError("CREATION_VARIABLE_TYPE_UNSUPPORTED",
        `Variable type '${kind}' is unsupported; only bool is supported.`);
    }
    if (typeof declaration.defaultValue !== "boolean")
      throw new BuildError("CREATION_VARIABLE_TYPE_MISMATCH", "Variable default for 'bool' must be boolean.");
    descriptors.push({ set: undefined as never, scopeKey: input.scopeKey, name,
      type: declaration.type, defaultValue: declaration.defaultValue });
    // Refs are completed with set identity after the frozen set object exists.
    variables[name] = Object.freeze({ kind: "bool", [refBrand]: (kind: Kind) => kind }) as VariableRef<Kind>;
  }
  const set = Object.freeze({ vars: Object.freeze(variables) }) as VariableSet<V>;
  const completed = descriptors.map((descriptor, index) => {
    const metadata = Object.freeze({ ...descriptor, set });
    refMetadata.set(variables[descriptor.name], metadata);
    descriptors[index] = metadata;
    return metadata;
  });
  setMetadata.set(set, Object.freeze({ scopeKey: input.scopeKey, variables: Object.freeze(completed) }));
  return set;
}

export function Variables(props: { declaration: VariableSet<any> }): JsxNode {
  const element = { kind: "variables", props };
  captureElement(element);
  return element as unknown as JsxNode;
}

function makeBinding<K extends Kind>(ref: VariableRef<K>, mode: "drive" | "bind"): FieldBinding<K> {
  if (!refMetadata.has(ref as object))
    throw new BuildError("CREATION_VARIABLE_UNRESOLVED",
      "Variable reference is unresolved; use a reference from defineVariables.");
  const binding = Object.freeze({ ref, mode, [bindingBrand]: (kind: K) => kind }) as FieldBinding<K>;
  bindingMetadata.set(binding, { ref: ref as object, mode });
  return binding;
}

export function drive<K extends Kind>(ref: VariableRef<K>): FieldBinding<K> {
  return makeBinding(ref, "drive");
}

export function bind<K extends Kind>(ref: VariableRef<K>): FieldBinding<K> {
  return makeBinding(ref, "bind");
}

export function variableSetDescriptor(set: unknown): {
  readonly set: object;
  readonly scopeKey: string;
  readonly variables: readonly VariableMetadata[];
} | undefined {
  if ((typeof set !== "object" || set === null) && typeof set !== "function") return undefined;
  const metadata = setMetadata.get(set as object);
  return metadata ? Object.freeze({ set: set as object, ...metadata }) : undefined;
}

export function variableRefDescriptor(ref: unknown): VariableMetadata | undefined {
  if ((typeof ref !== "object" || ref === null) && typeof ref !== "function") return undefined;
  return refMetadata.get(ref as object);
}

export function fieldBindingDescriptor(binding: unknown): (VariableMetadata & {
  readonly mode: "drive" | "bind";
  readonly ref: VariableRef<Kind>;
}) | undefined {
  if ((typeof binding !== "object" || binding === null) && typeof binding !== "function") return undefined;
  const metadata = bindingMetadata.get(binding as object);
  if (!metadata) return undefined;
  const descriptor = refMetadata.get(metadata.ref);
  return descriptor ? Object.freeze({ ...descriptor, ref: metadata.ref as VariableRef<Kind>, mode: metadata.mode }) : undefined;
}
