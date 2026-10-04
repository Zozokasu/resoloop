import { Slot, Component, ref } from "resoloop-jsx";
function dynamicType(): string { return "Synthetic.Strict"; }
export default <Slot key="root" name="Typed">
  <Component key="strict" type="Synthetic.Strict" fields={{ Amount: 2, Enabled: true,
    Caption: null, Position: [1, 2, 3], Mode: "Fast", Target: ref.slot("root"),
    Unsupported: { nested: [false, null, 1] } }} initialFields={{ Amount: 3 }} />
  <Component key="open" type="Synthetic.Open" fields={{ Amount: 1, Other: { arbitrary: true } }} />
  <Component key="unknown" type="Synthetic.Unknown" fields={{ Missing: [true, 5] }} />
  <Component key="wide" type={dynamicType()} fields={{ Amount: "legacy string", Missing: null }} />
  <Component key="empty" type="Synthetic.Empty" fields={{}} initialFields={{}} />
  <Component key="empty-omitted" type="Synthetic.Empty" />
  <Component key="alternate" type="Synthetic.Strict" fields={{ Caption: 1.5, Position: { x: 1, y: 2, z: 3 }, Mode: 2 }} />
</Slot>;
