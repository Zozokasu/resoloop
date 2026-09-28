import { Slot } from "resoloop-jsx";

// Same name under one parent must fail even with distinct keys.
export default (
  <Slot key="root" name="Root">
    <Slot key="first" name="SameName" />
    <Slot key="second" name="SameName" />
  </Slot>
);
