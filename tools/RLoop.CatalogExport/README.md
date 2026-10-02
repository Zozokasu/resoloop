# RLoop.CatalogExport (developer tool)

This tool exports an identified SDK metadata snapshot to an SDK-independent Core catalog, or imports and checks an existing catalog. It does not connect unless the explicit `capture --live` or `probe --live` mode is selected. No new NuGet/npm dependencies are required. Build the solution with `--no-restore` after packages have been restored outside the sandbox.

```powershell
dotnet run --project tools/RLoop.CatalogExport --no-build -- export SNAPSHOT.json CATALOG.json
dotnet run --project tools/RLoop.CatalogExport --no-build -- import CATALOG.json OUTPUT.json
resoloop validate FILE.json --catalog CATALOG.json --json
```

Snapshots store original SDK Component/type/SyncObject definitions, enum values, confirmed aliases, acquisition identity, evidence identity, provenance, an explicit synthetic flag and a content hash. The mapper retains nested list/array/dictionary definitions and SyncObject members. SDK arrays provide a value type rather than an element member definition. New catalog format and mapper version are `2`; client package pin is `0.13.1`. Format 2 adds hash-bound `content.acquisitionFailures` (`type`, `request`, `reason`). Format 1 without that field remains readable only with the current mapper identity, preserving the fixed synthetic fixture content/hashes; all mapper-1 catalogs are refused with `APPLY_CATALOG_UNAVAILABLE`. The catalog contains full type identities, member scope/completeness, nullable/tuple representations and base/interface evidence. Only exact SDK wire aliases and acquisition-confirmed aliases are recognized; arbitrary short type names and generic arguments are never inferred. Unknown metadata remains absent/unconfirmed.

Catalog identity includes engine/server Link/client package/mapper versions and acquisition time. Import compares the saved identity to the acquisition identity and checks SHA-256 canonical content. Object keys are sorted ordinally, arrays retain order, UTF-8 uses System.Text.Json escaping. Snapshot hashes use the adapter's SDK serializer. Sources are `live`, identity-matched `version-cache`, or `unverified` (refused). A content hash detects modification; it is not proof that runtime acquisition happened. Legacy cache entries with only key/observedAt/value are refused; recorded historical versions must never be supplied as acquisition evidence. This tool does not import the existing reflection cache automatically.

The following read-only capture command requires **separate explicit live authorization**. Choose the exact current URL and put the exact fully qualified Component names in `FULL_NAMES.json` (a JSON array). The tool never searches or automatically selects a world. It bounds acquisition to 512 distinct requested type names and two minutes, follows observed base/interfaces and recursively collects member/SyncObject metadata, then checks session versions again. Component and SyncObject types are also independently acquired with GetTypeDefinition; request kinds have separate deduplication. The independent definition supplies inheritance, and the flattened definition supplies members. Conflicting definitions, missing/failed independent acquisition, and a missing base despite a base-exists marker never certify closure completeness. A non-interface without an observed base (other than the exact System.Object root) also cannot certify completeness. Confirmed exact inheritance paths may still prove references even when negative closure is incomplete; generic negative restrictions are unchanged.

Failed responses and per-type exceptions are recorded in snapshot and catalog `content.acquisitionFailures` with the requested name, request kind (`component`, `type`, `syncObject`, `enum`), and server error/exception. Acquisition continues while connected. Unattempted requests at the 512-type or time boundary record `type-limit:` or `time-limit:` reasons. Connection failures and caller cancellation abort. If the two-minute deadline prevents the final session check, the snapshot is still saved with a `session` failure and `source: unverified`; export refuses it until a bounded fresh capture succeeds. Do not relabel historical mapper identities or source to promote an old snapshot into new evidence.

```powershell
dotnet run --project tools/RLoop.CatalogExport --no-build -- capture --live --url ws://localhost:CURRENT_PORT --types FULL_NAMES.json SNAPSHOT.json
dotnet run --project tools/RLoop.CatalogExport --no-build -- export SNAPSHOT.json CATALOG.json
```

For offline V11, `tools/resoloop-jsx/test/fixtures/catalog-v11/` contains a fixed **synthetic** original catalog and a separate handwritten table. Synthetic source claims simulate acquisition for tests and are never runtime evidence. Do not regenerate expectations from the catalog or the Apply generator. S2-4 may use `ApplyDocumentValidator.ValidateAsync(..., catalog: ...)`, `ApplyCatalogValidator.Validate`, `ApplyCatalog.ContentHash`, and `ApplyCatalogValidator.UsedTypes`; delivery/maps/TSX source diagnostics are not implemented here.

## Read-only SDK probe

This tool bypasses the adapter mapping and retains SDK response objects. It does
not add commands to the product CLI or change `IResoniteClient`.

```powershell
dotnet build ResoLoop.slnx --no-restore
dotnet run --project tools/RLoop.CatalogExport --no-build -- sdk-contract TestResults/S3-0/sdk-contract.json
dotnet run --project tools/RLoop.CatalogExport --no-build -- probe --live --url ws://localhost:PORT --slot Root --depth 0 TestResults/S3-0/root.json
```

`sdk-contract` is offline: it inventories the loaded pinned SDK's exported types,
public properties/fields, attributes and declared public methods, together with
the assembly hash and serializer settings. It makes no connection.

`probe` requires `--live`, an explicit `ws`/`wss` URL, an explicit Slot ID and an
output file. It performs two sequential connections, disposing the first before
opening the second. Each pass reads session data before and after acquisition.
There is no URL discovery, automatic retry, method invocation, or write request
in the probe transport. Its only SDK requests are `GetSessionData`,
`GetSlotData(GetSlot)` and `GetComponentData(GetComponent)`.

Depth defaults to 0 and must be 0..8. Slots are fetched one at a time at SDK depth
0 with component data disabled, then each component is fetched separately. Child
references remain in the SDK response; only children within the requested depth
are followed. Hard limits are 64 Slot requests and 512 Component requests per
connection (128/1024 total) and two minutes for the entire acquisition. These bound
requests and visited objects, not the number of references in a single SDK reply
or its byte size. Output serialization/file writing is outside the request budget.

SDK JSON uses `LinkInterface.SerializationOptions` (cloned for indentation),
including its field handling and polymorphic member discriminators. The envelope
uses PascalCase names; SDK response names retain their wire spelling. Read:

- `Connections[0/1].SessionBefore/SessionAfter`: `uniqueSessionId`,
  `resoniteVersion`, `resoniteLinkVersion`, `success`, `errorInfo`.
- `Connections[*].Slots[*].Response`: `depth`, `data`, `success`, `errorInfo`;
  `data` preserves Slot field wrappers, child/component reference information.
- `Connections[*].Components[*].Response.data.members`: member `id`, `$type`,
  values, nested members/elements, and reference `targetId`/`targetType`.
- `Connections[*].Diagnostics`: limits, exceptions, unsuccessful or incomplete
  responses. Partial evidence is saved and the command returns nonzero. If the
  deadline prevents connection 2, its entry explicitly says it was not attempted.

`CompleteWithinRequestedDepth` means that the requested bounded acquisition
finished without these diagnostics. It does **not** prove complete world
observation, an atomic snapshot, no concurrent writer, or stable session identity.
SDK deserialization cannot preserve unknown wire properties that the pinned SDK
does not model; this is an SDK-object probe, not a raw WebSocket recorder.

Re-observe IDs after a world/session restart. Confirm the target URL and Slot from
current read-only observations before using a saved ID. Live execution requires
authorization for that task. This charter's offline run did not execute a probe
against Resonite. Restart/reopen comparisons require the user's participation;
specified-ID creation/collision/reconnect tests require separate write approval.
