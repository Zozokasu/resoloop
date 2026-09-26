import { Slot } from "resoloop-jsx";

const items = [
  { id: "bullet-1", label: "Bullet One" },
  { id: "bullet-2", label: "Bullet Two" },
  { id: "bullet-3", label: "Bullet Three" },
];

// Array children from .map(): keys come from the item data, not the index.
export default (
  <Slot key="root" name="Root">
    {items.map((item) => (
      <Slot key={item.id} name={item.label} />
    ))}
  </Slot>
);
