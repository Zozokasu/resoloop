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
resoloop-jsx build <entry.tsx> -o <out.json> [--draft]
# or without installing:
node dist/src/cli.js build <entry.tsx> -o <out.json> [--draft]
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

Every `<Slot>` and `<Component>` requires an explicit stable `key` prop.
`--draft` relaxes this: a deterministic key of the form
`<parentKey>/<slug(name-or-type)>#<sameKindSiblingIndex>` is generated and one
warning per generated key is printed to stderr.

`ref.slot(key)` / `ref.component(key)` / `ref.member(key, member)` /
`ref.slotMember(key, member)` produce the `$slot:` / `$component:` /
`$member:` / `$slot-member:` selector strings.

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
`components`, and `children`. `ApplySlotSpec` fields supported as props:
`name` (required), `key`, `parent` (root only), `position`, `rotation`,
`scale`, `managedFields`, `preserveWorldTransform`, `migrateFrom`,
`relocationTransform`, `runtimeRelocatable`. `ApplyComponentSpec` fields:
`type` (required), `key`, `fields`, `migrateFrom`, `initialFields`,
`identityFields`. `assets`, `cameras`, `tests`, `include`, `prototypes`,
`parameters`, and `variables` are out of scope; because `assets` declarations
are not yet supported, no `ref.asset` helper is exposed.
