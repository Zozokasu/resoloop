# Reflection efficiency

Implemented and measured on 2026-09-25 against Resonite `2026.9.18.82`, ResoniteLink `0.13.1.0`, `ws://localhost:18831`. Baseline: commit `34a0f32`, published before these changes. Each invocation starts a separate Release CLI process.

## Current implementation

- The adapter now shares successful type/enum definitions between `DescribeTypeAsync` and value conversion in strict validation, diff, apply and primitive writes. Nested conversions use the same resolver. All in-memory definitions are cleared on connection initialization; failures are not cached.
- Successful type inspection previously evaluated `Suggestions(...)` eagerly, triggering a full component-category search even when no error occurred. Suggestions now run only on failure. This is a substantial part of the inspection speedup, independently of caching.
- `type query` batches explicit member/enum requirements into one connection and projects only selected metadata. Reflection requests remain sequential: the pinned SDK does not expose Reflection through its data-model operation batch, and its WebSocket send path is not a safe parallel-send contract.
- `type check --request` returns a contract verdict using trusted metadata (live or version-matched). `--brief` keeps the verdict/counts/differences, including failure output; `--report NEW_FILE.json` retains full evidence. `type check --manifest` reuses `ApplyDocumentValidator`, including recipe expansion and value conversion. It does not duplicate validation rules or inspect current world values.
- All adapter Reflection consumers share persistent version-matched definitions, including type query/describe/check, strict validation, diff/apply and primitive conversion. SDK definitions and their serialization stay entirely in the adapter.

Core contains requests/results and a metadata-client interface; disk storage and SDK models stay in the adapter. No visual properties or style choices are imposed by this feature.

## Usage

Use `resoloop schema describe reflection --json` for a small request example. Save its `example` object as a request, not the surrounding CLI envelope. For instance:

```json
{
  "types": [
    { "type": "[FrooxEngine]FrooxEngine.UIX.Canvas", "members": ["Size"] },
    {
      "type": "[FrooxEngine]FrooxEngine.UI_UnlitMaterial",
      "members": ["Sidedness", "ZWrite", "ZTest"],
      "enums": ["Sidedness", "ZWrite", "ZTest"],
      "expect": { "Sidedness": { "kind": "field", "enumValues": { "Front": 1 } } }
    }
  ]
}
```

These names/values were verified through runtime Reflection for the measured runtime; the check detects a changed contract.

```powershell
resoloop type query --request reflection.json --profile --json
resoloop type query --request reflection.json --refresh --json
resoloop type query --request reflection.json --cache off --json
resoloop type check --request reflection.json --brief --json
resoloop type check --manifest content/main.json --brief --profile --json
```

Optional `expect` supports exact `kind`, `valueType`, `targetType`, and an `enumValues` required name/value subset. Additional runtime enum values are allowed. `enums` and `expect` must refer to selected members. Limits: 1 MiB request, 1..64 selections, 1..64 members per selection, 256 members total. Unknown JSON properties are rejected. Missing members/contracts produce exit code 6 and structured differences. Transport failures remain errors.

Use search/describe when names are unknown. Query does not infer all recipe requirements automatically; manifest checking is the existing strict validator. Diff/apply already perform Reflection preflight using the same trusted cache, so adding a routine check immediately before them wastes work.

## Persistent-cache semantics

Default location: `<cwd>/.resoloop/cache/reflection`, ignored by Git. All connected commands accept `--cache-dir DIR`, `--cache auto|off|refresh`, and `--refresh` (an alias for refresh). Defaults apply to query, describe, check, strict validation, diff, apply and primitive writes.

- Definitions are trusted when Resonite and ResoniteLink versions, cache schema and adapter/Core build identities match. Loopback addresses share one scope so a local restart's port change does not discard metadata. Remote endpoints remain separately scoped. Connection/world IDs are excluded. Missing version information disables disk use.
- There is **no default time limit**. An explicitly supplied Core API `MaxAge` can impose one. Future timestamps, wrong keys, entries over 8 MiB, malformed JSON and invalid SDK definition shapes fall back to live reads. Refresh bypasses disk reads and replaces requested entries; off neither reads nor writes disk. Both retain same-connection in-memory reuse.
- Entries contain Component/type/member/enum definitions only: no instance IDs or field values. Component definitions preserve nested SDK field/list/dictionary/reference/SyncObject shapes needed for value conversion. Writes use a unique temporary file and atomic replacement; cache I/O failures do not prevent live queries.
- Query/check results have `verified: true` for live **or version-matched** definitions. `source: version-cache` means some metadata came from disk; `source: live` means all was read in this connection. `observedAt` is the oldest underlying observation time. `compatible` is a boolean verdict, including disk-backed checks. `--profile` includes `reflectionCache` hit/miss/write-failure counts for apply as well as read workflows.
- A cached contract mismatch/missing member causes one read-only refresh attempt. A cached definition that cannot parse a requested member/value triggers one live conversion retry **before mutation**. A rejected add/update invalidates consulted disk definitions and returns the error; it never replays the world write automatically. Inspect partial state and re-plan before retrying writes.
- This policy assumes the same runtime versions imply the same type environment. MOD/DLL changes under unchanged versions require `--refresh` (or off for diagnostics). The protocol does not provide a complete loaded-assembly fingerprint. Instance IDs, values, reference targets and ownership checks still use current world observations; type caching does not replace them.

General SyncObject-definition requests are not cached by this change. No resident CLI daemon is introduced.

## Original measurements (before version-trust policy)

Three repetitions per condition; baseline/optimized order alternated each round. Tables use median end-to-end process time, including startup and connection. The same running world was used; no deliberate application restart occurred during measurements. `--profile` covers service requests, not process startup/connection. The small sample is a local comparison, not a general latency guarantee.

### Authoring inspection

Required information: Canvas.Size and the Sidedness/ZWrite/ZTest enum candidates on UI_UnlitMaterial. Baseline uses four `type describe` invocations (Canvas plus three `--member` requests). The new query selects the same required information in one invocation. The baseline additionally returns unrequested metadata.

| Condition | CLI calls | Median wall time | Requests in query profile | Median stdout bytes |
|---|---:|---:|---:|---:|
| Baseline separate describes | 4 | 16,855 ms | unavailable | 4,625 |
| Batched, cache off | 1 | 911 ms | 9 | 1,831 |
| Batched, cold auto cache | 1 | 932 ms | 9 | 1,841 |
| Batched, warm disk cache | 1 | 833 ms | 1 | 1,632 |
| Batched, refresh | 1 | 922 ms | 9 | 1,831 |
| Live brief contract check | 1 | 912 ms | 9 | 485 |

The uncached batch is **94.6% faster** than the old workflow and displays **60.4% fewer bytes**. This combines removal of the erroneous category traversal, fewer process/connection round trips, and selected output; it is not a cache-only result. Warm disk reuse saves another **10.6% versus cold auto**, or **8.6% versus cache off**. It avoids all eight metadata requests; one session-version request remains. Each warm measurement used a new process and asserted five disk hits and `verified: false`.

Wall samples in milliseconds:

- Baseline four-call totals: 17,202.71 / 16,815.34 / 16,855.35.
- Cache off: 924.61 / 911.03 / 906.32.
- Cold auto: 932.06 / 943.59 / 913.38.
- Warm auto: 816.19 / 859.44 / 832.97.

### Live apply preflight

One isolated `ResoLoop_Test_reflection-0bddfa14a2b4` root contained eight separately named provider Slots, each with one UI_UnlitMaterial and three reflected enum fields. After reviewing diff and creating it, baseline/optimized reapplies used the same manifest/state and current-session observations. All six reapplies converged with **zero mutations**.

| Condition | Median wall time | Total profiled requests | Type requests | Enum requests | Mutations |
|---|---:|---:|---:|---:|---:|
| Baseline | 1,917 ms | 52 | 24 | 24 | 0 |
| Shared connection cache | 1,227 ms | 10 | 3 | 3 | 0 |

Wall time fell **36.0%**, total profiled requests **80.8%**, and type+enum requests **87.5%**. This measurement uses live in-memory caching, with no advisory disk reuse. Wall samples: baseline 1,917.07 / 1,898.58 / 1,957.20; optimized 1,290.33 / 1,227.24 / 1,200.97 ms.

The manifest check passed, while an invalid Sidedness name failed with `ENUM_VALUE_INVALID` and exit 6. The exact experimental root ID/name/parent were checked again before `slot delete --yes`; its absence from Root was verified afterwards.

Raw local evidence (ignored artifacts): `artifacts/reflection-efficiency/measure.py`, `request.json`, frozen `baseline-cli/`, measured `optimized-cli/`, `run-20260925-160254/` (queries), and `run-20260925-160319/` (apply/cleanup). Each run records command arguments, wall time, exit code, stdout byte count, full output and stderr. Later presentation-only changes compact failed check output; they do not alter the measured success paths.

This round did **not** measure agent token usage or account quota. Bytes, request counts and latency must not be treated as token/quota reduction rates. Remaining warm-query time includes .NET startup, connection, JSON and disk work; a daemon should be considered only if that residual cost matters in actual workflows.

## Original verification

Offline tests cover bounded/strict request parsing, selection, reference targets, enum contracts, de-duplication, cache provenance, expiry/version/endpoint/unknown-version changes, future timestamps, corruption, write failures, forced refresh, live checks bypassing disk, and nested enum conversion. The opt-in read-only `ReflectionCacheIntegrationTests` confirms repeated inspection/conversion uses only one type request and one enum request, with no category search. README and build/inspect/UIX workflows were updated; all three skills pass `quick_validate.py`.

Final checks: `dotnet build ResoLoop.slnx --no-restore` passed with zero warnings/errors; `dotnet test ResoLoop.slnx --no-build` passed 297 offline tests (18 opt-in integration methods returned without live execution). The targeted integration test was separately enabled and passed against port 18831. Live CLI checks also confirmed that brief mismatch output retains differences while the report preserves full metadata, check rejects cache options, and invalid cache modes cannot be hidden by `--refresh`. `git diff --check` passed.

## Version-trust follow-up

The current implementation supersedes the original advisory-only policy above. At follow-up, port 18831 was unavailable; discovery found one session, `orange World`, at `ws://localhost:21237/`. The read-only three-enum/two-component check produced:

| Mode | Metadata disk hits | Profiled requests | Verdict |
|---|---:|---:|---|
| refresh | 0 | 9 | verified and compatible, live |
| auto, separate CLI process | 5 | 1 | verified and compatible, version-cache |
| off | 0 | 8 | verified and compatible, live |

Warm auto still contacts the current runtime once for version identification. Off does not need that metadata-cache lookup. These counts are functional verification; no new latency or token-reduction claim is derived from them.

Additional offline coverage verifies a year-old entry remains usable across connection/loopback-port changes, link-version changes invalidate it, off never deletes/writes entries, malformed SDK metadata falls back, and nested SDK metadata survives serialization and conversion. The opt-in integration suite verifies fresh/warm/refresh/off behavior across new adapter connections and repairs a deliberately stale cached enum before validating it, with no world mutation.

An additional sandbox apply used eight separately named material providers under `ResoLoop_Test_version-cache-623bbe7f75`. Diff populated the cache; a separate apply process reused five entries and successfully created the providers (plus the generated-content marker). Observed Sidedness/ZWrite/ZTest values were Front/On/LessOrEqual. A separate reapply reused five disk entries, issued **zero metadata requests**, and converged with zero mutations. Its two remaining requests were session identification and live Slot observation. The exact root ID/name/parent were checked before deletion, and absence was verified afterwards. Evidence: `artifacts/reflection-efficiency/version-trust-20260925-170026/`.

Follow-up verification: solution build passed with zero warnings/errors; 300 offline tests passed. Both opt-in Reflection integration tests passed against port 21237; other integration tests were not enabled. The three affected skills pass their validator. No deliberate Resonite restart was performed during this follow-up: cross-connection behavior is tested live, while changed connection IDs, ports and entry age are covered offline.
