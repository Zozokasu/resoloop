import { Slot, Component, Scope, ref } from "resoloop-jsx";
import type { JsxNode } from "resoloop-jsx";

function Part({ name }: { name: string }): JsxNode {
  return <Slot key="body" name={name}>
    <Component key="state" type="Test.Target" fields={{ Enabled: true }} />
    <Component key="wire" type="Test.Source" fields={{
      Target: ref.component("state"), Member: ref.member("state", "Enabled"),
      Slot: ref.slot("body"), Rotation: ref.slotMember("body", "Rotation"),
    }} />
  </Slot>;
}

export default <Slot key="root" name="Scoped" parent="Root">
  <Scope instanceKey="left"><Part name="Left" /></Scope>
  <Scope instanceKey="right"><Part name="Right" /></Scope>
  <Component key="outside" type="Test.Source" fields={{ Target: ref.component(ref.key("left", "state")) }} />
</Slot>;
