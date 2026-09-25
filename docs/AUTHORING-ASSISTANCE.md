# 宣言・素材参照・撮影の調査負担を減らす

2026-09-25。control recipe比較で観測した宣言形式の推測、同一Slotの複数素材provider、撮影用の位置/縮尺調整を対象にした追加改善。

## 宣言を生成し、必要な項目だけ照会する

```powershell
resoloop manifest scaffold --key panel --output content/panel.json --json
resoloop schema list --json
resoloop schema describe camera --json
resoloop schema describe probe --json
```

scaffoldはownership、名前付きroot、空のcomponents、camera bookmarkを生成する。Canvasや色・形・素材は指定しない。既存ファイルは上書きしない。全プロジェクトの初期化が必要なら従来のinitを使う。initのcamera例も同じ型に基づく。

schema describeは実際のparser DTOのconstructorから項目名・型・既定値を生成し、そのDTOで例を直列化する。document/node/slot/component/camera/test/assertion/probeが対象。これは展開後の宣言契約であり、JSON Schema規格の出力ではない。include/prototypes/parameters等は先に展開する。Componentのfieldsや型名の正しさはruntime Reflectionで検証する。

失敗した宣言のJSON pathは従来どおり返す。schemaVersionの数値指定、camerasの配列指定、camera.rotation/lookAt、children直下のkeyなどには、その場所の有効な記述例と部分照会の導線を返す。cameraの未指定ベクトル・同一position/target・非有限値もvalidationで扱う。

## 素材の接続を作成前に診断する

```powershell
resoloop manifest scaffold --kind provider --key front-material --type '[FrooxEngine]FrooxEngine.UI_UnlitMaterial' --output content/front.node.json --json
```

これはchildrenへ追加する1つのnode断片を出力する。Slotのkey/nameとComponentのkey/typeだけを用意し、fieldsは空。利用側が観測済みの素材設定を記入して、`$component:front-material`で参照する。独立したprovider断片をdocumentとしてapplyしたり、rootへそのままincludeしたりしない。

validate/diffに`APPLY_COMPONENT_IDENTITY_RISK`警告を追加。同じSlot内の同型Componentについて、宣言に不変で異なるidentityFieldsの証拠がない場合、該当キー（先頭8件まで）と対処を返す。planのwarningsは通常出力、brief、summary、完全reportで確認できる。警告は適用を禁止するものではなく、runtimeの参照関係で区別できる配置も含む保守的な診断。

新規providerは別々の名前付きSlotに置くと識別しやすい。既存の曖昧なcheckpointに対して、manifestへidentityFieldsを追加するだけでは過去の識別値は補完されない。実際の候補と所有を確認した明示的な回復が必要。自動adopt、ordinalによる推測、既存素材の移動/削除は行わない。

## 平面Canvasを実測して撮影する

```powershell
resoloop capture content/panel.json --frame '$slot:canvas' --view front --output artifacts/front.jpg --json
resoloop capture content/panel.json --frame '$slot:canvas' --view rear --output artifacts/rear.jpg --json
```

対象は適用済みで、選択Slot上にCanvasがちょうど1つ必要。子孫から自動探索しない。stable selectorはmanifestの既定stateを使用し、別のstateなら`--state FILE`で指定する。`--camera`と`--frame`は同時指定できない。frameにはoutputが必須で、.svgは対応しない。

ReflectionでCanvas.ColliderとBoxCollider.Size/Offsetを確認し、同じSlot上の参照先から矩形を読む。親階層は最大64 Slotsまで読み、Root座標の四隅を計算する。画像の縦横比・縦画角から距離を計算し、任意の余白`--margin 1.1`を加える。`--fov 60`、`--width`、`--height`も指定可能。正面はCanvasのlocal -Z側、背面は反対側。カメラは従来どおりworld-upを使うため、Canvas自体の傾きは画像にも残る。

戻り値framingには境界の参照元、local寸法/中心、Root座標の四隅、計算したcameraを残す。対象の位置・回転・縮尺・寸法は変更しない。撮影用の一時Slotだけを作り、従来のfinally清掃を使う。

範囲はCanvasの平面collider。はみ出す子要素、曲面、鏡映/ゼロscale、遮蔽物、自動撮影中に動く対象は保証しない。未対応/不明な境界は明示cameraを使う。実写の確認は引き続き必要。

## 検証と効率の扱い

オフラインでscaffoldの展開/validation、既存ファイル保護、schema例の解析、局所的な修正案、識別リスクとdistinct identity、strict plan/briefへの警告伝播、回転・非一様scale・offsetを含む四隅の画角内収容を検査する。

opt-in実機試験では、一意なResoLoop_Test_CanvasFrame_*配下に回転/非一様scaleを持つ親と600×400 Canvasを作成。正面・背面の実写を取得し、四隅と前面文字/背面カバーを目視確認した。対象pose維持、再applyで変更ゼロ、撮影用Slotと試験rootの清掃を確認。証跡はgit対象外の`artifacts/authoring-assistance/live/`へ保存する。

最終buildは警告/エラー0。offline 275件、通常integration harness 17件が成功（通常実行ではopt-in本体は実行しない）。別途CanvasFramingIntegrationTestsを環境変数で有効にした実機1件も成功。CLI経由のscaffold/schema/provider/validate/diff/apply/frame capture/reapplyでも成功し、再applyは6 Slots・11 Componentsを保持して変更0、検証root削除後の不在を確認した。両skillのquick_validateとgit diff --checkも成功。

前回比較の固定済みCLIと新CLIに同じ4入力を与え、offline診断の前後を再現した：

| 入力 | 旧版 | 今回 |
| --- | --- | --- |
| 数値schemaVersion | 変換エラー、修正案なし | 文字列`"1"`とdocument照会を提示 |
| 配列cameras | 辞書への変換エラー、修正案なし | bookmark辞書の有効な例を提示 |
| camera.lookAt | 無関係な`count`を候補提示 | `target`を使う有効な例を提示 |
| 同一Slotの同型provider 2個 | 警告なしでvalidation成功 | 成功を維持し、該当キーと識別リスクを警告 |

再現手順・結果は`artifacts/authoring-assistance/compare-diagnostics.ps1`と`diagnostic-replay/comparison.json`、CLI実機の手順・結果は同ディレクトリの`cli-smoke.ps1`と`cli-*`に保存した。比較は固定入力の診断内容であり、エージェントによる自律制作時間の比較ではない。

これは前回の失敗要因に対する機能検証。エージェントの入力/出力トークンやアカウント使用量の削減率を測ったものではない。前回の[Astra比較](CONTROL-RECIPE-EFFICIENCY.md)は変更せず、今回の自動化だけから使用量削減率を推定しない。

後続の独立エージェントによる各2回の測定は[宣言・素材参照・自動撮影の効率比較](AUTHORING-EFFICIENCY.md)へ分けて記録する。
