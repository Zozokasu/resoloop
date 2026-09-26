import { Slot, Component } from "resoloop-jsx";

export default (
  <Slot key="root" name="Root">
    <Slot key="header" name="Header">
      <Component key="hdr-text" type="FrooxEngine.TextRenderer" />
    </Slot>
    <Slot key="body" name="Body" />
    <Slot key="footer" name="Footer" />
  </Slot>
);
