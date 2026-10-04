import { Component } from "resoloop-jsx";
export const wrongName = (
  <Component key="name" type="Synthetic.Strict" fields={{
    Missing: 1,
  }} />
);
export const wrongNumber = (
  <Component key="number" type="Synthetic.Strict" fields={{
    Amount: "bad",
  }} />
);
export const wrongInitial = (
  <Component key="initial" type="Synthetic.Strict" initialFields={{
    Amount: false,
  }} />
);
export const wrongTuple = (
  <Component key="tuple" type="Synthetic.Strict" fields={{
    Position: [1, 2],
  }} />
);
export const wrongEnum = (
  <Component key="enum" type="Synthetic.Strict" fields={{
    Mode: "Unknown",
  }} />
);
export const wrongNullable = (
  <Component key="nullable" type="Synthetic.Strict" fields={{
    Caption: false,
  }} />
);
export const wrongOpenValue = (
  <Component key="open" type="Synthetic.Open" fields={{
    Amount: false,
  }} />
);
export const wrongEmptyMember = (
  <Component key="empty" type="Synthetic.Empty" fields={{
    Missing: 1,
  }} />
);
