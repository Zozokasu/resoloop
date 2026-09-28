// Stable-reference helpers. The emitted prefixes are read by the C#
// validator/applier (docs/DECLARATIVE.md):
//   $slot:key                -> Slot ID
//   $component:key           -> Component ID
//   $member:key.Member       -> Component member ID
//   $slot-member:key.Member  -> Slot's own exposed field ID

export const ref = {
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
