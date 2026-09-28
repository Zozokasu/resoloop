import { Slot } from "resoloop-jsx";

// Deliberately wrong prop: position must be a 3-tuple of numbers.
export default (
  <Slot key="root" name="Root">
    <Slot key="bad" name="Bad" position={"nope"} />
  </Slot>
);
