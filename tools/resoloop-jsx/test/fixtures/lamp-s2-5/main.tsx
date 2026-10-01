import { Slot, Component, ref } from "resoloop-jsx";
import { amount } from "./config.js";
export const ownership = { key: "synthetic-lamp-owner" };
export default <Slot key="lamp" name="ResoLoop_Test_SyntheticLamp" parent="Root">
  <Component key="base" type="Synthetic.Base" />
  <Component key="holder" type="Synthetic.Holder" fields={{
    Amount: amount,
    Target: ref.component("base"),
  }} />
</Slot>;
