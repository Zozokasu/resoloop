# UIX test14: preview.10からの改善

対象は「Figma UIテンプレートを再現」タスクと `rloop-test14/artifacts/resoloop-test-notes.md`。制作環境は Resonite 2026.9.14.1239 / ResoniteLink 0.13.1.0 / resoloop 0.1.0-preview.10。以下はその後のソース変更であり、preview.10配布物に含まれる機能一覧ではない。

これらの改善を0.1.0-preview.11としてローカルパッケージ化する。nuget.orgへの公開は別途行う。

## 調査結果と実装

| 制作中の問題 | 確認・対応 |
| --- | --- |
| `/`や先頭空白を含むSlot名がstable selectorで解決できない | 名前を文字列pathから再分割する処理を改め、checkpoint schema 2に名前の配列を保存。`path:["Root","A/B"," Label "]`で厳密指定できる。再接続・prune・同じ表示pathを持つ別階層をテスト。 |
| 異なるkeyの同名兄弟を作れても次のdiffが曖昧になる | `APPLY_SIBLING_NAME_DUPLICATE`で初回の変更前に拒否。名前を勝手に変更しない。 |
| assetのkind省略でNullReferenceException | `ASSET_KIND_MISSING`、null assetは`ASSET_INVALID`としてvalidationで返す。local textureの宣言例をスキルへ追加。 |
| SliderからBooleanValueDriverへの移行後、TargetFieldが空になる | 最小実機試験で旧Sliderの駆動が残る間の参照拒否を再現。ライフサイクル変更後にsymbolic referenceを読み戻し、1回修復・再検証。残る不一致は`APPLY_REFERENCE_NOT_RETAINED`。削除は従来どおり明示的な`--prune --yes`のみ。 |
| radio背景のkeyを変更すると初回pruneで新背景まで消える | 同名の新keyが旧Slotを再利用するケースを再現・修正。新roleには新Slotを作り、live/stale keyが同じIDを指す破損stateは`APPLY_SLOT_OWNERSHIP_CONFLICT`で変更前に拒否。削除の包含関係は表示pathではなく実際のIDと親子関係で判定。元制作の全中間状態を再現したとの主張ではない。 |
| auditとapplyの並行実行でWindowsのcheckpoint更新が失敗 | readerが置換を許す共有モード、固有一時ファイルからの置換、短い読込再試行を導入。同一stateへのwriterは排他ロックし`APPLY_STATE_BUSY`で変更前に拒否。4 readerと100回の置換を回帰テスト。 |
| raw reference値に引用符が混入 | `REFERENCE_VALUE_QUOTED`でshell引用と値そのものの引用符の違いを説明。 |
| 画像・Buttonの問題をauditだけでは追いにくい | `uix audit`にtexture/provider設定、sprite/material参照、Button ColorDrivers、editor/handle driveの観測項目を追加。読み込み完了や描画成功の判定にはしない。 |

Slot keyを変えて同じobjectを保ちたい場合は`migrateFrom`を使う。新keyへ差し替える場合は別roleとして扱う。既存checkpoint schema 1は読み込み可能で、次の保存時にschema 2へ移行する。旧stateには名前配列がないため、過去の曖昧なpathの情報までは復元できない。schema 2のstateをpreview.10へ戻して使用しないこと。宣言JSONの`schemaVersion: "1"`は変更していない。

## 市松模様は圧縮設定だけが原因とは言えない

元の `forms-live.jpg` では画像部分に市松模様があり、`forms-uncompressed.jpg` では改善している。ただし、その変更では`Uncompressed=true`と`CrunchCompressed=false`を同時に変えており、圧縮形式、派生データの選択、再読み込みの影響を分離できていない。

当時のResoniteログ `MENTAIKO - 2026.9.14.1239 - 2026-09-15 08_55_13.log` の4695行、09:25:55.795には、元のc4f8ac26...pngと一致するimport URLについて `Could not gather asset variant` がある。variantは`compression=BC3_Crunched&quality=100&width=796&height=244`。これは派生データの取得・読み込み失敗の証拠で、圧縮された画素が壊れたという証拠ではない。別時刻・別URLのネットワークエラーは、この画像の根拠に混ぜていない。

同じ3枚の元PNGを専用の`ResoLoop_Test_TextureVariants_*`へimportし、保存済みproviderとURLが一致することを確認した。同じURLに対し次の4条件、計12画像を比較した。

| 列 | Uncompressed | CrunchCompressed |
| --- | --- | --- |
| 1（デフォルト） | false | true |
| 2 | false | false |
| 3 | true | true |
| 4 | true | false |

待機後のcaptureでは全条件で正常に表示された。デフォルト条件の再読み込みも試験に含め、元providerとユーザーのキャッシュは変更していない。現在は持続的に再現せず、当時の取得失敗の内訳がサーバー・キャッシュ・ローカル生成のどれだったかは特定できない。そのためCLIやスキルの標準を一律Uncompressedへ変更していない。失敗時のsource、URL、variant、logを揃え、一項目ずつ比較する診断手順を残した。

証跡はローカルの `artifacts/test14-textures/texture-observations.json`、`texture-settled.jpg`、`texture-default-retry.jpg`、`texture-cleanup.json`。個人の全ログや素材をソース管理へ取り込まない。再試験はopt-inの`TextureVariantIntegrationTests`で、`RESOLOOP_TEXTURE_SAMPLES`に最大3つのファイルpathを`;`区切りで指定する。

## スキルに残す制作上の知見

- Figmaの静止状態と動作するcontrolを区別し、入力/caret、dropdown、radio、toggle、tabs、slider、progressの必要動作を制作前に列挙する。
- TextEditor/TextFieldのnative caretを利用し、デザイン上のstatic caretが重複していないか確認する。
- ButtonがImageのColorDriversを初期化する場合がある。Normal/Highlight/Press/Disabledのalphaとtargetを観測し、Tintの所有者を決める。
- UIX.Textの既定material参照もportable auditの対象。所有root配下の共有UI_TextUnlitMaterialと明示的なMaterials参照を検討し、shader依存は別途確認する。
- OutlinedArcとTextの前後関係は階層順だけで判断しない。局所的なmaterial/render/depth設定を試し、共有material全体に変更を広げない。
- generic ComponentはReflectionが返したassembly付きの正確な型名を使う。動作確認済みの下流field変化と、実際のクリック・入力、多人数、保存再読込を分けて報告する。

詳しい作業手順は[resonite-uix](../skills/codex/resonite-uix/SKILL.md)に集約した。今回の変更は既存のLayout知見を保ち、素材診断・入力・移行時の参照所有を補強している。

## 検証範囲

`dotnet build ResoLoop.slnx --no-restore`成功（警告・エラー0）、オフラインテスト235件成功。opt-in実機回帰10件と別実行のtexture比較1件が成功。変更した3つのskill entrypointはquick_validate成功。通常のsolution testでintegration harnessが成功してもliveを実行した意味にはならないため、この実機件数は明示的に接続して実行したものだけを数えている。

オフラインではasset validation、同名兄弟、名前配列、旧state移行、key差し替え、参照再接続、prune安全性、checkpoint並行読書き、auditの観測項目を検証した。実機ではSliderの駆動先競合、明示prune後のBooleanValueDriver両端接続、同名背景の置換、別接続からの再解決と再apply無変更を確認。画像比較とdriver試験の専用rootはfinallyで名前とIDを照合して削除済み。

実際のユーザー入力、多人数同期、元ライブラリの保存再読込は今回の試験対象外。元の制作物は編集していない。
