// Private origin channel. No metadata is put in props or Apply JSON.
import { jsx } from "./jsx-runtime.js";
export type Source = { status: "unknown" } | { status: "known"; file: string; sha256: string;
  range: { start: { offset: number; line: number; column: number }; end: { offset: number; line: number; column: number } } };
export interface Origin { source: Source; valueSource?: Source; related?: Source[];
  forward?: string; forwardFrom?: object; children?: Record<string, Origin> }
export interface ElementOrigin { source: Source; attributes: Record<string, Origin>; related?: Source[] }
export const unknown: Source = { status: "unknown" };
const origins = new WeakMap<object, ElementOrigin>();
const irOrigins = new WeakMap<object, ElementOrigin>();
const propsOrigins = new WeakMap<object, Record<string, { origin: Origin; snapshot: string | undefined }>>();
const buildErrors = new WeakMap<object, { source: Source; related: Source[] }>();
let current: ElementOrigin | undefined;
let caller: { props: object; related: Source[] } | undefined;

function dataValue(value: unknown, key: string): unknown {
  if (value === null || typeof value !== "object") return Symbol();
  const descriptor = Object.getOwnPropertyDescriptor(value, key);
  return descriptor && "value" in descriptor ? descriptor.value : Symbol();
}

// Snapshot data values without invoking getters or retaining mutable objects.
// Unsupported/cyclic values cannot certify an origin.
function snapshot(value: unknown, depth = 0, ancestors = new Set<object>()): string | undefined {
  if (depth > 64) return undefined;
  if (value === null) return "null";
  if (typeof value !== "object") {
    if (typeof value === "function" || typeof value === "symbol") return undefined;
    return JSON.stringify([typeof value, Object.is(value, -0) ? "-0" : String(value)]);
  }
  const prototype = Object.getPrototypeOf(value);
  if (prototype !== Object.prototype && prototype !== null && !Array.isArray(value)) return undefined;
  if (ancestors.has(value)) return undefined;
  ancestors.add(value);
  const fields: string[] = [];
  for (const key of Object.keys(value).sort()) {
    const descriptor = Object.getOwnPropertyDescriptor(value, key)!;
    if (!("value" in descriptor)) return undefined;
    const child = snapshot(descriptor.value, depth + 1, ancestors);
    if (child === undefined) return undefined;
    fields.push(`[${JSON.stringify(key)},${child}]`);
  }
  ancestors.delete(value);
  return `[${JSON.stringify(Array.isArray(value) ? `array:${value.length}` : "object")},[${fields.join(",")}]]`;
}

// Arguments have already been evaluated by TS's own JSX lowering. In
// particular, await/yield and attribute side effects stay in their original scope.
export function originJsx(type: unknown, props: any, key: string | undefined, metadata: ElementOrigin): any {
  return sourceElement(() => jsx(type, props, key), metadata, props);
}

export function sourceElement(thunk: () => any, metadata: ElementOrigin, values?: any): any {
  const previous = current;
  function resolve(origin: Origin, value: unknown): Origin {
    const captured = origin.forward && origin.forwardFrom ? propsOrigins.get(origin.forwardFrom)?.[origin.forward] : undefined;
    const forwarded = captured?.snapshot !== undefined && captured.snapshot === snapshot(value) ? captured.origin : undefined;
    return { ...origin, valueSource: origin.forward ? forwarded?.valueSource ?? unknown : origin.valueSource,
      children: origin.forward ? forwarded?.children : origin.children && Object.fromEntries(Object.entries(origin.children).map(([k, v]) => [k, resolve(v, dataValue(value, k))])),
      related: [...(origin.related ?? []), ...(captured?.origin.related ?? []), ...(caller?.related ?? [])] };
  }
  current = { ...metadata, attributes: Object.fromEntries(Object.entries(metadata.attributes).map(([k, v]) => [k, resolve(v, dataValue(values, k))])),
    related: [...(metadata.related ?? []), ...(caller?.related ?? [])] };
  try { return thunk(); } finally { current = previous; }
}
export function captureElement(element: object): void { if (current) origins.set(element, current); }
export function callComponent(fn: (props: any) => any, props: object): any {
  const previous = caller;
  if (current) propsOrigins.set(props, Object.fromEntries(Object.entries(current.attributes)
    .map(([k, origin]) => [k, { origin, snapshot: snapshot(dataValue(props, k)) }])));
  caller = { props, related: current ? [current.source, ...(current.related ?? [])] : [] };
  try { return fn(props); } finally { caller = previous; }
}
export function copyOrigin(from: object, to: object): void { const origin = origins.get(from); if (origin) origins.set(to, origin); }
export function bindIr(from: object, to: object): void { const origin = origins.get(from); if (origin) irOrigins.set(to, origin); }
export function originOfElement(value: object): ElementOrigin | undefined { return origins.get(value); }
export function bindIrOrigin(to: object, origin: ElementOrigin): void { irOrigins.set(to, origin); }
export function originOfIr(value: object): ElementOrigin | undefined { return irOrigins.get(value); }
export function markBuildError(error: object, element: object, attribute: string): void {
  const origin = origins.get(element);
  buildErrors.set(error, { source: origin?.attributes[attribute]?.valueSource ?? unknown,
    related: [...(origin?.attributes[attribute]?.related ?? []), ...(origin?.related ?? [])] });
}
export function markBuildErrorOrigin(error: object, origin: Origin | undefined): void {
  buildErrors.set(error, { source: origin?.valueSource ?? origin?.source ?? unknown, related: origin?.related ?? [] });
}
export function buildErrorLocation(error: object): string {
  const source = buildErrors.get(error)?.source ?? unknown;
  return source.status === "known" ? `${source.file}:${source.range.start.line}:${source.range.start.column}` : "unknown";
}
