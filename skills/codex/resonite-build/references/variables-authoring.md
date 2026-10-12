# Variables authoring workflow (experimental)

Use this workflow to declare variables and bind them to component Fields in TSX. The Variables API is experimental and has not been verified against a live Resonite session.

The `resoloop-jsx/variables` subpath exports `types.bool`, `variable(type, defaultValue)`, `defineVariables({ scopeKey, variables })`, the `Variables` declaration component, and the `drive(...)` and `bind(...)` markers. A declaration's `set.vars` is a read-only collection of typed references. `scopeKey` and variable names must match `^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$`; every Slot and Component needs an explicit stable `key`.

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

`Fixture.Objects.Lamp` is fictional in this example; it is not evidence from a Resonite session. Generate a real catalog pair through Reflection and confirm the component and member before using them. Only `bool` variables and confirmed catalog-bound bool Fields are supported. A bare variable reference is invalid. Do not supply an ordinary `fields` or `initialFields` value for a bound Field. Declare the variables and targets in the same Dynamic Variable Space; references cannot escape that Space or its descendant Slots, and an authored Dynamic Variable Space between the declaration and a target is rejected. Use `drive` for a one-way driver or `bind` for a two-way field binding.

A declaration expands to a Dynamic Variable Space with `SpaceName` equal to `scopeKey` and `OnlyDirectBinding: false`. Its `DynamicVariables` child Slot contains the bool provider, whose `VariableName` is an identity field. Each `drive` or `bind` adds one wire child Slot with a Driver or DynamicField. The target Component records `driver-owned` or `runtime` in `propertyModes`; the bound target Field is not written into `fields` or `initialFields`.

Follow these steps in order:

1. Start with Reflection and a confirmed catalog. Confirm the component and member before using them; only confirmed catalog-bound bool Fields are supported.
2. Build successfully with the Variables API.
3. Validate the resulting IR offline.
4. Apply only to explicitly authorized targets.
5. Perform bounded post-change inspection.

The provider stores the declared default in its `Value` field, so after a successful apply, applying again writes that definition value; this describes apply behavior and is not a live-session guarantee. The API is experimental and has not been verified against a live Resonite session.
