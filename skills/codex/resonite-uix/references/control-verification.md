# Control recipe verification templates

Substitute the component keys and caller-selected values below. Put these entries in the manifest's `tests` array. Use `test --probe --yes --brief --report NEW_FILE.json`. It preflights all targets, saves original values, evaluates downstream assertions and restores/re-reads the sources. Tests do not deliver real clicks, typing or dragging.

```json
{
  "name": "Changed state reaches controls",
  "probe": {
    "kind": "set-members", "safe": true, "restore": true,
    "values": {
      "$member:editable-text.Content": "Temporary input",
      "$member:enabled-state.Value": true,
      "$member:mode-state.Value": 1,
      "$member:volume-slider.Value": 0.75
    }
  },
  "assertions": [
    { "target": "$member:editable-text.Content", "expected": "Temporary input", "phase": "after" },
    { "target": "$member:basic-selected.Value", "expected": false, "phase": "after" },
    { "target": "$member:advanced-selected.Value", "expected": true, "phase": "after" },
    { "target": "$member:handle-rect.AnchorMin", "expected": [0.75, 0.5], "phase": "after" },
    { "target": "$member:handle-rect.AnchorMax", "expected": [0.75, 0.5], "phase": "after" }
  ]
}
```

Those anchor expectations assume a horizontal 0..1 slider with the runtime's default AnchorOffset. Derive expectations for the actual range, direction and offset. Add an assertion for the toggle's caller-owned feedback target (e.g. tint, indicator or a bool observation field); asserting only its source is insufficient. Check slider min/mid/max separately. For choices, test each option and, if supported by the application, an unmatched value. Verify the old option becomes false when the new one becomes true. Confirm reference targets through bounded inspect; field success does not prove correct editor/consumer wiring.

State preservation needs a separate sequence because a reversible probe restores before returning:

1. Save the four original values, write temporary text/bool/selection/slider values within the owned test scope, then read them back.
2. Reapply the **same** manifest/state. Require zero mutations and unchanged user values. Desired style/configuration stays in fields; only user-owned defaults use initialFields.
3. In finally, restore original values and inspect them again, including downstream feedback. A nonzero restoration failure must be reported.

For tabs/open panels, inspect the actual target Slot's IsActive after state changes, and capture the resulting visible content. Keep state/logic outside disabled pages. For TextField, separately check Editor.Text and TextField.__text target the caller's Text, and TextField.Editor targets the intended editor. ButtonToggle.TargetValue and all ValueRadio.TargetValue references must target the intended shared field. Mark manual focus/typing/click/drag, multiplayer and save/reload as unperformed until actually tested.
