import { BuildError } from "./evaluate.js";

export function scopeKey(scope: string, local: unknown, path: string): string {
  if (typeof local !== "string" || local.trim().length === 0 || local.includes(":"))
    throw new BuildError("APPLY_SCOPE_INVALID", `${path} requires a non-empty local key without ':' (reserved for '::')`);
  return scope ? `${scope}::${local}` : local;
}

/** Short selectors are scope-local. Qualified selectors are absolute; no fallback. */
export function scopeValue(value: any, scope: string): any {
  if (!scope) return value;
  if (typeof value === "string") {
    const match = /^(\$(?:slot|component|ref|member|slot-member):)(.+)$/.exec(value);
    if (!match) return value;
    const body = match[2];
    const member = match[1] === "$member:" || match[1] === "$slot-member:";
    const split = member ? body.lastIndexOf(".") : -1;
    const key = split < 0 ? body : body.slice(0, split);
    return key.includes("::") ? value : `${match[1]}${scope}::${body}`;
  }
  if (Array.isArray(value)) return value.map(item => scopeValue(item, scope));
  if (value !== null && typeof value === "object")
    return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, scopeValue(item, scope)]));
  return value;
}
