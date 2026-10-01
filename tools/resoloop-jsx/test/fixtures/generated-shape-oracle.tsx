import { Slot, Component, Scope } from "resoloop-jsx";

// Values and expected JSON are chosen by hand, independently of generated metadata.
export default (
  <Slot name="Shape Oracle" key="oracle" parent="Root"
    position={[2, -3, 4]} rotation={[0, 0, 0, 1]} scale={[1, 2, 3]}
    managedFields={["position", "rotation", "scale"]} preserveWorldTransform={false}
    migrateFrom="old-oracle" relocationTransform="world" runtimeRelocatable={false}>
    <Component type="Oracle.Root" key="root-component"
      fields={{ Enabled: false, Empty: null, Target: "$slot:oracle" }}
      migrateFrom="old-component" initialFields={{ Seed: 17 }} identityFields={["Seed"]} />
    <Scope instanceKey="unit">
      <Slot name="Scoped" key="child" parent="IgnoredForChild" migrateFrom="previous"
        preserveWorldTransform={false} runtimeRelocatable={false}>
        <Component type="Oracle.Child" key="receiver"
          fields={{ Target: "$slot:child", Other: "$component:receiver", Member: "$member:receiver.Value", Empty: null }}
          migrateFrom="old-receiver" initialFields={{ BootTarget: "$slot:child", Enabled: false }}
          identityFields={["BootTarget"]} />
        <Component type="Oracle.Empty" key="empty" {...({ fields: null, initialFields: null, migrateFrom: null } as any)} />
      </Slot>
    </Scope>
    <Slot name="Omitted" key="omitted" />
  </Slot>
);
