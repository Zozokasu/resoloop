# UIX テスト13の評価と resoloop 改善計画

2026-09-14。対象は Codex タスク「Create Resonite UIX template」（`01a09ed6-05ff-7440-8435-f393732e69b2`）の全3ターンと、`../rloop-test13/` に残る制作ソース・Reflection・検証JSON・実写。初期制作、操作と共有アセットの修正、レスポンシブ化と利用者フィードバックを追跡した。以下に当初の改善計画と、その後source treeへ実装した範囲・検証結果を記録する。パッケージ公開は未実施。

## 実装済みの範囲

- `resonite-uix`とAssets/Layout/interactionの3 referenceを追加。6スキル＋referencesをinit/syncで配布し、lock schema 2でfile単位のhashを追跡する。schema 1の読込み、未編集更新、利用者編集の全件preflight保護、欠損復旧を回帰試験化した。
- 管理済みSlotで新Component keyが既存Componentをordinalで流用する不具合を最小テストで再現し修正。現役key・移行元・pruneのID重複は `APPLY_COMPONENT_OWNERSHIP_CONFLICT` として書込み前に拒否する。renameにはmigrateFromを使う。
- 新規親への移動は、Component作成・field/reference設定が終わった後に実施する。Imageの描画消失をliveで再現し、準備済み親への移動と修正applyで表示・Slot ID保持・再適用0を確認した。中断checkpointからの再開もoffline試験化した。
- `uix audit`を追加。depth/Slot予算内でLayout、Fitter、Mask、ScrollRect、driverなどの公開fieldを要約し、Graphic競合、Rect欠落、Fitterの寸法供給元欠落、観測打切りを報告する。本文Contentは要約しない。computed sizeはunknown、verificationはpartial。予算は詳細読取りと出力を制限し、各Slotの直下child一覧の通信量までは制限しない。
- `limits.expandedNodes`（既定10000、上限250000）と展開結果の上限表示を追加。includeも一つのownership/stateと予算を共有し、10 MiB/64 source file制限を保持する。境界・不正値・大規模読込みを試験化した。ローカル単独計測では40,000 Slot、160,005 JSON node、3,897,870 byteの読込みが689 ms、累積managed allocation約256 MBだった。ピーク常駐メモリやruntime applyの性能保証ではない。
- `set-members` probeを追加。全対象preflight、退避、順次書込み、成功/失敗/cancel時の逆順復元と再読取りを実装した。復元失敗時も他対象の復元を続け、失敗selectorを返す。atomic処理や実入力の検証とは区別する。
- `examples/uix-responsive.json`を追加。共有font、等幅カード、IgnoreLayout、可変の内容高さ、Mask/ScrollRectと幅＋長文probeを示す。live fixtureで通常/狭幅/長文/末尾/広幅を撮影し、元値の復元、font共有、再apply0を確認した。長短のカードが中央揃えで見えなくならないよう行をTop揃えにした。

実機はユーザー指定の`ws://localhost:3207`、Resonite 2026.9.9.1136 / ResoniteLink 0.13.1.0。実験は一意な`ResoLoop_Test*`内で実施し、finallyでrootのID・名前・親を再観測して削除した。最終画像・観測・削除記録はローカルの`artifacts/uix-final/`に保存した（git対象外）。元テスト13やユーザー制作物は変更していない。

今後の範囲は、複数manifest間のexported stable参照、部品依存閉包の抽出、区切り文字を含むSlot path、ローカルfont import、Text/Mask移行と入力状態の追加fixture、保存再取出しと複数ユーザー検証。公開APIでcomputed Rectを得られず、宣言寸法で代用していない。サンプルのfontは外部URLを使い、font binaryの同梱・可搬性保証は行っていない。

最終検証: `dotnet build ResoLoop.slnx --no-restore`は警告・エラー0。`dotnet test ResoLoop.slnx --no-build`はoffline 220件と非接続integration harness 11件が成功。別途3207でUIX fixture 2件＋既存end-to-end 6件の計8件がlive成功した。スキル形式検証とdiff whitespace検査も通過した。撮影fixtureは同じscreenshot directoryを使うため直列実行し、画像は目視確認した。

## 評価

最終成果は5ボード・41個の単体パーツ。最終監査時点で1,095 Slot・3,035 Component、上位Assetsに35個の共有プロバイダー、明示許可した外部シェーダー参照1件。5 manifest の再applyは作成・更新・削除がすべて0で、移行前後の入力・選択値等23項目も一致している。アセット共有の効果はプロバイダー構成で確認できるが、GPUメモリ・FPS・転送量の削減率は測定していない。

入力とレスポンシブ化後の操作・スクロールは利用者が確認した。Coreの幅480 / 1120、末尾スクロール、長文カード、押下・Switch・Tabs・Paginationの状態検証記録があり、今回も狭幅・広幅・長文・Switchオフの実写を確認した。長文カードは横幅を保って本文を折り返し、下のボタンと重ならず高さを増やしている。

一方、最初の成果は固定座標中心で、TextFieldのフォーカス構成やアセット共有も追加指示後に修正された。レスポンシブ化ターンには約89分を要し、配置調整だけでなく、旧Component識別と描画消失の復旧が大きな負担だった。新規制作時から再利用境界・Layout・状態所有者を決めるスキルと、既存コンテンツ移行の安全性を高める本体修正を優先する。

検証範囲には限界がある。41パーツの検証は全件offline validate＋代表5種strictであり、全パーツの独立生成・操作・保存再取出しを証明するものではない。`resoloop test` の存在assertionは `structuralOnly / partial` のまま。値の直接切替によるドライバー確認、実入力、実写、再apply、保存可搬性は別々に扱う。複数ユーザー操作とインベントリ保存後の再取出しは未確認。

## 1. 最優先でスキルへ収録する知見

独立した `skills/codex/resonite-uix/SKILL.md` を追加し、UIXの新規制作・修正・レスポンシブ化に適用する。既存 `resonite-build` から案内し、共通のReflection・所有境界・apply手順を重複させない。本文には判断に必要な要点を置き、詳細は `references/assets.md`、`references/layout.md`、`references/interaction-and-migration.md` に分ける。

以下は収録内容の設計。値や構成を全UIに固定するのではなく、どの症状・要求で選ぶかを説明する。今回の実測環境は Resonite `2026.9.9.1136` / ResoniteLink `0.13.1.0`。次の制作でも型・member・enumは対象環境のReflectionで確認する。

### アセットの整理と再利用境界

- **共有の単位は保存・配布するアイテム。** フォント、FontChain、テクスチャ、SpriteProvider、マテリアルをアイテム上位のAssetsへ集約し、パーツ間で共有する。ワールドRootへ移して保存対象の外へ出さない。
- **Assetsは通常のUI配置から分離。** 今回の `Fonts / Rounded sprites / Icons / Materials` は分かりやすい分類例。プロバイダーは役割名を持つSlotへ配置し、Layout対象の子に混ぜない。
- **同一URLだけで共有可否を判断しない。** 同じ画像でも読み込みprofile、Spriteのslice・寸法、マテリアルの描画設定、FontChainの順序が違えば別の構成。可変の色や状態は原則として部品側で持ち、共有マテリアル変更で全パーツが変わる設計かを意識する。
- **ボードを掴めることと、単体で保存できることを区別。** 今回のボードGrabbableは配置調整用で、全体Assetsに依存する。単体配布では必要な依存だけをその単体rootに含めて参照を付け直し、そのrootを監査する。
- **集約の移行順序。** 新しい共有providerを作る → 全consumerを再接続 → 参照を検査 → 旧providerだけを削除。既存consumerが旧IDを参照したまま削除しない。
- **フォントの読み込みと可搬性を分ける。** 今回のローカルfile URIは描画に失敗し、同書体の公開URLで復旧した。全ローカルフォントが非対応とは一般化せず、URL到達性・実グリフ・日本語fallback・ウェイト・保存再読込を確認する。共有すれば元のフォントバイナリが取り込まれるわけではない。
- **外部参照の許可は観測結果に限定。** 今回の例は `UI_TextUnlitMaterial:_shader` のStaticShaderを確認して許可したもの。同型providerを無条件にエンジン標準と見なさない。

想定する階層例:

```text
保存・配布する ItemRoot
├─ Assets
│  ├─ Fonts / 名前付きのFont・FontChain
│  ├─ Rounded sprites / Texture・SpriteProvider
│  ├─ Icons / Texture・SpriteProvider
│  └─ Materials / 共有描画設定
└─ UI
   └─ Canvas
      ├─ Header
      ├─ Viewport（Image・Mask）
      │  └─ Content（VerticalLayout・ContentSizeFitter・ScrollRect）
      │     └─ 部品群
      └─ Footer
```

これは今回成功した構成例であり、すべてのUIにスクロールや独立Canvasを要求しない。既存Canvasへ組み込む部品と、単体展示用のCanvas付きwrapperを分ける。

### Layout の役割と落とし穴

| 項目 | スキルに残す判断・挙動 | 今回の根拠・検証方法 |
| --- | --- | --- |
| `Canvas.Size` と Slot.Scale | Canvas内の利用可能寸法と、ワールド内の表示倍率を分ける。再レイアウトはCanvas.Sizeで確認する | Coreの480 / 1120幅検証。単なる全体拡大縮小をレスポンシブ化と扱わない |
| `LayoutElement` | Minは最低寸法、Preferredは希望寸法、Flexibleは余剰空間の配分。Flexibleだけを揃えても希望幅が異なれば等幅とは限らない | 長文カードで片側が幅を取りすぎた。修正版はカードの `PreferredWidth=0, UseZeroMetrics=true` と同じFlexibleWidthで幅配分を揃えた |
| `UseZeroMetrics` | 0を明示的なmetricとして使う場合に確認する。未指定を表す-1と区別し、必要なMin/Preferred等も併せて確認する | 0を設定しただけでは意図するカード幅にならず、UseZeroMetricsを有効化して修正。すべての要素に一律設定しない |
| `ContentSizeFitter` | 同じSlotのlayout metricsから自分のRectTransformを決める。子を集約する場合は同じSlotにLayoutが必要。MinSize / PreferredSizeは子の寸法宣言に合わせる | 参考UIにはMinSize、完成ギャラリーにはPreferredSizeが使われた。常に片方を正解にしない |
| 親Layoutとサイズの所有者 | 幅は親・高さは内容など、軸ごとに制御を決める。Layout・Fitter・ドライバー・固定Rect値の競合を調べる | 完成Contentは横Fit Disabled、縦PreferredSize。宣言値だけでなく実寸と実写を確認 |
| `HorizontalLayout` / `VerticalLayout` | 並び・Padding・Spacing・ForceExpandを親で、最小寸法・余剰配分を子で設計する。ForceExpandを両軸一律に有効化しない | 今回の縦本文はForceExpandWidth=true、Height=false。横列には別設定を使った |
| `GridLayout` | ボタン・Badge等の同種集合をセルで折り返す。CellSize、Spacing、ExpandWidthToFit、PreserveAspectOnExpandを用途で選ぶ | 幅で列数が変化。アイコン・エフェクトには横引き伸ばしを避ける設定を使用。CSSの任意wrapと同一視しない |
| Textの寸法 | 文字サイズを維持し、幅制約・折り返し・最小高さを組み合わせる。文字の自動縮小に頼って長文を押し込まない | TextのAutoSizeを無効化し最小行高さを設定。長文カードで本文とボタンの重なりを検査 |
| `IgnoreLayout` | 背景・枠・影などを配置計算から外し、実コンテンツと分ける。ImageとText等の競合Graphicは別Slot | 装飾は親に追従、本文はLayoutに参加。IgnoreLayoutは描画順・深度問題すべてを解決する設定ではない |
| 押下表現 | Layout管理下の外枠・ヒット領域を動かさず、内側の表面Offsetと文字Paddingを動かす。影は固定 | 今回は右下2px。押下時にLayoutと同じRectTransformを奪い合う旧構成から変更 |
| 小さな可動部品 | Switchノブ、チェック記号、Sliderの可動領域には固有の寸法・ドライバーがある。一般パネル用stretchを一括適用しない | ノブ枠までstretchして黒枠が伸びた。今回は20×20に戻して両状態で確認 |
| スクロールと装飾のはみ出し | ViewportのMask、Contentの寸法、ScrollRect参照・位置をまとめて確認。影や押下分を含む末尾Paddingを確保 | Paginationの影が下端で欠けた。今回の8pxはデザイン固有値。上端・下端・押下状態を撮影する |
| 固定列の最小幅 | Tabs / Pagination / Tableが狭幅で必ず自動改行するとは限らない。対応最小幅、列変更、別配置の方針を決める | 今回も極端な狭幅は未保証。成功したCoreの範囲を全パーツへ一般化しない |

LayoutElementのmetricと0の扱いは [Resonite Wiki: LayoutElement](https://wiki.resonite.com/Component:LayoutElement)、Fitterの同一Slotと子集約の関係は [ContentSizeFitter](https://wiki.resonite.com/Component:ContentSizeFitter)、装飾の除外は [IgnoreLayout](https://wiki.resonite.com/Component:IgnoreLayout) と照合した。実行時Reflectionはmemberの存在を証明するが、レイアウトの成否は実寸・画像でも検証する。

### 入力・状態と移行

- 今回の入力欄は同じSlotのButton＋TextField、子TextとTextEditorへの接続で動作した。TextFieldを追加しただけで入力完了としない。クリック・フォーカス・実文字入力・Disabledを確認する。
- 押下状態は `Button.IsPressed` → `ValueCopy<bool>` → `BooleanValueDriver<T>`、Switchの背景・ノブ、Tabs/Paginationの選択表現はネイティブ状態と参照を連動させた。具体的な接続はReflectionと既存構成で確認する。複雑な振る舞いがない段階からFluxを必須にしない。
- 設定値は `fields`、利用者が編集する入力・選択・進捗の初期値は `initialFields`。Layout/driverが更新するRect値も所有者を決める。偽差分を隠すために設定全体をinitialFieldsへ移さない。
- Componentを再作成するとinitialFieldsが再び適用される。通常の再applyでの値保持と、破壊を伴う移行での保持は別問題。移行では対象値・参照を退避し、再生成後に復元・照合する。
- 同型ドライバーを同一Slotへ大量追加すると識別が難しい。役割ごとの名前付きLogic Slot、安定key、検証できるidentityを使う。今回の回避に使ったCRC由来の `UpdateOrder` は実行順も変えるため、汎用の識別手法として推奨しない。
- 既存UIを新しい親へ移した後、設定が正しくても描画が消える事象があった。active/Enabledの切替やreparentでは復旧せず、UIX関連Componentの再生成で復旧した。エンジン内部の「再登録」が確定原因とは言わず、移行手順の未解決事象として記録する。自動全再生成は標準手順にしない。
- 保存済みstateのIDやidentityを手編集する復旧は通常workflowに入れない。既存stateに後からidentityFieldsを足すだけでは過去の識別根拠は補われない。

## 2. 本体の改善バックログ

以下は当初の設計候補と完了条件。実装済みのコマンド・範囲は冒頭とREADMEを参照し、未実装項目を現行機能として案内しない。

| 優先 | 項目 | 実装方針と完了条件 |
| --- | --- | --- |
| P0 | **Component再利用とpruneの衝突防止** | 新keyが旧key所有のComponentを暗黙に再利用し、その旧key削除が生きたComponentを消す経路を最小fixture化。plan全体で所有IDを予約し、異なる現役keyの二重割当、現役IDと削除集合の重複をmutation前に拒否。意図したrenameはmigrateFromへ誘導。新旧key混在、同型挿入・並替え、再接続、prune有無、中断再開をofflineで検査 |
| P0 | **UIX階層移行の描画消失を切り分ける** | 既存子を「新規Slotだけがある親」へ移す場合と「RectTransform / Layoutを構成済みの親」へ移す場合を比較。Graphic、Text、Layout、Mask別に縮小する。API書込み順・実寸・参照・画像を記録し、必要なら親UIX準備後に子を移す順序をadapterとCoreの境界を保って実装。原因未確定の再生成コマンドを先に追加しない |
| P1 | **UIX観測・lint** | boundedなUIX観測でCanvas/Rectの関係、Layout設定、寸法metric、Fitter軸、装飾除外、driver入出力、Mask/Viewportをまとめる。既存Graphic競合・runtime field競合案へ接続。0寸法や未接続参照は根拠を返す。等幅やpaddingの好みを一律errorにしない。公開APIで実寸が取得できない場合はunknownとし、宣言値で代用しない |
| P1 | **複数manifest間の共有参照** | 今回の `bind_shared_assets.py` と生ID再注入を、明示的な依存state＋exported stable keyで置換する案を設計。依存順、現在worldでの再解決、循環・重複ownership・欠損・曖昧さ・別worldをpreflight。自動adoptしない。再接続後も生ID編集なしで5分割の共有を再現し、途中失敗後に再開できること |
| P1 | **展開上限の説明と制御** | compilerの10,000はSlot数ではなく展開JSON node数。includeで分割しても同じ上限に合算される。Slot/Component数・JSON node数・byte数を明示し、管理された上限設定または一つのownershipの段階処理を検討。共有参照を壊す分割を強いない。上限引上げはメモリ・時間測定と境界テスト後に判断 |
| P1 | **UIX検証ケースの再利用** | 既存set-member probe / capture / testを使い、通常・狭幅・広幅・長文・末尾・押下・選択のケースを宣言可能にする。複数fieldの退避とfinally復元、復元失敗の明示、安定した描画後のcaptureを設計。初めから41パーツ全件ではなく代表fixtureで成立させる |
| P1 | **移行状態の保持** | 再生成が必要な場合だけ、typedな入力・選択値・外部consumerを捕捉して復元する明示的移行計画を設計。initialFieldsへ丸投げしない。参照再接続・値の一致・再apply0・checkpoint復旧まで確認する |
| P2 | **パーツ依存の抽出・監査** | 部品rootに必要な共有provider閉包を求め、同一構成を一度だけ同梱する設計。全体保存と部品保存で異なる監査結果を説明する。無関係なAssets・他部品・世界参照を巻き込まず、抽出先の再解決と保存再取出しを検証 |
| P2 | **Slot名の区切り文字** | `/` を名前に含むSlotと階層区切りを区別するpath表現を設計。表示pathと機械用のsegmentsを分け、state互換性・同名兄弟・rename・再接続を試験。任意のユーザー名を書き換える解決にしない |
| P2 | **フォント取り込みの明確化** | file URI失敗を最小化し、pinned公開APIが対応するasset importの範囲を調査。対応できる場合だけローカルフォント→可搬URL→StaticFontを提供。未対応なら理由と検証済み代替を示し、同書体URLを同じbinaryと説明しない |

### 本体の調査根拠と修正境界

調査時の `src/RLoop.Core/WorldService.cs` の `BuildComponentPlans` / `MatchComponent` はstateのない新keyでも型・ordinalによる既存Component選択へ進んでいた。`BuildDeletionPlans` とapplyのpruneは旧stateの削除を扱い、新keyへ流用したIDも削除した。今回この経路を最小回帰テストで再現して修正した。UIX固有の描画消失とは独立した不具合である。

`src/RLoop.Core/ApplyDocumentCompiler.cs` は展開後のJSON node数、10 MiB、includeファイル数を制限している。制限撤廃を目的にせず、分割で共有参照が扱いにくくなった実例として改善する。

Coreには汎用のplan・identity・依存参照・診断モデルを置く。ResoniteLinkの型・member・ライフサイクル差異は `RLoop.ResoniteLink` に隔離する。Fluxが必要なケースも既存SDKを使う。非公開実装のコピー、raw protocolの推測、WorldDelegateの非公開経路による接続は行わない。

## 3. スキル配布と着手順

1. **知見と新規制作のworkflowを先に配布する。** `resonite-uix` と上記referencesを作成し、`resonite-build` のUIX部分を入口へ整理する。代表の入力欄・等幅長文カード・共有Assets・スクロール末尾・押下ボタンを小さなfixtureへ落とす。元デザインのフォント・図版を無条件で同梱せず、中立な検証用素材を使う。
2. **配布経路を同時に完成させる。** 調査時の `BundledSkillManager.Names` と `RLoop.Core.csproj` は5スキルのSKILL.mdのみを列挙していた。新スキル登録とreferencesの埋込み・インストール・hash追跡を追加。既存lock移行、未編集更新、referencesの利用者編集時の競合停止、欠損復旧、全件preflightを `ProjectInitializerTests` 等で検証する。配布先に存在しないreferencesへのリンクだけを追加しない。
3. **P0のComponent所有・prune修正。** `ApplyWorkflowTests` に失敗する最小fixtureを追加してから修正。planとapplyの対象集合が一致し、曖昧な状態で書き込み0になることを確認する。
4. **UIX移行のlive最小再現。** opt-inの専用 `ResoLoop_Test*` 内で、新規生成・親変更・再生成を比較する。元テスト13や参考アイテムは使い回さない。実機修正が不要なら制約と安全な移行例を残す。
5. **共有参照と観測・検証の改善。** 上限対策とcross-manifest参照を一緒に設計し、手製PythonのID再注入・値検証を順に置き換える。既存の宣言schema、help、README、関連スキルを実装に合わせて更新する。
6. **抽出と保存・複数ユーザー検証。** 単体部品を別のCanvasへ組み込み、保存再取出しと2ユーザーでの入力・選択共有を確認して可搬性と状態同期の保証範囲を広げる。

CLIの修正では `dotnet build ResoLoop.slnx`、`dotnet test ResoLoop.slnx --no-build` を実行する。liveは環境変数で明示的にopt-inし、対象を現在sessionで確認、実験用rootのexact IDだけをfinallyで清掃する。delete/pruneの `--yes` は維持する。

### スキルの完了条件

- UIX制作依頼で新スキルが選ばれ、初稿から保存境界・共有Assets・軸別Layout方針・実入力確認を扱う。
- 知識の適用によって長文の幅独占、Switchの伸びた枠、押下とLayoutの競合、末尾の影欠けを代表fixtureで防げる。
- 設定のReflection、構造検査、値の追従、実操作、実写、保存再取出しを混同しない。
- 初期状態の設定と利用中状態の保持を区別し、移行後の参照・入力値を照合する。
- 参考文書がinit / skills sync後にも存在し、利用者編集を上書きしない。

## 4. 過去の回避策を一般ルールにしない

- 毎回UIXを全削除・再生成する、stateを直接修正する、UpdateOrderへ識別用の乱数相当値を入れる、という手順は採用しない。
- 20×20のノブ、12pxのNavigation余白、8pxの末尾余白、幅480 / 1120は今回のテスト値であり、全UI共通の規格にはしない。
- 任意のworld標準providerを自動許可しない。共有Assets化だけで保存・再配布の保証が完了したとも扱わない。
- SyncDelegateが公開APIで更新できない制約と、Button/TextField等のネイティブ操作ができることを区別する。API制約を理由にすべてのUI操作を不可能としない。
- 参照を読む際はscopeと出力を絞る。制作履歴にある大規模な `--members` dumpをスキルの標準手順として複製しない。

## 証拠への入口

以下はレビュー時点の隣接テストプロジェクトにある証拠。配布スキルから依存させるパスではない。

| 証拠 | 所在（`../rloop-test13/` 基準） |
| --- | --- |
| 完成状態・用途側への未接続事項・保存境界 | `content/README-pipipi.md` |
| Layout、UseZeroMetrics、IgnoreLayout、押下分離、ノブ例外、スクロール | `content/responsive_uix.py` |
| ネイティブ状態接続・provider集約 | `content/uix_behaviors.py` |
| manifest間の生ID再解決という現状の負担 | `content/bind_shared_assets.py`、`.resoloop/shared-assets-bindings.json` |
| 参考UIの読み取りと型情報 | `.resoloop/responsive-ref-template.json`、`responsive-ref-hanafuda.json`、`responsive-layout-types.json`、`metadata/` |
| 旧識別の復旧・欠落したbinding | `.resoloop/r3-pre-identity.state.json`、`r3-identity-recovery.json`、`r3-missing-bindings.json` |
| 描画消失と復旧を比較する画像 | `.resoloop/r3-core.jpg`、`r3-core-rebound.jpg`、`r3-core-rebuilt.jpg`、`r3-button-smoke.jpg` |
| 最終再apply | `.resoloop/r3-final-converge-*.json` |
| 23項目の移行前後照合 | `.resoloop/r3-preserved-value-checks.json` |
| 値の追従と復元 | `.resoloop/r3-state-checks.json` |
| サイズ・長文・スクロールと復元 | `.resoloop/r3-responsive-checks.json`、対応する `r3-responsive-*.jpg` |
| 修正したSwitch・Navigation・Pagination | `.resoloop/r3-switch-off-final.jpg`、`r3-switch-on-final.jpg`、`r3-navigation-top-final.jpg`、`r3-pagination-selection-final.jpg` |
| 単体パーツ検証・全体参照監査 | `.resoloop/r3-parts-validation.json`、`r3-item-audit.json` |

本計画作成では制作ログとローカル成果物を読み取り、Wikiと現在の開発コードを照合した。実機への接続・変更、元テスト成果物の編集は行っていない。

