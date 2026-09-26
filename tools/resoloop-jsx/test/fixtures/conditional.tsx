import { Slot } from "resoloop-jsx";

// Compile-time-constant conditions: falsy values and null are dropped by the
// evaluator, so only "Always" and "AMode" appear in the output.
const showExtra: boolean = false;
const mode: string = "a";

export default (
  <Slot key="root" name="Root">
    <Slot key="always" name="Always" />
    {showExtra && <Slot key="extra" name="Extra" />}
    {mode === "b" ? <Slot key="b-mode" name="BMode" /> : null}
    {mode === "a" ? <Slot key="a-mode" name="AMode" /> : null}
  </Slot>
);
