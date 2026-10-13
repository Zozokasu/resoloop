import { Slot, Component } from "resoloop-jsx/draft";

export default (
  <Slot key="room" name="Room">
    <Component type="Probe" fields={{ Value: "root" }} />
    <Slot name="Lamp">
      <Component type="Probe" fields={{ Value: "first" }} />
    </Slot>
    <Slot name="Lamp">
      <Component type="Probe" fields={{ Value: "second" }} />
    </Slot>
  </Slot>
);
