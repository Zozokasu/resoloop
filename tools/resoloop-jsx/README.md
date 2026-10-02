# resoloop-jsx

Author resoloop schema-v1 apply documents in TSX instead of hand-editing JSON.
A `.tsx` entry file describes a Resonite Slot/Component tree with JSX; the CLI
type-checks it, evaluates it eagerly (function components, arrays, and
conditionals are resolved at evaluation time — there is no virtual DOM or
re-render), and emits one `ApplyDocument` JSON file that `resoloop validate`
accepts offline.

Built with TypeScript `5.9.3` (the only dependency; pinned, no `@types/node`).

## Install / build

```sh
npm install
npm run build        # tsc -> dist/
npm test             # unit + fixture pipeline tests (node:test)
npm run contract     # additionally validates every fixture against the
                     # dotnet-built resoloop CLI (requires dotnet build ResoLoop.slnx)
```

## Build a project outside this repository

Build this package first (`npm run build` in `tools/resoloop-jsx`). In your
external project's directory, prepare a local dependency so both TypeScript
and the emitted JavaScript can resolve `node_modules/resoloop-jsx`:

```powershell
npm install --offline --save C:\path\to\resoloop\tools\resoloop-jsx
```

The package is private and is not installed from a registry. Use the local
package directory with its built `dist/` and existing dependencies. When working
entirely offline, a directory link at `PROJECT/node_modules/resoloop-jsx` to that
built package is also sufficient; no global install resolves project imports.

Set `"type": "module"` in the **project's** `package.json`, for example:

```json
{ "private": true, "type": "module", "dependencies": { "resoloop-jsx": "file:C:/path/to/resoloop/tools/resoloop-jsx" } }
```

Export a single `<Slot>` as the entry's default export, with optional named
`ownership`, then run from the project directory:

```powershell
node C:\path\to\resoloop\tools\resoloop-jsx\dist\src\cli.js build main.tsx -o out.json
```

Without the project's `"type": "module"`, NodeNext may compile the entry as
CommonJS and deliver `{ default: { ownership, default } }`. This reports
`ROOT_MUST_BE_SINGLE_SLOT` with the package.json cause. Add the module setting
and rebuild; do not unwrap or alter the TSX root to work around it.

## Usage

```sh
resoloop-jsx build <entry.tsx> -o <out.json> [--draft] [--project-root PATH]
# or without installing:
node dist/src/cli.js build <entry.tsx> -o <out.json> [--draft] [--project-root PATH]
```

## Request-bound build bundles (V12)

For a request-bound handoff, generate a fresh ID **before** each build and use a new output directory:

```powershell
$requestId = [guid]::NewGuid().ToString("N")
$bundleFile = "build/$requestId/bundle.json"
node dist/src/cli.js build content/main.tsx --bundle --catalog catalog.json --build-id $requestId -o $bundleFile
if ($LASTEXITCODE -ne 0) { throw "Build failed; do not consume an earlier bundle" }
resoloop validate $bundleFile --build-id $requestId --json
# Run diff/apply only for an authorized live task with a verified, non-synthetic catalog.
resoloop diff $bundleFile --build-id $requestId --state VERIFIED_STATE --require-state --json
resoloop apply $bundleFile --build-id $requestId --state VERIFIED_STATE --require-state --json
```

`--bundle` requires `--catalog FILE` and `--build-id R`; these options are rejected without bundle mode. Existing output files **and directories** are refused. The producer writes one complete JSON envelope to a temporary file in the new output directory, closes it, rechecks inputs and renames it to the final path. Only successful publication returns exit 0. A failed typecheck, emit, evaluation, input check or publication leaves no final bundle for that request; no earlier output is searched or reused. Ordinary builds retain their JSON, exit 0/1/2 and BuildError contracts and require no dotnet.

The envelope fields are `kind: "resoloop-build-bundle"`, `bundleVersion: "1"`, `buildId`, `completion: "committed"`, `buildStages` (`typecheck`, `emit`, `evaluate`, `inputs`), `inputs`, `ir`, `map`, `usedTypes`, `catalog`. Each payload stores its original `text` and the SHA-256 of its UTF-8 bytes. `inputs.status` is `complete`; each file records absolute `path`, raw-byte `sha256`, and `role` (`source` or `catalog`). Every TypeScript Program source file is recorded, including declarations/default libraries, plus the explicit catalog. Static local imports must stay inside the project, except the authoring runtime. Dynamic import/require, Node builtins (`fs`, `node:*`) and external packages other than `resoloop-jsx` runtime stop bundle publication. This is import-graph inspection, **not statement or call-target analysis**: environment variables, time and other implicit inputs cannot be detected and are outside this guarantee.

The map has `version`, `buildId`, `irSha256`, `sources` (path/hash) and `entries` (IR `jsonPath` and `source`). The producer emits known original AST ranges for tracked origins and explicit `{ "status": "unknown" }` for untracked locations; maps containing only unknown locations remain accepted. Core validates every payload, completion stages, request, input snapshot, shared IR/catalog semantics and the ordinal sorted set of resolved Component full names. Node identifies types without duplicating the semantic validator.

`validate|diff|plan|apply BUNDLE --build-id R` consume this single snapshot without Node. Detection uses `kind`/`bundleVersion`, not the extension; damaged/unsupported bundles never fall back to ordinary JSON. Bundle input requires the external request ID, while ordinary JSON rejects `--build-id`. Checks run before connection and inputs are checked again after preparation and immediately before the first mutation. `APPLY_BUILD_BUNDLE_INVALID` uses exit 6 and `context.reason` (`requestMismatch`, `uncommitted`, `mixed`, `inputChanged`, `inputUnknown`). Missing/invalid arguments use existing exit 2 codes. Only the embedded catalog is used; omit CLI `--catalog`. Unavailable catalog evidence retains `APPLY_CATALOG_UNAVAILABLE`. Connected commands (`diff`, `plan`, `apply`, `validate --strict`) reject synthetic catalogs and require session/client version matching. Workbench rejects bundles with `BACKEND_UNSUPPORTED`.

For source diagnosis, add `--diagnostics NEW_FILE.json` to `validate|diff|plan|apply`. This separate JSON has `diagnosticVersion: "1"` and structured key/member/IR path, known/unknown source, expected/observed evidence and completeness; a known null is distinct from unknown. Bundle map v1 stores original TS/TSX UTF-16 AST offsets and one-based, end-exclusive line/column ranges bound to source hashes. Fix TSX using that location, then create a fresh bundle/request; generated JSON lines are not an authoring location. Unchanged function props forwarded directly preserve caller expressions; rewritten scalar or nested object props have unknown primary locations; Scope, fragments, map callbacks and selected conditional children retain original ranges. Spread/computed members and untracked value transfers have unknown primary locations with real expressions/calls as related evidence. Handwritten and legacy generated JSON also accept diagnostics, with unknown source. Legacy stdout/stderr JSON, context.issues and reports are unchanged. Diagnostics require a new file in an existing writable directory, distinct from the selected input/bundle, catalog and state paths (including missing files); writing failure is reported on stderr without changing validation/apply judgement or exit code. Catalog evidence never completes runtime verification.

The bundle and map versions remain "1". Each map entry carries `jsonPath`,
`pathSegments` (string property names / integer array indices), `entityKind`,
final scoped `key`, optional `member`, `source` (property-name range),
`valueSource` (reference-producing expression) and `related` ranges. Fragment
nodes have no IR entry. Repeated map iterations use the same callback range.
Metadata stays in private WeakMaps through element copying and IR creation.
The consumer checks every known source/related/value range's file, input hash,
offset bounds, ordering and matching line/column; an ambiguous match is unknown.
Old map entries containing only unknown source remain valid.

For example, after a successful bundle build:

```sh
resoloop validate NEW_DIRECTORY/bundle.json --build-id R --diagnostics diagnostics-R.json --json
```

The diagnostics file is `{ "diagnosticVersion": "1", "diagnostics": [...] }`.
Each diagnostic carries `diagnosticVersion`, `code`, `severity`, `message`,
`phase` (validate/diff/plan/apply), `buildId`, `entityKind`, `key`, `member`,
`jsonPath`, `pathSegments`, `source`, `related`, `expected`, `observed`, and
`completeness`. Known source is `{status:"known",file,sha256,range:{start,end}}`;
points have `offset`, `line`, `column`. Unknown source is `{status:"unknown"}`.
Expected/observed are `{status:"known"|"unknown",value}`, with `value:null`
retained for both known null and unknown. Completeness contains
`location/type/member/reference/inputs/runtime`, each complete/partial/unknown.
Runtime remains unknown for schema/catalog checks. Exceptions lacking structured
information have unknown source; legacy issue paths are not parsed for origins.
TypeScript build errors precede semantic validation and retain TypeScript's
original-source diagnostics and exit 2; a failed build publishes no bundle.

Ordinary handwritten/generated JSON still works without bundles, Node, extra warnings or a freshness guarantee. Bundle IR retains schema `"1"`, authoring project/state resolution and explicit `--state`/`--require-state`. Request IDs and completion trust the producer; reused IDs or forged bundles are not authenticated. The gap after the final check, changes after writing starts, world preconditions, locks and response loss remain outside S2-4.

## Entry documents

The entry file must `export default` a single `<Slot>` element:

```tsx
import { Slot, Component, ref } from "resoloop-jsx";

export default (
  <Slot key="my-world" name="My World">
    <Slot key="target" name="Target" position={[0, 1, 0]} />
    <Slot key="holder" name="Holder">
      <Component
        key="grab"
        type="FrooxEngine.Grabbable"
        fields={{ TipReference: ref.slot("target") }}
      />
    </Slot>
  </Slot>
);
```

Use a named export when ownership differs from the root key:

```tsx
export const ownership = { key: "house-world" };
export default <Slot key="root" name="House" />;
```

Without the export, ownership remains the root Slot key. An exported empty key,
undefined value, or object with an invalid shape fails as an input/build error
(exit 1). Ownership never prefixes or changes Slot/Component keys.

The builder resolves the project base once: explicit `--project-root PATH`
(cwd-relative) > nearest `.resoloop.json` ancestor of the author TSX > TSX directory.
It adds optional top-level `authoring` metadata with absolute `projectRoot`,
project-relative `source`, and diagnostic `ownershipSource` (`entry-export` or
`root-key`). Output location and copying the generated JSON do not change the
state path. Rebuild after moving the whole project. These generated documents
require a CLI that supports `authoring`; handwritten JSON still works without Node.

For an existing JSON project, back up the original state and preserve ownership
and every effective key from it, including keys omitted in the old JSON. Build
without draft key generation, then use `resoloop diff OUTPUT.json --state OLD_STATE.json
--require-state --json` before applying with the same state and flag. Confirm zero
diff, preserved IDs, and zero diff after apply. The flag stops on a missing
checkpoint instead of creating an empty state. See [migration details](../../README-DETAILS.md).

Every `<Slot>` and `<Component>` carries an explicit stable `key` prop. With
the normal entry point (`import { Slot, Component } from "resoloop-jsx"`)
`key` is required by the TypeScript types — omitting it fails type-checking
(exit code 2) before the entry is evaluated.

`ref.slot(key)` / `ref.component(key)` / `ref.member(key, member)` /
`ref.slotMember(key, member)` produce the `$slot:` / `$component:` /
`$member:` / `$slot-member:` selector strings.

## Reusable subtrees and instance scopes

Wrap each new reusable subtree in `<Scope instanceKey="left">`. Existing
function components keep their current keys unless you explicitly add a Scope.

```tsx
import { Slot, Component, Scope, ref } from "resoloop-jsx";
function Part({ name }: { name: string }) {
  return <Slot key="body" name={name}>
    <Component key="state" type="CALLER_REFLECTED_TYPE" />
    <Component key="wire" type="CALLER_REFLECTED_REFERENCE_TYPE"
      fields={{ Target: ref.component("state") }} />
  </Slot>;
}
export default <Slot key="root" name="Assembly">
  <Scope instanceKey="left"><Part name="Left" /></Scope>
  <Scope instanceKey="right"><Part name="Right" /></Scope>
</Slot>;
```

This emits `left::body`, `left::state`, `left::wire` and the corresponding
`right::` keys. Give sibling Slots distinct names as before. Instance keys do
not depend on sibling order. Nested Scopes emit `outer::inner::local`.
Every scoped Slot/Component needs an explicit local key, even with `--draft`.
Instance/local segments must be non-empty and cannot contain `:`; all colons
are reserved to prevent ambiguous `::` boundaries. Legacy keys outside Scope
remain literal, including existing colons. Duplicate scopes or flattened keys
(including collisions with legacy keys) fail instead of receiving a suffix.

Short `$slot:`, `$component:`, `$ref:`, `$member:` and `$slot-member:` selectors
inside fields/initialFields resolve only in the current Scope, recursively in
arrays and objects. They never fall back to a parent Scope or global key.
Qualified keys containing `::` are absolute: from outside use
`ref.component(ref.key("left", "state"))` or `$member:left::state.Value`.
The same explicit form can reference another Scope from inside. Scoped
`migrateFrom` uses this rule too; adding Scope to existing content is not an
automatic migration. Preserve existing effective keys and state when rebuilding.
The JSON source equivalent is `"$scope":"left"` on a node wrapping slot,
components and children; expansion removes it before schema-v1 validation.
Invalid segments or unresolved scoped references fail the TSX build with
`APPLY_SCOPE_INVALID`; C# uses `APPLY_SCOPE_INVALID` for invalid segments and
existing key-conflict/reference error codes for collisions/missing targets.

## Entry points: normal vs draft

- `import { Slot, Component } from "resoloop-jsx"` — `key` is required in the
  prop types; omitting it fails TypeScript diagnostics (exit code 2).
- `import { Slot, Component } from "resoloop-jsx/draft"` — the same runtime
  functions, but `key` is optional in the types. Intended for prototyping a
  tree. Without `--draft` a build still fails at evaluation with
  `EXPLICIT_KEY_REQUIRED` (exit code 1); with `--draft`, a deterministic key
  of the form `<parentKey>/<slug(name-or-type)>#<sameKindSiblingIndex>` is
  generated per missing key and one warning each is printed to stderr
  (exit code 0). Such output carries the source guard `"$draftKeys":true`;
  validate/diff/plan/apply reject it with `APPLY_DRAFT_KEY_UNSTABLE`.
  Copy intended effective keys into explicit props and rebuild before applying.
  The old generated strings remain unchanged; inserting a sibling can shift
  their index, so they are inspection-only. Draft builds with all explicit keys
  have no guard and remain applicable.

Everything other than `Slot`/`Component` prop typing (`ref`, `Fragment`,
`Scope`, `BuildError`, `evaluate`, and all shared types) is identical from either
entry point.

## Exit codes

| code | meaning |
| --- | --- |
| 0 | success; JSON written |
| 1 | input or build error (bad arguments, missing entry, `EXPLICIT_KEY_REQUIRED`, `DUPLICATE_KEY`, `DUPLICATE_SIBLING_NAME`, `INVALID_CHILD`, `ROOT_MUST_BE_SINGLE_SLOT`, `SLOT_NAME_MISSING`, `NON_FINITE_NUMBER`, module load failure) |
| 2 | the entry file has TypeScript diagnostics and was not executed |

`npm run contract` additionally uses exit code `3` when `resoloop.dll` was not
found (the .NET CLI was not built — the contract test did not run).

## Supported schema-v1 subset

Emitted documents contain only `schemaVersion` (`"1"`), `ownership`, `slot`,
`components`, `children`, and build `authoring` metadata (plus the source-only
`$draftKeys` guard when keys were generated). `ApplySlotSpec` fields supported as props:
`name` (required), `key`, `parent` (root only), `position`, `rotation`,
`scale`, `managedFields`, `preserveWorldTransform`, `migrateFrom`,
`relocationTransform`, `runtimeRelocatable`. `ApplyComponentSpec` fields:
`type` (required), `key`, `fields`, `migrateFrom`, `initialFields`,
`identityFields`, `propertyModes`. `assets`, `cameras`, `tests`, `include`, `prototypes`,
`parameters`, and `variables` are out of scope; because `assets` declarations
are not yet supported, no `ref.asset` helper is exposed.

## Regenerating the Apply contract (developers)

The scalar prop types, schema-v1 output types and copy functions under `src/generated/`
are generated from the C# Apply records and their shape attributes. From the repository root:

```powershell
dotnet run --project tools/RLoop.ContractGen -- tools/resoloop-jsx/src/generated
```

Commit the generated files with the C# declaration change. Do not edit them by hand.
The .NET `GeneratedArtifacts_AreCurrent` test detects stale files and tolerates checkout
CRLF conversion; the generator writes deterministic UTF-8 without BOM and LF line endings.
`npm test` and TSX build use the checked-in files and do not run dotnet. The existing
`npm run contract` command still requires the built .NET CLI.

For a normal copied field, update its C# record (and any exceptional shape rule), an
independent handwritten fixture, the authoring documentation, and affected workflow
skills, then regenerate once. Do not copy the field into TS types, scalar lists, copy
functions, or C# typo candidates. Fields that change world behavior still need C#
execution changes and behavior tests. CLR nullable, constructor required, JSON required,
JSX required, and emitted-output required remain separate conditions. C# defaults are
descriptive metadata and are never inserted by the generated copy functions.

`test/fixtures/generated-shape-oracle.expected.json` is a handwritten output oracle,
not a generator target. Expected JSON changes require deliberate review.

## Catalog validation (V11)

After building an IR file, run `resoloop validate FILE.json --catalog CATALOG.json --json`. This is a C# Core check and runs without Node or a Resonite connection. No separate TypeScript member validator is introduced. Catalogs must carry acquisition identity, provenance and an intact content hash; missing/unconfirmed evidence and unknown reference closure fail with `APPLY_CATALOG_UNAVAILABLE`. Proven incompatible references use `APPLY_REFERENCE_TYPE_MISMATCH`; invalid/non-finite/out-of-range Single values use `VALUE_CONVERSION_FAILED`, including nullable non-null values and tuple elements. Rounding is allowed; member-specific ranges are not guessed. Both catalog error codes retain validation exit 6 and the existing issue format. Success keeps `strict: false`; adding `--strict` also requests the existing live validation after catalog preflight and session version comparison against the same snapshot; synthetic catalogs are rejected before connection.

`test/fixtures/catalog-v11/catalog.synthetic.json` is a fixed **synthetic** original with identity and content hash. `oracle.handwritten.json` independently fixes expected member types, case inputs and diagnostic codes/paths; do not derive or regenerate its expectations from the catalog or generated Apply types. `npm run contract` runs these cases and request-bound bundle handoffs through the actual offline CLI alongside the unchanged existing fixtures. This does not establish real Component/runtime verification. [CatalogExport](../RLoop.CatalogExport/README.md) provides developer-only export/import; legacy reflection caches without acquisition identity are not catalogs. Source locations are covered by the independent source-v11 oracle.
## propertyModes

`<Component propertyModes={{ Config: "config", Seed: "initial", Clock: "runtime", Driven: "driver-owned" }}>`
declares member policies. Omitted members retain the existing behavior: `fields` is config and
`initialFields` is initial (creation only). Ordinary apply never writes runtime or driver-owned
members, including on creation. A driver-owned declaration does not prove writer ownership.

## Applying and recovering the emitted document

Build emits schema `"1"`; apply state is v3, with confirmed bindings and pending evidence.
Older v1/v2 states are readable and save as v3; older CLIs reject v3. Preserve the state
and stable keys when changing TSX. Apply compares planned values/types and connection before
each send; observed conflicts or a possible writer referencing the target field give
`APPLY_PRECONDITION_FAILED` (exit 6). Writer evidence outside the bounded observation is
`unknown`, permits writing and never proves absence. Sent values are read back once;
incomplete/mismatched results stop with pending evidence and `APPLY_WRITE_UNVERIFIED` (exit 7).
References are not replayed at the end, and lost creation IDs are not recovered by name/type/order.

Explicit server rejection clears pending evidence. A later apply with matching discovery
identity, acceptance and exact target/owner evidence settles confirmed results or known
mismatches, then plans from current values. Explicit `--url` connections have unknown identity;
their interrupted pending operations cannot be automatically settled. Discovery-selected
connections can provide matching identity. Read failure context and diagnostics, inspect exact
IDs, and only after inspection use `resoloop apply FILE --state STATE --discard-pending OPERATION_ID --yes`.
This offline operation preserves confirmed bindings and does not adopt creation candidates;
discarded creations can be duplicated. Discarded updates/deletions require inspecting the
existing correspondence and re-planning. Keep the author's document and state together.

Writers to the same normalized URL share an exclusive handle under
`<LocalApplicationData>/ResoLoop/write-locks/<URL-hash>.lock`, across projects on one PC/OS user.
Apply (including imports), direct Slot/Component mutations, raster capture and authorized
probes participate; reads, offline SVG and Flux deployment do not. Contention returns
`APPLY_SESSION_BUSY` (exit 7); `APPLY_STATE_BUSY` remains a separate state-file lock.
The lock records the last writer's state location before pending persistence. A confirmed
missing file or directory allows writing to continue; access denial is not absence. Pending
or unreadable state, or a corrupt lock record, blocks other projects with `APPLY_WRITE_UNVERIFIED`.
Failure context includes `stateFile` and `lockFile`; a corrupt record can leave `stateFile` unknown.
Repair the state or resolve pending evidence using the original project's document/state;
inspect exact targets before explicitly discarding its chosen operation. If the state is
permanently lost, verify that no ResoLoop writes are running and inspect the live world before
deleting the reported `lockFile`. Never delete or steal a lock held by an active writer.

Prune needs `--prune --yes`. Both prune and relocation-source removal recheck stable key,
exact ID and owner before deletion. Slot deletion requires complete subtree child/component
coverage and refuses unmanaged contents, including unowned automatically added components.
Only exact `SLOT_NOT_FOUND` / `COMPONENT_NOT_FOUND` readback proves absence. Direct delete/remove
conditions retain their existing target/confirmation/Root protections. For a driver replacement
blocked by the old owner, first remove the old driver declaration and prune it after reviewing
diff; inspect absence, then add the new driver and apply. One apply stops at the first mismatch.

These checks coordinate this and later cooperating local ResoLoop versions and detect observed
conflicts. Apply remains `atomic:false`: no rollback, exclusion of external writers/ProtoFlux,
older CLIs or other PCs/users, nor detection of every unobserved race or future retention.
See [the recovery workflow](../../skills/codex/resonite-build/references/apply-recovery.md).
