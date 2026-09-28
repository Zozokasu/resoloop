import { Slot } from "resoloop-jsx";

// The normal entry requires `key` at the type level: omitting it must fail
// TypeScript diagnostics (exit code 2) before the file is ever evaluated.
export default (
  <Slot key="root" name="Root">
    <Slot name="NoKey" />
  </Slot>
);
