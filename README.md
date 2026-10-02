[English](README.md)｜[日本語](README_JA.md)

# resoloop

![resoloop_logo](./resource/resoloop_resonite_16_9.png)

resoloop is a CLI for controlling Resonite worlds from AI agents such as Codex and Claude Code.

Tell the AI what you want to create, and it will inspect the current world, describe the Slot and Component structure in files, and apply and verify the result through ResoniteLink. Because the work is stored as files, you can apply the same structure repeatedly and track changes with Git.

resoloop automatically adds `FrooxEngine.AI_GeneratedContent` to the root of the content it generates and records the running tool's name and version in `Source` (for example, `[resoloop 0.1.0-preview.9]`). The same tag is also added to portable and equippable roots within the declaration tree.

> [!NOTE]
> resoloop is currently in preview. ResoniteLink is also in Beta, so updates may change its behavior.

## Applying changes safely

Apply checks the planned values, types and connection again before each write. Observed changes or a possible writer referencing the target field stop that write with `APPLY_PRECONDITION_FAILED` (exit 6). Writer coverage outside the observation is `unknown`; this permits writing and does not prove that no writer exists. Each response is followed by one readback of the values sent. A mismatch stops with pending evidence and `APPLY_WRITE_UNVERIFIED` (exit 7), without a reference replay at the end.

Component `propertyModes` maps member names to `config`, `initial`, `runtime` or `driver-owned`. Omitted members retain `fields` configuration and creation-only `initialFields` behavior. Runtime and driver-owned members are never written by ordinary apply, including creation; the declaration does not prove a driver's ownership.

Schema v1 also supports optional Component `fieldAliases` and Slot `tag`; TSX supports `<Field>` children and `<Slot tag>`. Aliases resolve to canonical `$member` selectors, and omitted tags preserve the live value. See [declarative authoring details](docs/DECLARATIVE.md) for syntax, policy and diagnostics.

Keep the reported state file. State v3 stores confirmed bindings and pending operations; v1/v2 can be read, saves use v3, and older CLIs reject v3. Explicit server rejection clears the pending operation. A later apply can reconcile an accepted result with matching discovery identity and exact target/owner evidence: confirmed values commit, known mismatches clear and planning uses the current observation. Unknown creation IDs, unproven acceptance or identity remain pending; names, types and order are never used to recover a lost creation. Explicit `--url` connections have unknown identity and cannot automatically settle interrupted pending operations; a discovery-selected connection can supply matching identity evidence.

On failure, read `context.reason`, `stateFile`, `operationId`, confirmed results and completeness; inspect the exact Slot/Component IDs before deciding how to continue. After inspecting the world, clear only a chosen operation with:

```powershell
resoloop apply FILE --state STATE --discard-pending OPERATION_ID --yes
```

This is an offline state operation, not a world write. It preserves confirmed bindings. Discarded creation candidates are not adopted and may be duplicated by a later apply; discarded updates/deletions retain their confirmed correspondence and require inspection and a new plan.

ResoniteLink writers share an exclusive handle at `<LocalApplicationData>/ResoLoop/write-locks/<normalized-URL-hash>.lock`. Host case, any loopback address (localhost, 127.0.0.0/8 such as 127.0.0.2, ::1) and default ports normalize to the same key across projects; a different scheme, port, path or query is a different key, and whether different keys reach one world is unverified. Apply, asset imports within apply, direct Slot/Component edits and deletions, raster capture's temporary camera and `test --probe` participate. Reads and offline SVG capture take no lock; Flux deploy is outside this lock. `APPLY_SESSION_BUSY` (exit 7) means an active holder; `APPLY_STATE_BUSY` still means contention on one project state. Never remove or steal a lock held by an active writer. The next holder checks the last writer's state location: pending or unreadable state stops other projects with `APPLY_WRITE_UNVERIFIED`; a confirmed missing file or directory allows writing to continue. Access denial is not absence. Failure context includes `stateFile` and `lockFile`, including when the lock record is corrupt. Repair the state or resolve pending evidence in the original project; inspect exact targets before discarding a chosen pending operation. If the state is permanently lost, verify that no ResoLoop writes are running and inspect the live world before deleting the reported `lockFile`. Cache cleanup does not clear these locks or journals.

Prune requires `--prune --yes`. Prune and relocation-source deletion recheck stable key, exact ID and owner Slot immediately before deletion. Slot deletion requires complete child/component observation and rejects unmanaged descendants, including automatically added components without ownership evidence. Absence readback uses only exact `SLOT_NOT_FOUND` / `COMPONENT_NOT_FOUND`. Direct `slot delete` / `component remove` retain their existing exact-target/`--yes` protections and Root refusal. To replace a driver whose old owner prevents a new reference from sticking, first remove the old driver from the declaration, review diff and apply with `--prune --yes`; inspect its absence, then add the new driver and apply. One apply cannot perform that replacement through a failed readback.

These checks detect observed conflicts and coordinate this and later cooperating ResoLoop versions on the same PC and OS user. Operations remain `atomic:false`; there is no rollback or exclusion of older CLIs, other PCs/users, people, external tools or ProtoFlux. Changes outside observation or between checking and sending remain possible. Readback does not guarantee future value retention. See [failure recovery and deletion details](README-DETAILS.md#apply-の停止後は保留と実機を確認する).

## Installation

Requirements:

- Windows 10 or 11
- [`.NET 10 SDK`](https://dotnet.microsoft.com/download/dotnet/10.0)
- Resonite
- An AI coding agent such as Codex or Claude Code

Install resoloop in PowerShell:

~~~powershell
dotnet tool install --global ResoLoop --version 0.1.0-preview.14
resoloop --version
~~~

If resoloop is already installed, update it with the following command:

~~~powershell
dotnet tool update --global ResoLoop --version 0.1.0-preview.14
~~~

## Usage

### 1. Create a project

Create a dedicated project for each thing you want to build in Resonite.

~~~powershell
resoloop init MyResoniteProject
Set-Location MyResoniteProject
~~~

### 2. Configure ResoniteLink

1. Start Resonite and open the world you want to edit.
2. Open the `Settings` tab on the Dashboard's `Session` page.
3. Select `Enable ResoniteLink` in the lower-left corner.
4. Once `ResoniteLink running on port: ...` appears, ResoniteLink is ready.

The AI can then use resoloop to find the port and connect automatically.

You can also set the connection yourself using an environment variable, but this is optional.

For example, if the displayed port is `12449`:

~~~powershell
$env:RESONITE_LINK_URL="ws://localhost:12449"
resoloop doctor
~~~

The setup is complete when `ready` appears at the end.

For existing content, preserve its checkpoint and use `diff/plan/apply --state STATE --require-state`. TSX can export `ownership` independently of the root key; build metadata fixes the project base regardless of JSON output location. Rebuild after moving the project. See [authoring and migration details](README-DETAILS.md).

For TSX build handoffs, create a fresh request ID before each build: `resoloop-jsx build content/main.tsx --bundle --catalog catalog.json --build-id R -o build/R/bundle.json`. Only after exit 0, pass that same externally generated R to `resoloop validate|diff|plan|apply build/R/bundle.json --build-id R`. Each build needs a new output directory; failed builds publish no bundle. The CLI checks the embedded IR/map/catalog, used types and recorded source/catalog bytes before connecting and checks inputs again before the first write. Connected commands require a verified non-synthetic catalog matching session/client versions; offline validate can use explicitly synthetic fixtures. `APPLY_BUILD_BUNDLE_INVALID` is exit 6 with a reason; bundle/ordinary JSON argument mismatches use existing exit 2 errors. Workbench refuses bundles. Handwritten and directly generated JSON remain supported without Node or warnings.

The input guarantee covers TypeScript's source/import graph and catalog: dynamic import/require, Node builtins and external packages (except the `resoloop-jsx` runtime) stop publication. Environment variables, time and other implicit inputs cannot be detected and are outside the guarantee. Map positions come from original AST ranges, with `unknown` for untracked origins; request IDs/completion trust the build producer, and changes after the final input check remain outside this guarantee. See the [bundle format and workflow](tools/resoloop-jsx/README.md).

For source diagnosis, add `--diagnostics NEW_FILE.json` to `validate|diff|plan|apply`. This separate JSON has `diagnosticVersion: "1"` and structured key/member/IR path, known/unknown source, expected/observed evidence and completeness; a known null is distinct from unknown. Bundle map v1 stores original TS/TSX UTF-16 AST offsets and one-based, end-exclusive line/column ranges bound to source hashes. Fix TSX using that location, then create a fresh bundle/request; generated JSON lines are not an authoring location. Function props forwarded directly preserve caller expressions; Scope, fragments, map callbacks and selected conditional children retain original ranges. Spread/computed members and untracked value transfers have unknown primary locations with real expressions/calls as related evidence. Handwritten and legacy generated JSON also accept diagnostics, with unknown source. Legacy stdout/stderr JSON, context.issues and reports are unchanged. Diagnostics require a new file in an existing writable directory; writing failure is reported on stderr without changing validation/apply judgement or exit code. Catalog evidence never completes runtime verification.

### 3. Ask the AI to work on your project

Open the project you created in an AI agent. If you have continued using the same AI session since creating the project, reopen the session once so that the agent can discover the generated Skill.

Then describe what you want to build in Resonite using ordinary language. For example:

~~~text
Create a teleporter gun in Resonite. Make it an equippable item shaped like a gun. When fired, it should launch a projectile in an arc and teleport me to the point where the projectile lands.
~~~

## Blender modeling

When you ask for a complex model, resoloop may use Blender.

If Blender is installed on your PC, resoloop detects and uses it automatically.

You do not need to have Blender open.

## Efficient UIX authoring

UIX recipes provide reusable button, text-input, toggle, exclusive-choice, slider, shared-state, boolean binding and scroll-content structures. Choice/state bindings also compose tabs and open/closed panels. They leave shapes, colors, fonts, dimensions and feedback to the caller. Discover a recipe's parameters and connection points, then export it as a normal declaration prototype:

~~~powershell
resoloop uix recipe list --json
resoloop uix recipe describe button --json
resoloop uix recipe describe text-input --json
resoloop uix recipe export button --output content/recipes/button.json --json
resoloop diff content/main.json --brief --report artifacts/plan-01.json --json
resoloop apply content/main.json --brief --json
resoloop uix audit '$slot:panel' --state .resoloop/state/panel.json --brief --report artifacts/audit-01.json --json
~~~

For new content, declare `{"$recipe":"button","$with":{"key":"accept","rect":{}}}` directly in `children`; no include/export is needed. Generated keys use `uix-button--accept` as their prefix. Exported prototypes remain supported for pinned editable wiring; their existing keys do not change. See [Structural recipes](skills/codex/resonite-uix/references/recipes.md) for parameters and ports. Recipe export and reports require new filenames. `--brief` reduces displayed evidence, not validation: diff already performs offline and runtime checks, and apply repeats preflight against the current world. Standalone validate remains useful for offline authoring or diagnosis. Full reports preserve the existing JSON format.

Batch known managed fields with `resoloop observe '$member:state.Value' '$member:toggle.TargetValue' --state STATE --json`. This read-only command accepts up to 64 selectors and returns typed values and reference IDs, failing if any selected field is missing. It uses normal stable re-resolution after reconnect. Cross-Component values are sequential observations, not an atomic snapshot.

## Declaration and capture assistance

New declarations can start from an offline structural scaffold. Inspect only the schema section you need; examples come from the parser DTOs. Provider scaffolds contain no visual settings.

~~~powershell
resoloop manifest scaffold --key panel --output content/panel.json --json
resoloop schema describe camera --json
resoloop manifest scaffold --kind provider --key front-material --type '[FrooxEngine]FrooxEngine.UI_UnlitMaterial' --output content/front.node.json --json
resoloop validate content/panel.json --json
# After appending the provider node, designing and applying the panel:
resoloop capture content/panel.json --frame '$slot:canvas' --view front --output artifacts/front.jpg --json
resoloop capture content/panel.json --frame '$slot:canvas' --view rear --output artifacts/rear.jpg --json
~~~

`--frame` requires the exact Slot containing one live planar Canvas, and uses its collider plus ancestor transforms. It preserves content scale; overflowing children, curved geometry, mirrored scales and occlusion require an explicit camera. `validate`/`diff` return conservative identity warnings for same-type Components sharing a Slot; separate named providers avoid this ambiguity for new content. See [authoring assistance](docs/AUTHORING-ASSISTANCE.md) for contracts and verification.

## Further documentation

Batch required Reflection metadata with `type query --request FILE.json --json`; obtain a request example from `schema describe reflection --json`. Explicit `members` select output; `enums` optionally adds candidate values. Persistent definitions are trusted when engine/link versions and adapter/Core builds match, with no default expiry. Local port changes do not invalidate them. Check, diff, apply and primitive conversion share this cache. `type check --request FILE.json --brief --json` checks contracts; `type check --manifest FILE.json --brief --json` reuses strict declaration validation. `--refresh` re-fetches definitions; `--cache off` bypasses disk reads/writes; `--cache-dir DIR` overrides storage. Refresh after MOD/DLL changes. Instance IDs, values and reference targets are still observed live. `--profile` includes request and cache counts. See [Reflection caching and measurements](docs/REFLECTION-EFFICIENCY.md).

- [Detailed documentation](README-DETAILS.md) — commands, architecture, declaration format, Flux-SDK, and limitations
- [Quick start](docs/QUICKSTART.md) — detailed steps including applying, verifying, and using ProtoFlux
- [Declaration format](docs/DECLARATIVE.md) — specification for `content/*.json`
- [UIX agent efficiency](docs/AGENT-EFFICIENCY.md) — implementation and measured before/after token usage
- [Control recipe efficiency](docs/CONTROL-RECIPE-EFFICIENCY.md) — expanded controls and a separate before/after benchmark
- [Authoring assistance efficiency](docs/AUTHORING-EFFICIENCY.md) — two runs per version measuring declaration, material-reference and capture assistance
- [Batched observation and direct recipes](docs/OBSERVATION-RECIPE-EFFICIENCY.md) — follow-up implementation, fixed-operation replay and independent authoring comparison
- [Roadmap](docs/ROADMAP.md)

## License

[AGPL-3.0-or-later](LICENSE)

## Offline catalog validation

For a normal CLI workflow, first use Resonite Reflection to confirm the exact full runtime Component type names, then save them as a JSON array in `component-types.json`. With the current ResoniteLink URL already configured (or supplied explicitly), acquire a read-only catalog and generate TypeScript editor assistance:

```powershell
resoloop catalog capture --types component-types.json --output catalog.json --url ws://localhost:<current-port>
resoloop catalog types catalog.json
resoloop-jsx build content/main.tsx -o build/main.json
resoloop validate build/main.json --catalog catalog.json --json
```

`catalog capture` reuses the CLI's existing URL/configuration selection when `--url` is omitted. It takes no session write lock, writes no world state, checks versions before and after acquisition, and is bounded to 512 requested names, 512 unique metadata types including recursive dependencies, and two minutes. `catalog types` rejects synthetic or unavailable catalogs and writes `.resoloop/catalog-types.d.ts` in the nearest project with `.resoloop.json` (or the current directory if no project is found), unless `--output` is supplied. The default output location is resolved from the current directory, while `resoloop-jsx build` automatically reads only the `.resoloop/catalog-types.d.ts` of the project containing `.resoloop.json` (or of `--project-root`), so run `catalog types` inside that project or pass `--output`. The generated declarations augment `CatalogComponentRegistry`; include the declaration file in the TypeScript project's `include` or reference it from the project. `resoloop-jsx build` also loads this conventional project file automatically when present. This is editor and `tsc` assistance only: C# `ApplyCatalogValidator` remains authoritative for catalog validation. Types apply only to the `fields`/`initialFields` of `<Component type="...">`; `<Field>` children stay loosely typed (`JsonValue`) and their member names and values are checked only by the C# catalog validation, not by TypeScript. Capture and its result are not runtime verification.

`resoloop validate content/panel.json --catalog catalog.json --json` validates expanded IR without Resonite or Node. Supply a catalog explicitly; the CLI neither searches for one nor fetches metadata. It checks acquisition identity, mapper version and content hash, then rejects invalid/non-finite/out-of-range Single values with `VALUE_CONVERSION_FAILED` and proven reference incompatibility with `APPLY_REFERENCE_TYPE_MISMATCH`. Normal rounding and values outside an assumed member-specific range remain allowed. Missing/unconfirmed evidence, identity mismatch and insufficient reference closure fail with `APPLY_CATALOG_UNAVAILABLE`; issues retain type/member/path context and validation exit code 6. Unknown external IDs, assets and Slot/member reference types cannot be certified offline.

Catalog success leaves `strict: false`. With both `--catalog` and `--strict`, catalog preflight runs first, then the existing live validation checks the selected session's versions; it requires an authorized live connection. The commands without `--catalog` retain their existing behavior. Catalog checks do not replace bounded observation, current-ID verification or post-change inspection.

The developer tool also supports lower-level snapshot export/import: `dotnet run --project tools/RLoop.CatalogExport --no-build -- export SNAPSHOT.json CATALOG.json` or `... -- import CATALOG.json OUTPUT.json`. Snapshots preserve recursive SDK definitions and identity (Resonite/server Link/client package/mapper versions and acquisition time). Catalogs include a content hash and `live`, identity-matched `version-cache`, or refused `unverified` provenance; a hash proves integrity, not runtime acquisition. Legacy reflection caches without acquisition identity are refused. The V11 fixtures are **synthetic**, with a fixed original, identity/hash and handwritten diagnostic table. See [catalog tool](tools/RLoop.CatalogExport/README.md).
