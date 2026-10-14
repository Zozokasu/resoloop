# resoloop-jsx

Author resoloop schema-v1 apply documents or experimental creation envelopes in TSX instead of hand-editing JSON.
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

See [Key rules](#key-rules) for how `--draft` handles omitted keys.

## Key rules

| Use | Import | Build | Apply | Key |
| --- | --- | --- | --- | --- |
| Regenerate (experimental) | `resoloop-jsx` | `build entry.tsx -o out.json --target creation` | `apply out.json --mode regenerate --replace-order delete-first --yes --url auto --session S-ID` | May be omitted. `Scope` `instanceKey` is required. |
| Normal | `resoloop-jsx` | `build entry.tsx -o out.json` | `apply out.json` | Required on all nodes. Missing keys fail with `EXPLICIT_KEY_REQUIRED` (exit code 1, with a TSX location). |
| Draft (inspection only) | `resoloop-jsx` | `build entry.tsx -o out.json --draft` | Not applicable (`APPLY_DRAFT_KEY_UNSTABLE`) | May be omitted. |

With `--draft`, missing keys are generated as `<parentKey>/<slug(name-or-type)>#<sameKindSiblingIndex>` and one warning per generated key is printed to stderr. The output carries the source guard `"$draftKeys":true`; validate/diff/plan/apply reject it with `APPLY_DRAFT_KEY_UNSTABLE`. Copy intended effective keys into explicit props and rebuild before applying. The old generated strings remain unchanged, but inserting a sibling can shift their index, so they are inspection-only. Draft builds with all explicit keys have no guard and remain applicable.

Explicit keys must be unique in every use. Normal builds also require distinct sibling Slot names (`DUPLICATE_SIBLING_NAME`); creation target allows repeated sibling names.

`resoloop-jsx/draft` is a compatibility alias for the main entry point; do not use it for new TSX.

## Experimental TSX creation input

To create an envelope for the experimental whole-root regenerate workflow, pass `--target creation`:

```tsx
import { Slot, Component } from "resoloop-jsx";

export const ownership = { key: "room" };

export default <Slot name="Room">
  <Slot name="Lamp"><Component type="REFLECTION_VERIFIED_FULL_COMPONENT_TYPE" /></Slot>
  <Slot name="Lamp"><Component type="REFLECTION_VERIFIED_FULL_COMPONENT_TYPE" /></Slot>
</Slot>;
```

The entry's default export must evaluate to one root `<Slot>`. See [Key rules](#key-rules) for key requirements. Creation builds do not use `--draft`, `--bundle`, `--catalog`, or `--build-id`; this version has no creation bundle.

`creationId` comes from the entry's named `ownership` export key when present, otherwise from the root Slot's explicit key. If neither supplies a valid key, build fails with `CREATION_ID_INVALID`. The value must contain 1–128 characters and no control characters; generated positional keys never become `creationId`.

Build and validate the envelope offline, then use regenerate mode for planning and applying:

```powershell
resoloop-jsx build content/room.tsx --target creation -o creation.json --project-root .
resoloop validate creation.json --json
resoloop plan creation.json --mode regenerate --state creation-state.json --url auto --session S-ID --json
resoloop apply creation.json --mode regenerate --state creation-state.json --replace-order delete-first --url auto --session S-ID --yes --json
```

`validate` recognizes creation envelopes by their contents, runs offline without `--mode`, and does not accept a mode option. `plan` and `apply` require `--mode regenerate`; both run the creation regenerate preflight before connecting. Unsupported Asset, Flux, Script, and other creation declarations stop there. Creation TSX and sibling-name regeneration have not been verified in a live Resonite session. See the [creation regenerate and recovery workflow](../../skills/codex/resonite-build/references/creation-regenerate.md) for session matching, exact-root inspection, deletion, pending-state, recovery, and post-change inspection safeguards.

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

`validate|diff|plan|apply BUNDLE --build-id R` consume this single snapshot without Node. Detection uses `kind`/`bundleVersion`, not the extension; damaged/unsupported bundles never fall back to ordinary JSON. Bundle input requires the external request ID, while ordinary JSON rejects `--build-id`. Checks run before connection and inputs are checked again after preparation and immediately before the first mutation. `APPLY_BUILD_BUNDLE_INVALID` uses exit 6 and `context.reason` (`requestMismatch`, `uncommitted`, `mixed`, `inputChanged`, `inputUnknown`). Missing/invalid arguments use existing exit 2 codes. Only the embedded catalog is used; omit CLI `--catalog`. Unavailable catalog evidence retains `APPLY_CATALOG_UNAVAILABLE`. Connected commands (`diff`, `plan`, `apply`, `validate --strict`) reject synthetic catalogs and require session/client version matching.

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

See [Key rules](#key-rules) for when `<Slot>` and `<Component>` keys are required.

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
`relocationTransform`, `runtimeRelocatable`, `tag`. `ApplyComponentSpec` fields:
`type` (required), `key`, `fields`, `migrateFrom`, `initialFields`,
`identityFields`, `propertyModes`, `fieldAliases`. `assets`, `cameras`, `tests`, `include`, `prototypes`,
`parameters`, and `variables` are out of scope; because `assets` declarations
are not yet supported, no `ref.asset` helper is exposed.

### Field children and aliases

`<Component>` may contain `<Field name="Intensity" value={2} />`. `name` is the declared runtime member name; verify the type and member with Resonite Reflection before using it. `key` optionally supplies a short alias, and `ref.field("brightness")` emits `$field:brightness`. Alias names must be unique across the expanded document. Scope-local short keys do not fall back outside that scope; use a fully qualified key to cross scopes.

Without `mode`, a Field inherits `propertyModes[name]` when present; otherwise it is `config` and emits under `fields`. `mode="initial"` emits under `initialFields`. `runtime` and `driver-owned` values are never written, including during creation. A value may be omitted for `runtime` or `driver-owned`; the Field itself declares that member in `propertyModes`, and its optional `key` adds an alias. Alias targets must appear in `fields`, `initialFields`, or `propertyModes`. Do not declare the same member through both `fields`/`initialFields` and `<Field>`, or through multiple Field children. The generated types apply only to the `fields` and `initialFields` of `<Component type="...">`. Legacy `<Field>` children keep the loose `JsonValue` type, so TypeScript does not check their member names or values. For catalog-bound checks, use `componentFields(type, catalog)` and its `Lamp.Field` method; it checks confirmed member names and values. C# catalog validation remains authoritative.

`<Slot tag="...">` maps to optional JSON `slot.tag`. Omit it to preserve an existing Tag without a plan change; pass an empty string to clear it. Slot Tag writes use the existing pre-send check, pending evidence and one readback. Live-verified on 2026-10-03 (Resonite 2026.9.18.82, ResoniteLink 0.13.1.0): creating and updating a Slot with a declared tag applied and matched the readback; removing the tag from the declaration left the live value unchanged; `tag: ""` read back as an empty string with no pending evidence left (an unset Tag is returned as null). Runtime Reflection remains unverified.

JSON `fieldAliases` is an optional alias-to-member dictionary. It does not change `schemaVersion: "1"`. The compiler resolves `$field` references to canonical `$member:componentKey.Member` selectors; state v3 and downstream `observe` selectors continue to use `$member`.

For the equivalent JSON form, use `slot.tag` and `fieldAliases` on the provider Component:

~~~json
{
  "schemaVersion": "1",
  "ownership": { "key": "lamp" },
  "slot": { "key": "root", "name": "Lamp", "tag": "lighting" },
  "components": [
    { "key": "light", "type": "REFLECTION_VERIFIED_LIGHT_TYPE", "fieldAliases": { "brightness": "REFLECTION_VERIFIED_LIGHT_MEMBER" }, "fields": { "REFLECTION_VERIFIED_LIGHT_MEMBER": 2.0 } },
    { "key": "controller", "type": "REFLECTION_VERIFIED_CONTROLLER_TYPE", "fields": { "REFLECTION_VERIFIED_CONTROLLER_MEMBER": "$field:brightness" } }
  ]
}
~~~

The TSX equivalent is:

~~~tsx
import { Slot, Component, Field, ref } from "resoloop-jsx";

export default <Slot key="root" name="Lamp" tag="lighting">
  <Component key="light" type="REFLECTION_VERIFIED_LIGHT_TYPE">
    <Field name="REFLECTION_VERIFIED_LIGHT_MEMBER" value={2} key="brightness" />
  </Component>
  <Component key="controller" type="REFLECTION_VERIFIED_CONTROLLER_TYPE"
    fields={{ REFLECTION_VERIFIED_CONTROLLER_MEMBER: ref.field("brightness") }} />
</Slot>;
~~~

Replace every `REFLECTION_VERIFIED_*` type/member placeholder with a name confirmed by runtime Reflection. The example shows authoring syntax; it does not certify that those runtime members exist.

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

For regular CLI authoring, use Reflection to confirm each exact full runtime Component type name and save the names as a JSON array. Then capture metadata and generate optional TypeScript editor declarations:

```powershell
resoloop catalog capture --types component-types.json --output catalog.json --url ws://localhost:<current-port>
resoloop catalog types catalog.json
resoloop-jsx build content/main.tsx -o build/main.json
resoloop validate build/main.json --catalog catalog.json --json
```

`--url` may be omitted to use the existing CLI URL/configuration selection. Capture uses the Link backend, is read-only, takes no session write lock, checks versions before and after acquisition, and is bounded to 512 requested names, 512 unique attempted metadata names (including failed requests and recursive dependencies, before alias normalization), plus two minutes. Missing requested Component/type metadata or budget/deadline truncation saves the partial catalog and returns `CATALOG_CAPTURE_INCOMPLETE` (exit 6); recursive dependency failures alone remain diagnostic. JSON errors retain the original result under `error.context.result`; text retains multiline result/counts. `requestedTypes` counts raw input names including duplicates; `acquisitionBudgetUsed` counts unique attempts including failures before alias normalization; `acquisitionBudgetLimit` is 512; `outputTypes` counts exported types and can exceed 512 from synthesized known scalars. Older snapshots without budget metadata report the string `"unknown"`.

`catalog types` is offline, rejects synthetic catalogs and catalogs with an `UnavailableReason`, and defaults to `.resoloop/catalog-types.d.ts` under the nearest `.resoloop.json` project root (or the current directory when there is no project). The default output location is resolved from the current directory, while `resoloop-jsx build` automatically reads only the `.resoloop/catalog-types.d.ts` of the project containing `.resoloop.json` (or of `--project-root`), so run `catalog types` inside that project or pass `--output`. Its paired artifacts use format 1 and generator 3; the declaration header binds catalog content hash, runtime filename, and runtime SHA-256. Comments beginning `resoloop-catalog-fallback` list fallbacks. The declarations augment `CatalogComponentRegistry`; a normal `tsc` setup must explicitly include them, for example with `/// <reference path="../.resoloop/catalog-types.d.ts" />` or a `tsconfig.json` `include` entry. JSX build automatically loads this conventional file when it exists. When both this generated file and `--catalog` are present in bundle mode, their content hashes must match or build fails with `APPLY_CATALOG_TYPES_HASH_MISMATCH`. For a literal `<Component>`, an owner with `Confirmed` and `MembersComplete` closes the accepted member names; otherwise the member-name set stays open. A member with confirmed value metadata can receive a specific TypeScript value type even when the owner is incomplete, while an unconfirmed member uses `JsonValue`.

Single values map to TypeScript `number`, nullable values to a union with `null`, tuples to arrays and xyzw/rgba objects, enums to names or numbers, and references outside the strict scalar field-reference set to `string | null`. Confirmed, non-nullable scalar field targets are strictly typed only for `boolean`, `int32`, `string`, and `single` as `FieldRef<T, R>`. Nullable and other reference forms keep their existing `string | null` or `UncheckedReference` fallback. Plain string references and legacy `ref.*` helpers remain compatible. Confirmed boolean, int32 and string scalar fields map to TypeScript `boolean`, `number` and `string`; other numeric types remain `JsonValue`. TypeScript checks these JSON types strictly. For example, `true` and `"true"`, or `42` and `"42"`, have different TypeScript types; C# runtime conversion accepts both forms for boolean and int32. It uses raw value text for string fields. Finite/range checks remain in C#.

A confirmed field value may keep its type when its owner is generic with incomplete closure; unknown members and value shapes are not inferred. Catalog format 2 and mapper version 3 are unchanged; the declaration generator is version 3. Mapper 2 catalogs fail with `APPLY_CATALOG_UNAVAILABLE`; after explicitly authorized live recapture, regenerate the declarations. Generator/header/hash mismatch fails with `APPLY_CATALOG_TYPES_HASH_MISMATCH`, and old-generator declarations must be regenerated even when the catalog hash matches. C# `ApplyCatalogValidator` remains authoritative: TypeScript acceptance does not replace its judgement, and `JsonValue` fallback alone does not imply `APPLY_CATALOG_UNAVAILABLE`.

Use the exact full Component type and member obtained through Reflection. Replace both placeholders below with observed names; this example assumes Reflection confirmed the member is a Single. The valid declaration is checked early; the commented typo and wrong value are examples of TypeScript diagnostics when the type is confirmed:

```tsx
<Component key="example" type="REFLECTION_VERIFIED_FULL_COMPONENT_TYPE" fields={{ REFLECTION_VERIFIED_SINGLE_MEMBER: 2 }} />
// <Component key="example" type="REFLECTION_VERIFIED_FULL_COMPONENT_TYPE" fields={{ REFLECTION_VERIFIED_SINLGE_MEMBER: 2 }} /> // unknown member when names are closed
// <Component key="example" type="REFLECTION_VERIFIED_FULL_COMPONENT_TYPE" fields={{ REFLECTION_VERIFIED_SINGLE_MEMBER: "bad" }} /> // wrong value type
```

For `--catalog` hash binding, use the existing request-bound bundle mode. `--catalog` remains bundle-only on the JSX build command:

```powershell
$requestId = [guid]::NewGuid().ToString("N")
resoloop-jsx build content/main.tsx --bundle --catalog catalog.json --build-id $requestId -o build/$requestId/bundle.json
resoloop validate build/$requestId/bundle.json --build-id $requestId --json
```

The C# Core check runs without Node or a Resonite connection. Catalogs must carry acquisition identity, provenance and an intact content hash; missing/unconfirmed evidence and unknown reference closure fail with `APPLY_CATALOG_UNAVAILABLE`. Proven incompatible references use `APPLY_REFERENCE_TYPE_MISMATCH`; invalid/non-finite/out-of-range Single values use `VALUE_CONVERSION_FAILED`, including nullable non-null values and tuple elements. Rounding is allowed; member-specific ranges are not guessed. Both catalog error codes retain validation exit 6 and the existing issue format. Success keeps `strict: false`; adding `--strict` also requests the existing live validation after catalog preflight and session version comparison against the same snapshot; synthetic catalogs are rejected before connection.

`test/fixtures/catalog-v11/catalog.synthetic.json` is a fixed **synthetic** original with identity and content hash. `oracle.handwritten.json` independently fixes expected member types, case inputs and diagnostic codes/paths; do not derive or regenerate its expectations from the catalog or generated Apply types. `npm run contract` runs these cases and request-bound bundle handoffs through the actual offline CLI alongside the unchanged existing fixtures. This does not establish real Component/runtime verification. [CatalogExport](../RLoop.CatalogExport/README.md) documents the lower-level developer export/import tool; legacy reflection caches without acquisition identity are not catalogs. Source locations are covered by the independent source-v11 oracle.
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

### Experimental Object API

`defineObject<P>()({ exports, body })` defines a reusable body and named public references. `Def.instance(id)` creates one placement handle, used as `<Def handle={handle} ...props />`; `handle` is reserved and cannot appear in user props. `ref.export(handle, name)` references only a declared export. `componentFields(type, catalog)` binds member checks and `FieldRef` metadata to an explicitly imported runtime table. Strict references support only `boolean`, `int32`, `string`, and `single`; values use TypeScript `number` for both numeric forms, while reference types keep `int32` and `single` distinct. Legacy `<Field>` remains loose `JsonValue`.

This offline example uses fictional `Fixture.Objects.Lamp` and `Fixture.Objects.Toggle` names and is not runtime evidence. Replace them only after Reflection confirms exact types and members.

<!-- object-example -->
```tsx
import { Slot, Component, defineObject, componentFields, ref } from "resoloop-jsx";
import { catalog } from "../.resoloop/catalog-fields.js";

const Lamp = componentFields("Fixture.Objects.Lamp", catalog);
export const ownership = { key: "object-fixture" };

const Light = defineObject<{ name: string; intensity: number }>()({
  exports: { enabled: Lamp.ref("light", "Enabled") },
  body: ({ name, intensity }) => (
    <Slot key="root" name={name}>
      <Component
        key="light"
        type="Fixture.Objects.Lamp"
        fields={{ Intensity: intensity }}
        initialFields={{ Enabled: true }}
      />
    </Slot>
  ),
});

const desk = Light.instance("desk-lamp");
const ceiling = Light.instance("ceiling-lamp");

export default (
  <Slot key="scene" name="Objects">
    <Light handle={desk} name="Desk" intensity={1.5} />
    <Light handle={ceiling} name="Ceiling" intensity={4} />
    <Component
      key="toggle"
      type="Fixture.Objects.Toggle"
      fields={{ Target: ref.export(desk, "enabled") }}
    />
  </Slot>
);
```

Object instance IDs must match `^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$` and be globally unique. Give every Slot and Component in an Object body an explicit key. Expanded keys use `instanceId::localKey`, with nested Scope segments appended. The `instanceId::` namespace is reserved. A raw reference cannot cross Object boundaries, even when its target has a public export; cross-boundary access uses only `ref.export`. Nested Objects are unsupported. Output remains schema 1 with single ownership; apply state remains version 3.

TypeScript must include or reference the generated declaration; JSX build loads the conventional project `.resoloop/catalog-types.d.ts`. Import `.resoloop/catalog-fields.js` explicitly from source. `catalog types` writes paired `.resoloop/catalog-types.d.ts` and `.resoloop/catalog-fields.ts` by default under the nearest `.resoloop.json` project, or the current directory when none exists. `--output X.d.ts` also writes `X.fields.ts`; another suffix emits declarations only with a warning. Headers use format 1 and generator 3, and bind catalog hash, runtime filename, and BOM-free UTF-8 runtime SHA-256 with CRLF normalized to LF. Catalog format 2 and mapper 3 remain unchanged. Generator-3 declaration-only legacy files can provide editor types, but `componentFields` needs its runtime table. An explicit import whose runtime module cannot be resolved produces TypeScript TS2307 (build exit 2); unavailable evidence is `APPLY_CATALOG_UNAVAILABLE` (exit 1); stale header/hash/runtime/companion data is `APPLY_CATALOG_TYPES_HASH_MISMATCH` (exit 1). `tsc` alone does not check artifact binding, freshness, identity, placement, or privacy. A normal build compares imported runtime data with its declaration but cannot establish freshness against source catalog JSON; only existing bundle mode with `--catalog` binds that input.

The TSX-only Object/Field build diagnostics use exit 1: `APPLY_OBJECT_INVALID`, `APPLY_OBJECT_DUPLICATE`, `APPLY_OBJECT_EXPORT_INVALID`, `APPLY_OBJECT_EXPORT_UNKNOWN`, `APPLY_OBJECT_TYPE_MISMATCH`, `APPLY_OBJECT_PRIVATE_REFERENCE`, and `APPLY_FIELD_TYPE_MISMATCH`. TypeScript diagnostics use exit 2. C# `validate` remains authoritative and returns existing exit 6 for `VALUE_CONVERSION_FAILED`, `APPLY_REFERENCE_TYPE_MISMATCH`, and `APPLY_CATALOG_UNAVAILABLE`.

Keep instance IDs and export names stable while consumers rely on them; stable names preserve consumer source when the target is updated inside the Object definition. Moving a Component to a different owner, changing its type/key, or replacing its target can change runtime reference IDs and may require content recreation. `initialFields` apply only on creation, so recreation can reapply them and runtime values may be lost. The API does not prove state or real-world ownership. Offline tests cover repeated and parallel builds, not ESM cache reclamation or watch mode. The API is experimental; raw internal references cannot cross an Object boundary; use only declared exports through `ref.export`.

For project-root resolution, an entry inside the explicit `projectRoot` uses that root for authoring paths and project catalog loading. An entry outside it keeps the legacy entry-directory root and relative `authoring.source` (for example, `../project/content/main.tsx`); importing the external project catalog can produce TypeScript `TS6059`. The Object API has not been verified against a live Resonite session, and Reflection has not confirmed a real Toggle component or its referenced member. The existing computed-literal `__proto__` handling remains narrow; it does not make other prototype-key forms valid.

### Experimental variables API

The `resoloop-jsx/variables` subpath exports `types.bool`, `variable(type, defaultValue)`,
`defineVariables({ scopeKey, variables })`, the `Variables` declaration component, and the
`drive(...)` and `bind(...)` markers. A declaration's `set.vars` is a read-only collection of
typed references. `scopeKey` and variable names must match
`^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$`; every Slot and Component needs an explicit stable `key`.

<!-- variables-example -->
```tsx
import { Slot, Component, componentFields } from "resoloop-jsx";
import { defineVariables, variable, types, drive, bind, Variables } from "resoloop-jsx/variables";
import { catalog } from "./.resoloop/catalog-fields.js";

const Light = componentFields("Fixture.Objects.Lamp", catalog);
const set = defineVariables({ scopeKey: "lamp", variables: { on: variable(types.bool, true) } });

export default (
  <Slot key="scene" name="Lamp">
    <Component key="light" type={Light.type}>
      <Light.Field name="Enabled" value={drive(set.vars.on)} />
    </Component>
    <Component key="toggle" type={Light.type}>
      <Light.Field name="Enabled" value={bind(set.vars.on)} />
    </Component>
    <Variables declaration={set} />
  </Slot>
);
```

This uses the fictional `Fixture.Objects.Lamp` type as an offline example, not as evidence
from Resonite. Generate a real catalog pair through Reflection and confirm the component and
member before using them. Only `bool` variables and confirmed catalog-bound bool Fields are
supported. Place the declaration and every driven Field in the same Dynamic Variable Space;
references cannot escape that Space or its descendant Slots. Use `drive` for a one-way driver
or `bind` for a two-way field binding. A bare variable reference is invalid.

Do not declare the same set or bind the same Field more than once, and do not supply an ordinary
`fields` or `initialFields` value for a bound Field. An authored Dynamic Variable Space between
the declaration and a target is rejected; duplicate variable names within one Space and generated
key or sibling-name collisions are also rejected. The declaration's owning Slot may itself hold
a target or contain it in a descendant Slot within that Space; targets outside that ownership
subtree cannot bind. The provider stores the declared default in its
`Value` field, so after a successful apply, applying again writes that definition value; this
describes apply behavior and is not a live-session guarantee. This API is experimental and
has not been verified against a live Resonite session.

A declaration expands to a Dynamic Variable Space with `SpaceName` equal to `scopeKey` and
`OnlyDirectBinding: false`. Its `DynamicVariables` child Slot contains the bool provider, whose
`VariableName` is an identity field. Each `drive` or `bind` adds one wire child Slot with a
Driver or DynamicField. The target Component records `driver-owned` or `runtime` in
`propertyModes`; the bound target Field is not written into `fields` or `initialFields`.
