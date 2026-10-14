# Creation regenerate and recovery workflow (experimental)

Creation regenerate rebuilds one complete owned root from an envelope. The feature is experimental; neither the TSX creation path nor regenerate with same-name sibling Slots has been verified in a live Resonite session. Read this workflow before using it. The feature does not take ownership of a root by name or path alone.

## Scope and input

Build the envelope from TSX with `resoloop-jsx build content/room.tsx --target creation -o creation.json --project-root .`. The entry's default export must evaluate to one root `<Slot>`. See the `resoloop-jsx` package README, section “Key rules”, for key requirements. Creation target permits repeated sibling Slot names, and explicit keys still must be unique.

`creationId` is selected from the key in the entry's named `ownership` export when present; otherwise the root Slot must have an explicit key. The chosen key must contain 1–128 characters and no control characters. If neither source supplies it, TSX build fails with `CREATION_ID_INVALID`; generated positional keys are not used as `creationId`. Creation builds cannot combine with `--draft`, `--bundle`, `--catalog`, or `--build-id`; creation bundles are unavailable in this version.

Validate the output offline with `resoloop validate creation.json --json`. The CLI recognizes the envelope automatically, and `validate` does not accept `--mode`. `plan` and `apply` require `--mode regenerate`. They run `CreationRegeneratePreflight` before connecting; unsupported Asset, Flux, Script, and other creation declarations stop at that preflight. Mode-free `validate` performs envelope validation and does not run `CreationRegeneratePreflight`.

The envelope has this shape and contains a schema v1 definition:

```json
{
  "schemaVersion": "creation-1",
  "kind": "resoloop-creation",
  "creationVersion": "1",
  "creationId": "room",
  "definition": {
    "schemaVersion": "1",
    "ownership": { "key": "room" },
    "slot": { "key": "root", "name": "Room", "parent": "Root" },
    "components": [],
    "children": []
  }
}
```

The envelope may be written by hand as JSON or built from TSX as described above; creation build bundles are not supported. Asset, Flux and Script placement are unsupported. The implementation also rejects cameras, tests, migration declarations and nested child Slots with their own parent. Unsupported declarations must be removed or handled by another workflow; they are not silently ignored.

Creation state schema v4 is separate from ordinary apply state v3. No v3-to-v4 migration is provided. Keep the state file safe and available. Planning and applying require `--url auto --session S-ID`, where `S-ID` is the exact discovered session ID. An explicit URL alone does not prove the session.

## Plan, inspect, apply

Run plan first and review its root, replacement order and counts. A plan is a connected observation and never writes to the world:

```powershell
resoloop plan creation.json --mode regenerate --state creation-state.json --url auto --session S-ID --json
```

The plan result reports `mode: "regenerate"`, `creationId`, `stateFile`, `inputHash`, `action` (`create` or `replace`), `previousRootId`, `replaceOrder`, `definitionsReset`, Slot/Component counts, ownership/session evidence, diagnostics and `atomic: false`. Replacing a root is delete-first. Before confirming, inspect the exact recorded root and the entire subtree that belongs to it. Every Slot under the root is deleted, including content manually added after the prior run.

Apply only after reviewing that scope:

```powershell
resoloop apply creation.json --mode regenerate --state creation-state.json --replace-order delete-first --url auto --session S-ID --yes --json
```

The first apply also requires `--yes`; it is not an implicit confirmation bypass. `--replace-order` currently accepts only `delete-first`. This order deletes the old root before creating the replacement, so there is a period with no root. If creation fails after deletion, recovery does not restore the old root (nor does a failed create). The result includes exact previous/new root IDs, `replaced`, `definitionsReset`, counts, phase, evidence, diagnostics, `atomic: false`, and any retained pending operation.

Resonite may add Components on its own (for example UIX Components add a `RectTransform`). Regenerate does not fail on this: the Slot count must match the definition exactly, the Component count must be at least the defined count, and the result's `engineAddedComponents` reports how many extra Components were observed (`creation recover --action finish` reports it too). Engine-added Components are not in the definition, so they cannot be referenced or have values set from it; declare any Component whose values you need explicitly.

If Resonite removes a declared Component as a duplicate of one it already added itself (for example a declared `RectTransform` written after a UIX Component), regenerate writes the declared values to the engine-added Component and uses it, whatever the order in the definition, and reports the warning `CREATION_COMPONENT_MERGED_WITH_ENGINE_ADDED`. It adopts a candidate only when the owner Slot is fully observed and exactly one same-type candidate exists; otherwise it fails with `COMPONENT_NOT_FOUND`. `plan --mode regenerate` validates Component types and values the same way as `apply` (read-only). If a command timeout leaves a pending operation, use the `creation recover` guidance in the error `suggestions`.

If the recorded root was deleted by hand in Resonite, the next plan or apply creates a new root (nothing is deleted) when the recorded root ID is exactly absent in the matched session, and reports the warning `CREATION_PREVIOUS_ROOT_ABSENT`. Anything short of an exact absence observation (partial data, timeout, session change) still stops. A root left in the world after `--discard-pending` is not deleted automatically; check the IDs in the result and delete it by hand.

Keep the state file and read the full JSON result, especially `phase`, `pending`, `evidence` and `diagnostics`. Failures retain operation context such as `stateFile`, `operationId`, stage and exact IDs. An uncertain write or readback is not proof of success. Do not retry apply while a pending operation exists; plan and apply block on pending state until it is reconciled.

## Recover a pending operation

First inspect the saved state, operation ID, previous/candidate root IDs and the exact IDs reported in pending evidence. Confirm the currently selected discovery session. Recovery requires the same matched session proof (`--url auto --session S-ID`) and explicit `--yes`:

```powershell
resoloop creation recover --state creation-state.json --action rollback --url auto --session S-ID --yes --json
resoloop creation recover --state creation-state.json --action finish --url auto --session S-ID --yes --json
```

Choose `rollback` to clean up a candidate only when its complete ownership can be proven from the exact ID, parent, generation, creation stamp, uniqueness and subtree evidence. It retains a verified old root when one remains. If delete-first already removed the old root, rollback does not recreate it and records no active root when its absence is proven. A candidate without a proven creation stamp is never deleted automatically. Unknown IDs and incomplete observations remain pending; inspect only the exact reported IDs and do not guess from names, order or type.

Choose `finish` only when the pending operation has a staged candidate, a recorded complete tree and counts, and the candidate's structure and ownership can be reverified. Finish records that candidate as active; it does not replay or rewrite content. If these conditions are not met, the pending state is retained with an error and recovery context.

If world cleanup cannot be proven and the operator deliberately chooses to clear only local evidence, discard requires the exact operation ID and `--yes`:

```powershell
resoloop creation recover --state creation-state.json --discard-pending OP --yes --json
```

This is local-only: it does not connect, inspect, delete or otherwise clean the world. The active-root record is retained and the result warns about possible remaining root IDs. Discard does not make a later apply exempt from its normal state and ownership checks. Inspect the world and exact IDs before deciding what to do next.

`creation recover --bind-root` is unsupported, and `creation migrate` and `creation inspect` are unavailable. Use the existing ordinary `inspect` and `hierarchy` commands for bounded observation of exact IDs, then inspect again after any authorized change. Preserve Reflection-first behavior for Component/member details, keep observations bounded, delete only proven exact targets, and perform post-change inspection.
