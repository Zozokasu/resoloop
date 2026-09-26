import { Slot, Component } from "resoloop-jsx";

export default (
  <Slot key="root" name="Root">
    <Component
      key="collider"
      type="FrooxEngine.BoxCollider"
      fields={{ Size: [1, 1, 1] }}
    />
    <Slot key="child-a" name="ChildA">
      <Slot key="grand" name="Grand" position={[1, 2, 3]}>
        <Component key="grab" type="FrooxEngine.Grabbable" />
      </Slot>
    </Slot>
    <Slot key="child-b" name="ChildB" scale={[2, 2, 2]} />
  </Slot>
);
