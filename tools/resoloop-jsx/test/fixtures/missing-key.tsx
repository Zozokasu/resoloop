import { Slot } from "resoloop-jsx";
import type { SlotProps } from "resoloop-jsx";

// `key` is a required prop in SlotProps, so a compile-time omission is a type
// error; this fixture deliberately escapes the type system to exercise the
// evaluator's EXPLICIT_KEY_REQUIRED path (and --draft key generation).
const keyless = { name: "NoKey" } as unknown as SlotProps;

export default (
  <Slot key="root" name="Root">
    <Slot {...keyless} />
  </Slot>
);
