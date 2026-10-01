import { Slot, Component } from "resoloop-jsx";

export const ownership = { key: "house-world" };
export default <Slot key="root" name="House" parent="Root">
  <Component key="root/component:FrooxEngine.BoxCollider:0" type="FrooxEngine.BoxCollider" fields={{ Size: [1, 1, 1] }} />
  <Slot key="$path:Root/House/Child" name="Child" />
</Slot>;
