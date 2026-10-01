# V11 synthetic independent oracle

`catalog.synthetic.json` is the fixed synthetic catalog original, not a runtime Reflection capture. Both identities use `SYNTHETIC-*` versions and `synthetic: true`. The `live` source claim models verified acquisition only for offline tests; it is not real-world evidence.

Fixed canonical content SHA-256: `60eca209e09e4eb9707232b7966d0812d4c9c079133bdb11ffcfa2e8cb7b6446`.

`oracle.handwritten.json` separately fixes acquisition identity/hash, member value/target types and thirty individually chosen IR inputs with expected issue codes and JSON paths. Ordinary Single values, maximum/minimum boundaries, overflow, NaN/Infinity, conversion failure, rounding/underflow, nullable, tuple, list/array/dictionary/SyncObject and initial-only fields are covered. Reference cases independently cover exact type, acquired inheritance, proven incompatibility and insufficient closure/generic evidence. Diagnostics do not come from generated TypeScript, a catalog traversal or converter output.

These files are reviewable originals. Do not overwrite them with a generator or build output. Changing expectations is a specification change. The .NET oracle and npm CLI contract both read this table, while adapter mapping is tested with separately handwritten SDK definitions and expected nested metadata.
