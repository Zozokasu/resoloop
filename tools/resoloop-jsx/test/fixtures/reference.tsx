import { Slot, Component, ref } from "resoloop-jsx";

// Field values built with the ref helpers must be emitted verbatim as
// $slot: / $component: / $member: / $slot-member: selector strings, and every
// referenced key must be declared somewhere in the same document.
export default (
  <Slot key="root" name="Root">
    <Slot key="target" name="Target" />
    <Slot key="holder" name="Holder">
      <Component
        key="driver"
        type="FrooxEngine.ValueCopy<float>"
        fields={{
          Source: ref.slotMember("target", "Rotation"),
          Target: ref.member("spinner", "_speed"),
        }}
      />
      <Component key="spinner" type="FrooxEngine.Spinner" />
      <Component
        key="grab"
        type="FrooxEngine.Grabbable"
        fields={{
          TipReference: ref.slot("target"),
          Other: ref.component("driver"),
        }}
      />
    </Slot>
  </Slot>
);
