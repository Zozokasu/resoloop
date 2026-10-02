// Stable-reference helpers. The emitted prefixes are read by the C#
// validator/applier (docs/DECLARATIVE.md):
//   $slot:key                -> Slot ID
//   $component:key           -> Component ID
//   $member:key.Member       -> Component member ID
//   $slot-member:key.Member  -> Slot's own exposed field ID

import { scopeKey } from "./scope.js";

export const ref = {
  /** Source-only alias, resolved by the C# compiler to a member selector. */
  field: (key: string) => `$field:${key}`,
  /** Compose an absolute scoped key, including nested instance segments. */
  key: (...segments: [string, string, ...string[]]) =>
    segments.reduce((scope, segment) => scopeKey(scope, segment, "ref.key"), ""),
  /** `$slot:${key}` — reference to a Slot declared with this stable key. */
  slot: (key: string) => `$slot:${key}`,
  /** `$component:${key}` — reference to a Component declared with this key. */
  component: (key: string) => `$component:${key}`,
  /** `$member:${componentKey}.${member}` — reference to a Component member. */
  member: (componentKey: string, member: string) =>
    `$member:${componentKey}.${member}`,
  /** `$slot-member:${slotKey}.${member}` — reference to an exposed Slot field
   *  (Position, Rotation, Scale, Name, Tag, Parent, IsActive, IsPersistent,
   *  OrderOffset). */
  slotMember: (slotKey: string, member: string) =>
    `$slot-member:${slotKey}.${member}`,
};
