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
exclusive handle, independent of project/cwd. Host case, loopback aliases and default ports
normalize together. Locks live at `<LocalApplicationData>/ResoLoop/write-locks/<URL-hash>.lock`;
cache cleanup does not touch them. Apply/imports, direct Slot/Component mutations, raster
capture's temporary camera and authorized `test --probe` participate. Reads and offline SVG
do not; Flux deployment is outside this lock. `APPLY_STATE_BUSY` is a separate lock on one
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

For a driver replacement blocked by the old owner, remove the old driver from the declaration
first, review and apply with `--prune --yes`, inspect absence, then add the new driver and
apply. One apply stops at its first readback mismatch. If a replacement was already attempted,
settle its pending evidence and inspect ambiguous candidates before this staged workflow;
never select by ordinal. Re-inspect values and references after each completed stage.

These checks detect observed conflicts and coordinate this and later cooperating ResoLoop
versions on the same PC and OS user. Operations remain `atomic:false`, with no rollback or
exclusion of older CLIs, other PCs/users, people, external tools or ProtoFlux. Unobserved
changes and changes between checking and sending remain possible. Readback does not prove
future retention. Report structural, visual and live behavior evidence separately.
