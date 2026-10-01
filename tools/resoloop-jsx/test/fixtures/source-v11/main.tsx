import { Slot, Component, Scope, ref } from "resoloop-jsx";
function Forward(props: { target: string }) {
  return <Component key="forward" type="Synthetic.Holder" fields={{
    Typo: 1,
    Target: props.target,
  }} />;
}
const spread = { SpreadTypo: 1 };
const dynamic: string = "DynamicTypo";
const transferred = ref.component("missing-transfer");
export default <Slot key="root" name="SyntheticOracle">
  <Component key="base" type="Synthetic.Base" />
  <Component key="other" type="Synthetic.Other" />
  <Forward target={ref.component("missing")} />
  <Component key="mismatch" type="Synthetic.Holder" fields={{
    Target: ref.component("other"),
  }} />
  <Component key="member" type="Synthetic.Holder" fields={{
    Target: ref.member("base", "Absent"),
  }} />
  <Component key="same" type="Synthetic.Holder"
    fields={{ SameTypo: 1 }} initialFields={{ SameTypo: 2 }} />
  <Component key="nested" type="Synthetic.Holder" fields={{
    Values: [[ref.component("missing-nested")]],
  }} />
  <Scope instanceKey="instance"><>
    <Component key="scoped" type="Synthetic.Holder" fields={{ ScopedTypo: 1 }} />
    {["a", "b"].map(key => <Component key={key} type="Synthetic.Holder" fields={{ MapTypo: 1 }} />)}
    {true ? <Component key="yes" type="Synthetic.Holder" fields={{ ConditionalTypo: 1 }} /> : null}
  </></Scope>
  <Component key="spread" type="Synthetic.Holder" fields={{ ...spread }} />
  <Component key="dynamic" type="Synthetic.Holder" fields={{ [dynamic]: 1 }} />
  <Component key="transfer" type="Synthetic.Holder" fields={{ Target: transferred }} />
</Slot>;
