import { Slot, Component, Scope, Field, ref } from "resoloop-jsx";
export default <Slot key="root" name="Aliases" parent="Root" tag="alias-oracle">
  <Component key="wire" type="Test.Source"><Field name="Target" value={ref.field("left::enabled")} /></Component>
  <Scope instanceKey="left">
    <Slot key="body" name="Left">
      <Component key="state" type="Test.Target" identityFields={["Seed"]}>
        <><Field key="enabled" name="Enabled" value={false} /><Field key="seed" name="Seed" mode="initial" value={17} /></>
        <Field key="clock" name="Clock" mode="runtime" />
        <Field key="driven" name="Driven" mode="driver-owned" />
      </Component>
      <Component key="local-wire" type="Test.Source"><Field name="Target" value={ref.field("enabled")} /></Component>
    </Slot>
  </Scope>
</Slot>;
