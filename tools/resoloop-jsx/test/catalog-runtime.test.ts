import { test } from "node:test";
import { deepEqual, equal, ok } from "node:assert/strict";
import { BuildError } from "../src/evaluate.js";
import { catalogMember, verifyRuntimeTable } from "../src/catalog-runtime.js";
import { objectTestRuntimeTable, tableWithOwnProtoMember, tableWithOwnType } from "./object-test-helpers.js";

const unavailableCode = "APPLY_CATALOG_UNAVAILABLE";
const mismatchCode = "APPLY_CATALOG_TYPES_HASH_MISMATCH";
const mismatchMessage = "Catalog runtime table does not match format/generator/descriptor shape; regenerate catalog types.";

function expectBuildError(action: () => unknown, code: string, message?: string): void {
  let captured: unknown;
  try { action(); } catch (error) { captured = error; }
  if (!(captured instanceof BuildError)) throw new Error("Expected a BuildError");
  equal(captured.code, code);
  if (message !== undefined) equal(captured.message, message);
}

function clone<T>(value: T): T { return JSON.parse(JSON.stringify(value)) as T; }

test("valid runtime table preserves four field representations, a reference, and canonical aliases", () => {
  const table = verifyRuntimeTable(objectTestRuntimeTable());
  equal(table.contentHash, "handwritten-runtime-hash");
  for (const [member, scalar] of [["Enabled", "boolean"], ["Count", "int32"], ["Label", "string"], ["Weight", "single"]])
    equal(catalogMember(table, "Synthetic.Device", member).scalar, scalar);
  deepEqual(catalogMember(table, "Synthetic.Device", "Target").fieldTarget, {
    representation: "boolean", valueType: "bool", targetType: "[FrooxEngine]FrooxEngine.IField<bool>",
  });
  equal(catalogMember(table, "Synthetic.Device", "Target").targetType, "[FrooxEngine]FrooxEngine.BooleanFieldAlias");
  equal(catalogMember(table, "Synthetic.DeviceAlias", "Enabled"), table.types["Synthetic.Device"].members.Enabled);
});

test("missing, old, and future runtime descriptor versions fail with the fixed mismatch error", () => {
  const base = objectTestRuntimeTable();
  const { formatVersion: _missingVersion, ...missingVersion } = base;
  for (const value of [
    missingVersion,
    { ...base, formatVersion: "0" },
    { ...base, generatorVersion: "2" },
    { ...base, generatorVersion: "4" },
  ]) {
    expectBuildError(() => verifyRuntimeTable(value), mismatchCode);
    expectBuildError(() => verifyRuntimeTable(value), mismatchCode, mismatchMessage);
  }
});

test("invalid descriptor shape, field targets, and field interfaces fail before lookup", () => {
  const base = objectTestRuntimeTable();
  const badTarget = clone(base);
  (badTarget.types["Synthetic.Device"].members.Target as any).fieldTarget.representation = "double";
  const badInterface = clone(base);
  (badInterface.types["Synthetic.Device"].members.Enabled as any).fieldInterface = "IField<bool>";
  const extraKey = { ...base, extra: true };
  for (const value of [badTarget, badInterface, extraKey, { ...base, contentHash: "" },
    { ...base, types: { "Synthetic.Device": { ...base.types["Synthetic.Device"], unexpected: true } } }])
    expectBuildError(() => verifyRuntimeTable(value), mismatchCode, mismatchMessage);
});

test("missing and unconfirmed types or members have the fixed unavailable error", () => {
  const table = verifyRuntimeTable(objectTestRuntimeTable());
  expectBuildError(() => catalogMember(table, "Missing.Type", "Enabled"), unavailableCode,
    "Type 'Missing.Type', member 'Enabled': runtime catalog evidence is missing or unconfirmed.");
  expectBuildError(() => catalogMember(table, "Synthetic.Device", "Missing"), unavailableCode);
  expectBuildError(() => catalogMember(table, "Synthetic.Device", "Uncertain"), unavailableCode);
  const unconfirmed = clone(objectTestRuntimeTable(false));
  const owner = unconfirmed.types["Synthetic.Device"] as any;
  owner.confirmed = false;
  for (const member of Object.values(owner.members) as Array<Record<string, unknown>>) {
    member.confirmed = false; member.scalar = null; member.fieldInterface = null;
    member.fieldTarget = null;
  }
  expectBuildError(() => catalogMember(verifyRuntimeTable(unconfirmed), "Synthetic.Device", "Enabled"), unavailableCode);
  const acceptedRaw = verifyRuntimeTable(objectTestRuntimeTable());
  equal(acceptedRaw.types["Synthetic.Device"].members.UnconfirmedTarget.targetType,
    "[FrooxEngine]FrooxEngine.UnconfirmedAlias");
  expectBuildError(() => catalogMember(acceptedRaw, "Synthetic.Device", "UnconfirmedTarget"), unavailableCode);
});

test("unconfirmed evidence cannot assert a scalar, interface, or target during validation", () => {
  const base = objectTestRuntimeTable();
  const scalar = clone(base);
  (scalar.types["Synthetic.Device"].members.Uncertain as any).scalar = "boolean";
  const fieldTarget = clone(base);
  (fieldTarget.types["Synthetic.Device"].members.Uncertain as any).fieldTarget = {
    representation: "boolean", valueType: "bool", targetType: "[FrooxEngine]FrooxEngine.IField<bool>",
  };
  for (const value of [scalar, fieldTarget]) expectBuildError(() => verifyRuntimeTable(value), mismatchCode);
});

test("lookup ignores inherited prototype names and accepts explicit own __proto__ data", () => {
  const table = verifyRuntimeTable(objectTestRuntimeTable());
  for (const name of ["constructor", "toString", "__proto__"])
    expectBuildError(() => catalogMember(table, name, "Enabled"), unavailableCode);
  const entry = table.types["Synthetic.Device"];
  const special = tableWithOwnType("__proto__", entry);
  const verified = verifyRuntimeTable(special);
  equal(catalogMember(verified, "__proto__", "Enabled").scalar, "boolean");
  const ownProtoMember = verifyRuntimeTable(tableWithOwnProtoMember());
  equal(catalogMember(ownProtoMember, "Synthetic.Device", "__proto__").scalar, "boolean");
  expectBuildError(() => catalogMember(verified, "__proto__", "constructor"), unavailableCode);
});

test("open owners can resolve present confirmed members; contradictory aliases fail validation", () => {
  const base = objectTestRuntimeTable();
  const table = verifyRuntimeTable(base);
  equal(table.types["Synthetic.Device"].membersComplete, false);
  equal(catalogMember(table, "Synthetic.Device", "Enabled").confirmed, true);
  const conflict = clone(base);
  (conflict.types["Synthetic.DeviceAlias"] as any).membersComplete = true;
  expectBuildError(() => verifyRuntimeTable(conflict), mismatchCode, mismatchMessage);
});
