import { test } from "node:test";
import { equal, ok } from "node:assert/strict";
import type { CatalogRuntimeTable } from "../src/catalog-runtime.js";

const booleanMember = {
  kind: "field",
  catalogKind: "field",
  confirmed: true,
  memberType: null,
  valueType: "[System.Private.CoreLib]System.Boolean",
  targetType: null,
  scalar: "boolean",
  fieldInterface: null,
  fieldTarget: null,
} as const;

const protoMembers = { ["__proto__"]: booleanMember } as const;
const handwrittenProtoTable = {
  formatVersion: "1",
  generatorVersion: "3",
  contentHash: "handwritten-proto-literal",
  types: {
    ["__proto__"]: {
      canonicalType: "__proto__",
      confirmed: true,
      membersComplete: true,
      members: protoMembers,
    },
    ProtoAlias: {
      canonicalType: "__proto__",
      confirmed: true,
      membersComplete: true,
      members: protoMembers,
    },
  },
} as const satisfies CatalogRuntimeTable;

type HandwrittenQuotedMetadata = { readonly "__proto__": CatalogRuntimeTable["types"][string]["members"][string] };
type HasQuotedProtoKey = "__proto__" extends keyof HandwrittenQuotedMetadata ? true : false;
const declarationKeepsQuotedKey: HasQuotedProtoKey = true;

test("independent computed-key runtime literals preserve own proto data and quoted declaration keys", () => {
  const types = handwrittenProtoTable.types;
  ok(Object.hasOwn(types, "__proto__"));
  ok(Object.hasOwn(types, "ProtoAlias"));
  ok(Object.hasOwn(types["__proto__"].members, "__proto__"));
  equal(types["ProtoAlias"].canonicalType, "__proto__");
  equal(Object.getPrototypeOf(types), Object.prototype);
  equal(Object.getPrototypeOf(types["__proto__"].members), Object.prototype);
  equal(Object.getPrototypeOf(Object.prototype), null);
  equal(declarationKeepsQuotedKey, true);
});
