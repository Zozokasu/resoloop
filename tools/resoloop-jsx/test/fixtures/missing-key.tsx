import { Slot } from "resoloop-jsx";

// `key` is optional in the types so that omitting it reaches the evaluator:
// without --draft the build fails with EXPLICIT_KEY_REQUIRED, with --draft a
// deterministic key is generated instead.
export default (
  <Slot key="root" name="Root">
    <Slot name="NoKey" />
  </Slot>
);
