import { Slot, Component } from "resoloop-jsx";
export default <Slot name="Modes" key="modes">
  <Component type="Test.Modes" key="values" fields={{ Config: 2, Runtime: 3, Driven: 4 }}
    initialFields={{ Seed: 1 }} propertyModes={{ Config: "config", Seed: "initial", Runtime: "runtime", Driven: "driver-owned" }} />
</Slot>;
