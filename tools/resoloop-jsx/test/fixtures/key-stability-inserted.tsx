import { Slot, Component } from "resoloop-jsx";

// Same keys as key-stability-base.tsx, plus one new sibling inserted at the
// front. Every shared key must survive byte-identically even though the
// shared slots' array indices shift — keys are never position-derived.
export default (
  <Slot key="root" name="Root">
    <Slot key="sidebar" name="Sidebar" />
    <Slot key="header" name="Header">
      <Component key="hdr-text" type="FrooxEngine.TextRenderer" />
    </Slot>
    <Slot key="body" name="Body" />
    <Slot key="footer" name="Footer" />
  </Slot>
);
