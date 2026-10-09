# Object authoring workflow (experimental)

Use this workflow to author reusable Objects in TSX, from Reflection to a checked result. The Object API is experimental. It has not been verified in a live Resonite session, and the final Toggle target type is not yet confirmed. Names such as `Fixture.Objects.Lamp` and `Fixture.Objects.Toggle` in documentation are fictional. Replace them only with types confirmed through Reflection. A formal example follows Reflection.

Follow these steps in order. Do not skip ahead.

1. Reflection. Confirm the complete runtime type names, their members, each member's Kind and TargetType, the generic closure, and the writer of each target field through `type search`, `type describe` and `type query`. Never guess a type or member name. Live metadata acquisition, including a request to the running world, needs explicit authorization. Without it, work only from already identified evidence.

2. Bounded capture. With explicit authorization, run `resoloop catalog capture --types FULL_NAMES.json --output NEW_CATALOG.json`. Capture is bounded to 512 requested names, 512 attempted metadata names (including failures and recursive dependencies), and two minutes. A partial catalog saved with `CATALOG_CAPTURE_INCOMPLETE` (exit 6) is not a success. Missing metadata stays missing; do not treat a partial capture as complete or fill the gap by guessing.

3. Catalog types. `resoloop catalog types NEW_CATALOG.json` is offline. Run it inside the project that contains `.resoloop.json`, or pass `--output`. By default it writes the paired `.resoloop/catalog-types.d.ts` and `.resoloop/catalog-fields.ts`. `--output X.d.ts` also writes `X.fields.ts`; any other suffix emits declarations only, with a warning. Headers use format 1 and generator 3, and bind the catalog hash, the runtime filename and the runtime SHA-256. Catalog format 2 and mapper version 3 are unchanged.
   - Include or reference the `.d.ts` file in `tsconfig.json` (`include`), or with `/// <reference path="../.resoloop/catalog-types.d.ts" />`. Import `.resoloop/catalog-fields.js` explicitly from the source.
   - Use `componentFields(type, catalog)` for member and value checks; a declaration-only legacy file gives editor types but not the runtime table. `Lamp.ref(componentKey, member)` makes a `FieldRef`. Strict references support only `boolean`, `int32`, `string` and `single`. Numeric values are TypeScript `number`; the reference types keep `int32` and `single` distinct. Legacy `<Field>` and plain string references stay loose and compatible.
   - An explicit import whose runtime file cannot be resolved produces TS2307 (build exit 2). Unavailable evidence is `APPLY_CATALOG_UNAVAILABLE` (exit 1). A stale header, hash, runtime or companion file is `APPLY_CATALOG_TYPES_HASH_MISMATCH` (exit 1).
   - `tsc` does not check artifact binding, freshness, identity, placement or privacy. A normal build compares imported runtime data with its declaration but cannot establish freshness against the source catalog JSON; only bundle mode with `--catalog` binds that input.

4. defineObject. Write `defineObject<P>()({ exports, body })`. `exports` names the public references, for example `{ enabled: Lamp.ref("light", "Enabled") }`. Create each placement with `Def.instance(id)` and render `<Def handle={handle} ...props />`. `handle` is reserved and cannot be a user prop. Reference an export only with `ref.export(handle, name)`.
   - The instance ID matches `^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$` and is unique across the whole build.
   - Give every Slot and Component in the body an explicit key. Expanded keys become `instanceId::localKey`, with nested Scope segments appended. The `instanceId::` namespace is reserved everywhere.
   - A raw reference cannot cross an Object boundary, even when its target is also a public export. Use only `ref.export` across the boundary. Nested Objects are unsupported.
   - TSX-only diagnostics exit 1: `APPLY_OBJECT_INVALID`, `APPLY_OBJECT_DUPLICATE`, `APPLY_OBJECT_EXPORT_INVALID`, `APPLY_OBJECT_EXPORT_UNKNOWN`, `APPLY_OBJECT_TYPE_MISMATCH`, `APPLY_OBJECT_PRIVATE_REFERENCE` and `APPLY_FIELD_TYPE_MISMATCH`. TypeScript diagnostics exit 2.
   - Output stays schema 1 with a single ownership key; apply state stays version 3.

5. Offline validate, diff and plan decision. Build with a fresh request ID, then validate offline with `resoloop validate FILE.json --catalog NEW_CATALOG.json --json`. The C# validator is authoritative and returns the existing exit 6 for `VALUE_CONVERSION_FAILED`, `APPLY_REFERENCE_TYPE_MISMATCH` and `APPLY_CATALOG_UNAVAILABLE`. Catalog, `tsc` and offline checks do not prove runtime behavior, real ownership or privacy. Connected `diff`, `plan` and `validate --strict` need live authorization. Run `diff FILE --brief --report NEW_PLAN.json --json`, review each create/update/rename/delete target and reason, and decide whether to apply. Declare `propertyModes` honestly: config and initial values are written, while runtime and driver-owned values are never written, even on creation.

6. Authorized apply. Apply only with the user's authorization for the intended session. Experiments run inside a clearly named `ResoLoop_Test*` Slot, and every edit stays below it. Use `--state VERIFIED_STATE --require-state` for continued work. Wrap experiments in a `finally` step so cleanup runs even if a check fails. Before cleanup, confirm that no pending operation remains, then check ownership, state and session, and read back the result. Remove only the exact `ResoLoop_Test*` Slot you created. Never target Root, an unconfirmed ID or path, or existing user content. For `remove`, `delete`, `--prune` and `--discard-pending`, keep the explicit `--yes`; never add it automatically. See [apply recovery](apply-recovery.md) for pending evidence.

7. Exact-target readback. Inspect the exact IDs returned by apply (`inspect ID --members`, `component inspect ID`, or `observe` selectors) and compare the readback with the plan. Check that references target the intended exported members. Report structural, offline and live evidence separately.

## Preservation and limits

- Keep instance IDs and export names stable while consumers rely on them. Updating a target inside the Object definition then preserves consumer source.
- Moving a Component to a different owner, or changing its type, key or target, can change runtime reference IDs and may require recreation. `initialFields` apply only on creation, so recreation can apply them again and runtime values may be lost.
- State and real-world ownership may be incomplete; the API does not guarantee that IDs or runtime state are retained.
- Offline tests cover repeated and parallel builds, not ESM cache reclamation or watch mode.
- Live verification and a real Toggle target are unverified. Do not describe results as runtime-verified before they are.
