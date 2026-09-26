import { Slot, Component } from "resoloop-jsx";
import type { JsxNode } from "resoloop-jsx";

interface RowProps {
  index: number;
  label: string;
}

function Row(props: RowProps): JsxNode {
  return (
    <Slot
      key={`row-${props.index}`}
      name={`Row ${props.label}`}
      position={[0, props.index, 0]}
    >
      <Component
        key={`row-${props.index}-tag`}
        type="FrooxEngine.Tag"
        fields={{ Value: props.label }}
      />
    </Slot>
  );
}

export default (
  <Slot key="root" name="Root">
    <Row index={0} label="alpha" />
    <Row index={1} label="beta" />
  </Slot>
);
