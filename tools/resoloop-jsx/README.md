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

## Usage

```sh
resoloop-jsx build <entry.tsx> -o <out.json> [--draft] [--project-root PATH]
# or without installing:
node dist/src/cli.js build <entry.tsx> -o <out.json> [--draft] [--project-root PATH]
```

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

## Entry points: normal vs draft

- `import { Slot, Component } from "resoloop-jsx"` — `key` is required in the
  prop types; omitting it fails TypeScript diagnostics (exit code 2).
- `import { Slot, Component } from "resoloop-jsx/draft"` — the same runtime
  functions, but `key` is optional in the types. Intended for prototyping a
  tree. Without `--draft` a build still fails at evaluation with
  `EXPLICIT_KEY_REQUIRED` (exit code 1); with `--draft`, a deterministic key
  of the form `<parentKey>/<slug(name-or-type)>#<sameKindSiblingIndex>` is
  generated per missing key and one warning each is printed to stderr
  (exit code 0).

Everything other than `Slot`/`Component` prop typing (`ref`, `Fragment`,
`BuildError`, `evaluate`, and all shared types) is identical from either
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
`components`, `children`, and build `authoring` metadata. `ApplySlotSpec` fields supported as props:
`name` (required), `key`, `parent` (root only), `position`, `rotation`,
`scale`, `managedFields`, `preserveWorldTransform`, `migrateFrom`,
`relocationTransform`, `runtimeRelocatable`. `ApplyComponentSpec` fields:
`type` (required), `key`, `fields`, `migrateFrom`, `initialFields`,
`identityFields`. `assets`, `cameras`, `tests`, `include`, `prototypes`,
`parameters`, and `variables` are out of scope; because `assets` declarations
are not yet supported, no `ref.asset` helper is exposed.
