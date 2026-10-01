# RLoop.CatalogExport (developer tool)

This tool exports an identified SDK metadata snapshot to an SDK-independent Core catalog, or imports and checks an existing catalog. It does not connect unless the explicit `capture --live` mode is selected. No new NuGet/npm dependencies are required. Restore from the existing local package cache, then build the solution.

```powershell
dotnet run --project tools/RLoop.CatalogExport --no-build -- export SNAPSHOT.json CATALOG.json
dotnet run --project tools/RLoop.CatalogExport --no-build -- import CATALOG.json OUTPUT.json
resoloop validate FILE.json --catalog CATALOG.json --json
```

Snapshots store original SDK Component/type/SyncObject definitions, enum values, confirmed aliases, acquisition identity, evidence identity, provenance, an explicit synthetic flag and a content hash. The mapper retains nested list/array/dictionary definitions and SyncObject members. SDK arrays provide a value type rather than an element member definition. Catalog format and mapper version are `1`; client package pin is `0.13.1`. The catalog contains full type identities, member scope/completeness, nullable/tuple representations and base/interface evidence. Only exact SDK wire aliases and acquisition-confirmed aliases are recognized; arbitrary short type names and generic arguments are never inferred. Unknown metadata remains absent/unconfirmed.

Catalog identity includes engine/server Link/client package/mapper versions and acquisition time. Import compares the saved identity to the acquisition identity and checks SHA-256 canonical content. Object keys are sorted ordinally, arrays retain order, UTF-8 uses System.Text.Json escaping. Snapshot hashes use the adapter's SDK serializer. Sources are `live`, identity-matched `version-cache`, or `unverified` (refused). A content hash detects modification; it is not proof that runtime acquisition happened. Legacy cache entries with only key/observedAt/value are refused; recorded historical versions must never be supplied as acquisition evidence. This tool does not import the existing reflection cache automatically.

The following read-only capture command requires **separate explicit live authorization** and was not executed for S2-3c. Choose the exact current URL and put the exact fully qualified Component names in `FULL_NAMES.json` (a JSON array). The tool never searches or automatically selects a world. It bounds acquisition to 512 type entries and two minutes, follows observed base/interfaces and recursively collects member/SyncObject metadata, then checks session versions again. Failed reads remain absent so subsequent validation fails when that evidence is needed.

```powershell
dotnet run --project tools/RLoop.CatalogExport --no-build -- capture --live --url ws://localhost:CURRENT_PORT --types FULL_NAMES.json SNAPSHOT.json
dotnet run --project tools/RLoop.CatalogExport --no-build -- export SNAPSHOT.json CATALOG.json
```

For offline V11, `tools/resoloop-jsx/test/fixtures/catalog-v11/` contains a fixed **synthetic** original catalog and a separate handwritten table. Synthetic source claims simulate acquisition for tests and are never runtime evidence. Do not regenerate expectations from the catalog or the Apply generator. S2-4 may use `ApplyDocumentValidator.ValidateAsync(..., catalog: ...)`, `ApplyCatalogValidator.Validate`, `ApplyCatalog.ContentHash`, and `ApplyCatalogValidator.UsedTypes`; delivery/maps/TSX source diagnostics are not implemented here.
