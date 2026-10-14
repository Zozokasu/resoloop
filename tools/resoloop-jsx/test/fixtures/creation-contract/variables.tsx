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
