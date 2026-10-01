# Offline simulated session only

This catalog was **not acquired from Resonite**. It copies the unchanged V11
synthetic type definitions, with `synthetic: false` and explicit
`OFFLINE-FIXTURE-*` identities solely to exercise the connected-command
boundary against FakeResoniteClient. It is never runtime evidence and must
never be used for a real connection. The original synthetic catalog remains
rejected by connected commands.

The TSX uses Synthetic.Holder.Amount/Target and Synthetic.Base, not real Lamp
types/members. R1/R2 keep config amount 1, R3 changes it to 2, R4 replaces
`Amount` with `Typooo` and `ref.component("base")` with
`ref.component("missing")`, and R5 restores TSX with config amount 2.
The independent handwritten R4 locations use exclusive one-based ranges.
