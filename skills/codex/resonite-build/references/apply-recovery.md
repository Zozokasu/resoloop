# Inspect pending evidence before continuing

Preserve the reported state and the author's document. Read failure `context.reason`,
`stateFile`, `operationId`, `sendStatus`, `pending`, `confirmedBindings`,
`evidencePersistence`, expected/observed values and completeness. Use a new diagnostics
file for full evidence. Inspect only the exact reported Slot/Component IDs, with bounded
`inspect ID --members`, `component inspect ID`, or selected `observe` fields. Reflection
still establishes runtime types and members; cached metadata does not verify current values.

Before each send, apply compares the plan's values, types and connection. An observed
change or possible writer gives `APPLY_PRECONDITION_FAILED` (exit 6). A Component's
`IField<…>` reference to a target field can be a writer or a reader; inspect its actual
role before changing it. The target's own Component and references managed and written
by this apply are excluded. Writer coverage outside the observation is unknown, permits
writing and does not prove absence. Use `propertyModes` to declare config, creation-only
initial, runtime or driver-owned members. Omitted members retain `fields`/`initialFields`
semantics; ordinary apply never writes runtime or driver-owned values, including creation.
The declaration itself does not establish driver ownership.

State v3 stores confirmed correspondence and pending operations. v1/v2 remain readable,
saves use v3 and older CLIs reject it. A successful response is followed by one readback
of the sent attributes/members. A mismatch or incomplete result remains pending and
stops with `APPLY_WRITE_UNVERIFIED` (exit 7); no reference replay happens at the end.
Cancellation, timeout and persistence errors keep their existing error codes and also
require inspecting pending evidence. Do not promise convergence from an unconditional retry.

Explicit server rejection clears its pending operation and stops with the original error.
On a later apply, matching discovery identity, acceptance and exact ID/type/parent/owner
evidence allow confirmed results to commit or known mismatches to clear. Apply then plans
from the current observation. Unreadable targets, unknown creation IDs, unproven acceptance
or identity remain pending. Never recover a creation by name, type or ordinal. Explicit
`--url` connections have unknown identity and cannot automatically settle their interrupted
pending operations; a discovery-selected connection can provide matching identity. Preserve
the user's selected destination when arranging discovery; never switch worlds for recovery.

After inspecting the world, a user-authorized state recovery can discard only the selected
operation with `resoloop apply FILE --state STATE --discard-pending OPERATION_ID --yes`.
This is offline and exits without applying. It keeps confirmed bindings and never adopts
candidate bindings. Unknown/unconfirmed creations may already exist outside management;
a later apply can duplicate them. Updates/deletions retain their confirmed correspondence:
inspect current values or exact absence and review a new plan. Delete unwanted objects only
by verified exact IDs with `--yes`. Never clear all state or guess an ID to unblock work.

`APPLY_SESSION_BUSY` (exit 7) means a cooperating writer holds the same normalized URL's
exclusive handle, independent of project/cwd. The key is the normalized endpoint: scheme and
host are lowercased (IDN host in its ASCII form, trailing dot removed), any loopback address
(`localhost`, all of `127.0.0.0/8` such as `127.0.0.2`, and `::1`) becomes one `localhost`
host, and a default port equals its omission. A different scheme, port, path or query is a
different key. A hostname that resolves to the same Resonite process but is not a loopback
literal is a different key; whether two keys reach the same world is not verified. Locks live at `<LocalApplicationData>/ResoLoop/write-locks/<URL-hash>.lock`;
cache cleanup does not touch them. Apply/imports, direct Slot/Component mutations, raster
capture's temporary camera and authorized `test --probe` participate. Reads and offline SVG
do not; Flux deployment participates in this lock, holding it per deployment through readback and deploy-state commit. `APPLY_STATE_BUSY` is a separate lock on one
project state. Wait for the active holder; never infer staleness from a filename or PID and
never delete or steal a lock held by an active writer. OS handle release after process exit
permits the next acquisition.

The lock stores the last writer's state location before any pending save. A confirmed missing
file or directory allows writing to continue; access denial is not absence. Pending or
unreadable previous state blocks other projects with `APPLY_WRITE_UNVERIFIED`, reason
`previousStatePending`/`previousStateUnreadable`, `stateFile` and `lockFile`. Corrupt lock records
also stop with `previousStateUnreadable` and `lockFile`; the state path may be unknown.
Repair the state or resolve pending evidence in the original project using its document/state
and exact targets. Inspect before explicitly discarding its chosen operation. Never
automatically repair another project's journal. If the state is permanently lost, verify
that no ResoLoop writes are running and inspect the live world before deleting the reported
`lockFile`. Never delete or steal a lock held by an active writer.

Before prune, review `diff --deletes-only` and use `--prune --yes` only for the intended
owned targets. Prune and relocation-source deletion recheck stable key, exact ID and owner
Slot immediately before sending. Recursive Slot deletion requires complete child/component
coverage and refuses depth truncation, missing evidence or unmanaged descendants. Engine
created components do not become owned merely by appearing. Only exact `SLOT_NOT_FOUND` /
`COMPONENT_NOT_FOUND` readback proves absence. Lost deletion responses remain pending;
absence alone does not prove acceptance. Direct delete/remove retain their existing exact
target, `--yes` and Root protections; the apply ownership checks are not added to them.

## Driver replacement

For a driver replacement blocked by the old owner, remove the old driver from the declaration
first, review and apply with `--prune --yes`, inspect absence, then add the new driver and
apply. One apply stops at its first readback mismatch. Re-inspect values and references after
each completed stage. Never select a candidate by ordinal, `componentIndex`, name or type.

## Exit after a failed replacement settled into `STABLE_COMPONENT_AMBIGUOUS`

Situation: the one-step replacement stopped on `APPLY_WRITE_UNVERIFIED` (`readbackMismatch`);
a later apply settled the pending record into the checkpoint, and the next apply now stops with
`STABLE_COMPONENT_AMBIGUOUS` and no pending. The state then holds `components[OLD_KEY]` (old
driver, still correct) and `components[NEW_KEY]` (the failed-created Component, which is
not a verified replacement). The recorded failed ID is a confirmed record, not proof of
ownership. This is a manual repair of two files you already own; there is no recovery command
and none may be assumed. Do not overwrite an ID with a guessed candidate, clear the state or
select by ordinal.

1. Stop every writer (this CLI, other projects on the same endpoint, people, tools). Use the
   same selected session and re-read current IDs; IDs from another session or an earlier
   run mean nothing here. Do not proceed if any ID cannot be read.
2. Confirm exact IDs. Take OLD_ID and FAILED_ID from `candidateIds`, the stopped error and
   `state.components`. Inspect the owner Slot with `inspect OWNER_SLOT_ID --members`, and each
   ID with `component inspect ID`. Require the expected type and owner Slot, the old driver's
   reference to the intended target, and the failed Component's missing/wrong reference.
   Anything else (extra siblings, wrong parent, other target) stops this procedure.
3. Byte-copy the state file before editing (for example `Copy-Item STATE STATE.before-repair`,
   `cp -n STATE STATE.before-repair`). Keep it unchanged until the end.
4. Remove only the failed Component: `resoloop component remove FAILED_ID --yes` with the
   same connection options. It is a direct exact-ID removal and does not use the apply
   ownership checks. Then `component inspect FAILED_ID` must report `COMPONENT_NOT_FOUND`.
5. Only after that absence proof, edit the state JSON manually and remove only
   `components[NEW_KEY]`. Keep `schemaVersion`, `ownershipKey`, `sessionId`, slots, assets,
   every other component binding and the empty `pending` unchanged in meaning; compare
   with the backup. Do not touch `components[OLD_KEY]`.
6. Re-plan with the intermediate declaration that contains neither driver:
   `resoloop diff INTERMEDIATE --state STATE --require-state --deletes-only --json`.
   It must list only the verified old-driver deletion. Then `resoloop apply INTERMEDIATE --state STATE
   --require-state --prune --yes`, and `component inspect OLD_ID` must report
   `COMPONENT_NOT_FOUND`.
7. Add the new driver to the declaration and `resoloop apply FILE --state STATE
   --require-state`. Verify the new Component's ID, owner Slot and reference with
   bounded `inspect`, that no pending remains and that unrelated bindings and the backup
   are unchanged.

If a stored-ID/reference contradiction stops with `APPLY_STORED_ID_UNVERIFIED`
(`componentEvidenceMismatch`) before any write, state is deliberately preserved and an
unmanaged sibling does not receive ownership. Inspect the recorded target and actual
references of every named ID. The only repair offered here is the exact-ID removal in step 4
and the single-record edit in step 5 for a Component you have proven is the failed-created
one; if the contradiction involves anything else, stop and report instead of editing.

Limits: readback does not promise retention; nothing here is atomic, exclusive of other
writers, or verified against a live Resonite session. If these steps do not fit (for
example, the failure cannot be tied to one removable Component), stop.

These checks detect observed conflicts and coordinate this and later cooperating ResoLoop
versions on the same PC and OS user. Operations remain `atomic:false`, with no rollback or
exclusion of older CLIs, other PCs/users, people, external tools or ProtoFlux. Unobserved
changes and changes between checking and sending remain possible. Readback does not prove
future retention. Report structural, visual and live behavior evidence separately.
