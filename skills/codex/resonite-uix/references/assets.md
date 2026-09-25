# Shared assets and portable parts

Share at the root that will actually be saved. A useful arrangement is ItemRoot/Assets with Fonts, Rounded sprites, Icons, and Materials, alongside ItemRoot/UI. Keep resource-only Slots out of UI layout. Use named provider Slots so repeated types can be identified after reconnecting.

Share providers only when their configuration and intended mutability match: texture URL and import profile, sprite texture/slice/fixed dimensions, font chain order, and material settings. The same texture may need different SpriteProviders. Per-control tint and selection state usually belong on the control's Image or driver; changing a shared material can change every consumer. Provider sharing reduces duplicated component configuration, but does not establish measured GPU memory, bandwidth, or FPS savings.

For consolidation, create the shared providers, rebind every consumer, inspect the references, and only then prune the exact obsolete providers after reviewing deletions. Keep stable keys for retained providers; use migrateFrom for an intentional key rename. A provider referenced by another manifest needs a current-session binding; a raw ID in an old generated JSON is not portable across sessions.

For standalone parts, include only the required provider closure under that part's saved root and rebind the internal references. A Grabbable on a gallery board can be only a placement handle: if the board uses Assets above it, copying the board alone can break it. Audit both the whole library and the intended standalone root with item audit. Audit policy approval is not a save/reload test. Allow engine shader/font roles only after inspecting the actual audit evidence; never blanket-allow all providers of that type.

Use native editable Image/Text controls and reusable sliced sprites for borders and rounded backgrounds when suitable. Avoid baking the entire UI into one image. Keep fonts, source images and their provenance explicit. In the test13 runtime a local file URI StaticFont did not render; the same font family at a reachable public URL did. This is a recorded failure, not proof that all local fonts are unsupported. Check actual Latin/Japanese glyphs, fallback chain, weights, URL access, and save/reload. A public font URL is not evidence that the supplied font binary was imported.

For local textures, declare an asset and bind its imported URL to a provider; a machine-local `file:` URI is not a portable import. The asset dictionary requires `kind` and `source`, for example `"assets": {"skin": {"kind": "texture", "source": "assets/skin.png"}}`, then `"URL": "$asset:skin"` on a reflected StaticTexture2D component. A SpriteProvider references that texture component, and Image references the sprite. Paths are manifest-relative. Missing `kind` now returns `ASSET_KIND_MISSING` during validation rather than reaching import.

UIX.Text may acquire an engine-provided text material. For a self-contained library, inspect its Materials references and use an owned, shared UI_TextUnlitMaterial when appropriate. Audit its shader dependency separately; do not assume sharing a font makes text material references internal. If depth behavior differs, use a separate material for that role instead of changing a shared material for every text consumer.

For checkerboards or missing images, diagnose in this order:

1. Decode/view the source image to separate baked checker pixels from a runtime placeholder. Record the source hash, imported URL, and actual Sprite/Texture references.
2. Read the provider configuration and Resonite log around the failure time. Look for the exact asset URL and variant, not an unrelated asset/server error. `uix audit` includes provider settings but cannot prove a successful load.
3. Under a unique test root, compare the same URL with default settings and one changed setting at a time. Observe after settling; retry the default and capture the result. Preserve the original provider and do not clear the user's global cache as a routine test.
4. Treat Uncompressed/CrunchCompressed changes as variant/reload workarounds until the evidence isolates compression. Record quality, memory implications and any verification gap before making a library-wide choice.

Test14 originally logged `Could not gather asset variant` for `BC3_Crunched` on a checkerboard texture. A later sandbox using the same three imported URLs rendered all four Uncompressed/CrunchCompressed combinations, including defaults. This demonstrates a past variant-load failure and no persistent reproduction; it does not establish corrupt compression, a particular cache/server cause, or a universal need for uncompressed UI textures. Keep defaults unless a controlled test justifies an override.

## World-space background material

For UI placed in world space, use an explicitly configured **UI Unlit Material** (`[FrooxEngine]FrooxEngine.UI_UnlitMaterial`) on the background Image's `Material` reference. Without this background depth configuration, elements that should be hidden behind the panel can show through. Use this as the normal background recipe for world-space panels:

| Member | Value |
| --- | --- |
| `ZWrite` | `On` |
| `ZTest` | `LessOrEqual` |
| `OffsetFactor` | `1` |
| `OffsetUnits` | `100` |

The reflected spelling is `OffsetFactor`, not `OffestFactor`. `ZWrite` and `ZTest` are enums; use their names rather than boolean values or guessed numbers. Example keyed material declaration:

```json
{
  "key": "background-material",
  "type": "[FrooxEngine]FrooxEngine.UI_UnlitMaterial",
  "fields": { "ZWrite": "On", "ZTest": "LessOrEqual", "OffsetFactor": 1, "OffsetUnits": 100 }
}
```

Set the background Image's field to `"Material": "$component:background-material"`. Keep the material under the saved item's Assets/Materials and share it among backgrounds requiring the same configuration. This is a background material, distinct from UI_TextUnlitMaterial; do not propagate these settings indiscriminately to text, icons, foreground decoration or intentionally translucent layers. Hierarchy order and IgnoreLayout alone do not supply this depth behavior. After applying, inspect the actual Image.Material target and all four values, then check overlapping UI from relevant viewing angles for both unwanted show-through and hidden foreground content.

On 2026-09-19, read-only inspection of the user's `UIX Template/Canvas/Background mask` Image confirmed its Material reference targets UI_UnlitMaterial with exactly these four values; runtime Reflection confirmed the member names and types. The user supplied the visual failure and remedy; this inspection verified the configuration, without modifying the example or running a before/after visual experiment. Reuse the configuration, not the example's session-scoped IDs.

## Rear cover with reverse culling

Put new front/rear material providers in separate named Slots below Assets/Materials. `manifest scaffold --kind provider --key rear-material --type '[FrooxEngine]FrooxEngine.UI_UnlitMaterial' --output NEW_NODE.json` generates the structural node; append it to children, then fill the fields below. Colors and render settings remain caller-owned. Review identity warnings in diff; adding identityFields after an ambiguous checkpoint exists does not retroactively populate that checkpoint.

By default, add a rear cover to a world-space UI panel as part of its background construction. Users can walk around or turn a panel, and a front-facing background alone can disappear from behind. Skip the extra cover only when the intended design is one-sided or see-through, or an existing backing already closes the rear. Apply this at the panel's background boundary; it does not require duplicating every button, label or decorative layer.

1. Add a dedicated child Slot under the front backdrop (or an equivalent background layer), with its own RectTransform and Image/GradientImage. Match the front background's bounds, rounded sprite, nine-slice sizing, tint and gradient; let the cover follow resizing. Keep it outside content layout and set `InteractionTarget=false` so it remains decorative.
2. Keep the front-facing material and create a separate UI_UnlitMaterial for the rear with `Sidedness=Back`. The observed front material used `Sidedness=Front`. Verify `Sidedness` through runtime Reflection; these values select the rendered side, so do not confuse them with which face a generic culling API removes. Retain the background depth recipe: `ZWrite=On`, `ZTest=LessOrEqual`, `OffsetFactor=1`, `OffsetUnits=100`.
3. Bind only the rear Image's `Material` to the new material. Keep its provider under the saved item's Assets/Materials, reusing the front sprite when its configuration matches. Preserve alpha clipping for rounded transparent corners; the tested material used `AlphaClip=true` and `AlphaCutoff=0.01`, but match the actual sprite/front configuration rather than treating that cutoff as universal. Share a rear material only among matching rear-cover roles.
4. Inspect both material references and their sidedness after apply. Capture the front and rear: the rear should show the intended background, while front controls and text remain visible, with no edge gaps or flicker. Check resized bounds and interaction targets, then reapply to confirm convergence. Keep the cover and its required providers in the standalone panel's saved root.

Example rear material fields, in addition to the background Image's own sprite/color configuration:

```json
{
  "Sidedness": "Back",
  "ZWrite": "On",
  "ZTest": "LessOrEqual",
  "OffsetFactor": 1,
  "OffsetUnits": 100
}
```

A dedicated rear background avoids making foreground text and controls visible in reverse. Do not change a shared front material to Back or switch the whole UI to Double as a shortcut. With an unrotated duplicate, reversing material sidedness is sufficient in the observed setup; do not also rotate it 180 degrees by default. If a particular hierarchy still clips the cover, inspect Canvas culling and depth behavior through Reflection and captures before changing global settings.

Evidence: the final revision of test15's `content/build_aqua.py` added `Backdrop/Backdrop - reverse culling` with a GradientImage and its own Back material. The referenced task checked front/rear captures and read back the five material fields; reapply reported zero changes. Its inner reusable controls were not duplicated for the rear. This provides a working panel recipe, not proof that every clipping/material combination behaves identically.
