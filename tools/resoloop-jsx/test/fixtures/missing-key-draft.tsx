import { Slot } from "resoloop-jsx/draft";

// The draft entry makes `key` optional in the types so the omission reaches
// the evaluator: without --draft the build fails with EXPLICIT_KEY_REQUIRED,
// with --draft a deterministic key is generated instead.
export default (
  <Slot key="root" name="Root">
    <Slot name="NoKey" />
  </Slot>
);
