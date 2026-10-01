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
const propsOrigins = new WeakMap<object, Record<string, Origin>>();
const buildErrors = new WeakMap<object, { source: Source; related: Source[] }>();
let current: ElementOrigin | undefined;
let caller: { props: object; related: Source[] } | undefined;

// Arguments have already been evaluated by TS's own JSX lowering. In
// particular, await/yield and attribute side effects stay in their original scope.
export function originJsx(type: unknown, props: any, key: string | undefined, metadata: ElementOrigin): any {
  return sourceElement(() => jsx(type, props, key), metadata);
}

export function sourceElement(thunk: () => any, metadata: ElementOrigin): any {
  const previous = current;
  function resolve(origin: Origin): Origin {
    const forwarded = origin.forward && origin.forwardFrom ? propsOrigins.get(origin.forwardFrom)?.[origin.forward] : undefined;
    return { ...origin, valueSource: origin.forward ? forwarded?.valueSource ?? unknown : origin.valueSource,
      children: origin.forward ? forwarded?.children : origin.children && Object.fromEntries(Object.entries(origin.children).map(([k, v]) => [k, resolve(v)])),
      related: [...(origin.related ?? []), ...(forwarded?.related ?? []), ...(caller?.related ?? [])] };
  }
  current = { ...metadata, attributes: Object.fromEntries(Object.entries(metadata.attributes).map(([k, v]) => [k, resolve(v)])),
    related: [...(metadata.related ?? []), ...(caller?.related ?? [])] };
  try { return thunk(); } finally { current = previous; }
}
export function captureElement(element: object): void { if (current) origins.set(element, current); }
export function callComponent(fn: (props: any) => any, props: object): any {
  const previous = caller;
  if (current) propsOrigins.set(props, current.attributes);
  caller = { props, related: current ? [current.source, ...(current.related ?? [])] : [] };
  try { return fn(props); } finally { caller = previous; }
}
export function copyOrigin(from: object, to: object): void { const origin = origins.get(from); if (origin) origins.set(to, origin); }
export function bindIr(from: object, to: object): void { const origin = origins.get(from); if (origin) irOrigins.set(to, origin); }
export function originOfIr(value: object): ElementOrigin | undefined { return irOrigins.get(value); }
export function markBuildError(error: object, element: object, attribute: string): void {
  const origin = origins.get(element);
  buildErrors.set(error, { source: origin?.attributes[attribute]?.valueSource ?? unknown,
    related: [...(origin?.attributes[attribute]?.related ?? []), ...(origin?.related ?? [])] });
}
export function buildErrorLocation(error: object): string {
  const source = buildErrors.get(error)?.source ?? unknown;
  return source.status === "known" ? `${source.file}:${source.range.start.line}:${source.range.start.column}` : "unknown";
}
