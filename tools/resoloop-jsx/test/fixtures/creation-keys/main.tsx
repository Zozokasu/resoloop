import { Slot, Component } from "resoloop-jsx/draft";

export const ownership = { key: "room" };
export default <Slot name="Room">
  <Component type="Probe" />
  <Slot name="Lamp"><Component type="Probe" /></Slot>
  <Slot name="Lamp"><Component key="right" type="Probe" /></Slot>
</Slot>;
