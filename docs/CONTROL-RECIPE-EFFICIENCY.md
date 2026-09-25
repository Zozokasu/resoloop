# UIXコントロールレシピの拡張と効率比較

2026-09-24〜25。既存のbutton・boolean-state・scroll-contentに、value-state・text-input・toggle・choice・sliderを追加した。形、色、文字、素材、固定寸法、フィードバック表現は利用側が指定する。

## 機能と境界

| レシピ | 機能構造 | 利用側が用意するもの |
| --- | --- | --- |
| value-state | typed ValueFieldと初期値。再applyで現在値を保持 | 型、初期値、用途 |
| text-input | focus用Button、TextField、TextEditorの参照接続 | 入力用Graphic、別SlotのText、文言・書体・配置 |
| toggle | Button slot eventとButtonToggle、共有boolへの参照 | 状態の保存先、ヒット領域、任意のフィードバック |
| choice | ValueRadio、共有状態、各項目の派生selected出力 | 選択値、項目数、配置・外観 |
| slider | floatの範囲・初期値・方向、ハンドルへの参照 | 軌道、つまみ、基準位置anchorOffset、寸法・表現 |

choiceのselected boolをページのIsActiveへ接続すればタブ、bool状態とtoggleをパネルのIsActiveへ接続すれば開閉パネルを構成できる。これらに専用の見た目付きレシピは不要。Dropdownのフォーカス・外側クリック・キーボード操作は、今回の部品だけで実装済みとはしない。

利用中の文字列はcaller TextのinitialFields.Contentへ、共通状態やslider値の初期値はrecipeのinitialFieldsへ置く。派生selectedと駆動先anchorは別のwriterやfieldsで上書きしない。[接続方法](../skills/codex/resonite-uix/references/recipes.md)と[検証雛形](../skills/codex/resonite-uix/references/control-verification.md)もinit/skills syncで配布する。

## 実機調査で直した点

ResoniteLink 0.13.1のTypeReferenceはopen genericと引数を別々に返す。従来adapterは引数を末尾へ追加し、Sliderのdirection型を `Slider<>+Direction<float>` としていた。公開upstreamモデルの説明を確認し、placeholderへ引数を配置するようadapter内で修正。runtime Reflectionで `Slider<float>+Direction` とenumのHorizontal/Verticalを確認し、宣言の厳密検証・値変換まで実機で通した。CoreへResoniteLinkモデルは追加していない。

また、方向変更だけではAnchorOffsetが横用のまま残ることを実測。最終レシピではanchorOffsetもcallerの必須引数にした。中央を通る横向きなら[0,0.5]、縦向きなら[0.5,0]を指定でき、別の基準位置にも対応できる。これはレシピの固定デザインではない。

## 比較方法

両側ともAstra軽（gpt-6-astra / low）の独立したサブエージェント。CLIとスキルを別ディレクトリへ固定し、publishは親が行って両エージェントの測定対象から除外した。両側へ同じ設定パネル制作課題を渡し、相手の成果物・現在の製品ソースは読ませていない。

課題は、文字入力、boolトグル、二択の単一選択、floatスライダーを一つのworld-spaceパネルに作ること。参照接続、可逆probe、編集値を維持した再apply、通常/変更後/背面の実写、正確なテストrootのfinally清掃を要求した。実機はユーザー指定port 55855、Resonite 2026.9.18.82 / ResoniteLink 0.13.1.0。

ベースラインは今回の拡張前、つまり前回追加した3レシピとbrief/reportを使用可能な版。改善側は8レシピとgeneric型修正を使用した。したがって前回のボタンだけの比較とは別の実験である。

比較用CLIを固定した後、最終ソースではsliderのanchorOffsetを必須引数に拡張した。比較課題は横向きで、固定版の既定offsetで成立するものだった。以下は固定版の実測であり、最終版の縦向き対応による効率効果を含めていない。最終版の横/縦方向は別の実機回帰で確認した。

## 実測結果

各条件1回の結果。今回のレシピ拡張だけでは、エージェント使用量の削減を確認できなかった。

| 指標 | 拡張前 | 拡張後 | 変化 |
| --- | ---: | ---: | ---: |
| 入力トークン総数 | 1,946,731 | 1,947,509 | +0.04% |
| うちキャッシュ入力 | 1,890,816 | 1,882,880 | −0.42% |
| キャッシュを除く入力 | 55,915 | 64,629 | +15.6% |
| 出力トークン | 8,317 | 8,420 | +1.2% |
| CLI呼び出し | 58 | 62 | +6.9% |
| ログ対象CLIの実行時間合計 | 105.380秒 | 86.153秒 | −18.2% |
| エージェントsession経過時間 | 8分51.7秒 | 9分20.0秒 | +5.3% |
| ソースmanifestのファイルサイズ | 28,805 bytes | 24,021 bytes | −16.6% |

トークンは各sessionの最終token_count実測。キャッシュを除く入力はinput_tokens − cached_input_tokensで算出した。CLI回数は各armのrun.jsonlに加え、ロガー導入前のhelpを1回ずつ含む。そのhelpの時間・出力bytesは推定していない。CLI実行時間はモデルの生成、ファイル編集、画像確認等を含まないため、完成までの時間とは異なる。session時間は最初と最後の記録の差で、親の実装・publish・回帰検証・集計作業は含まない。これらからアカウントの使用量枠や料金の削減率は算定できない。

両側とも通常/変更後/背面の実写を取得し、9件の可逆probe、編集した4状態の保持、再applyで変更ゼロ、所有rootの正確な削除と不在確認を完了した。拡張後には24件の参照・状態・素材の明示的検査も残した。最終パネルは拡張前26 Slots / 67 Components、拡張後26 Slots / 60 Components。意匠と作者の選択が異なるので、component数やmanifestサイズの差をレシピ単独の効果とはしない。

両側にschemaVersion・camera宣言形式などの調査/修正が発生した。拡張後は同一Slotの複数material providerによる曖昧な再接続を修正するため、所有rootを検証して一度作り直した。さらに撮影時のworld scaleを修正した。レシピの互換性・内部接続の失敗は報告されていないが、宣言スキーマ、素材の識別、撮影の試行錯誤はレシピで省略できていない。改善側は公開DTO metadataを大量出力した場面もある。これらは観測した要因であり、各要因のトークン増分を分離して測ったわけではない。

次の改善候補は、外観を含まない有効なmanifest/camera宣言の雛形、CLIからの限定的なschema照会、material providerの安定した識別を作成前に検査する仕組み。部品数を増やすだけで使用量が下がるとは判断しない。今回の各1回の比較には生成のばらつきと独立したデザイン判断も含まれるため、削減率を主張するには同じ構造・検証範囲で複数回の比較が必要。

## 証跡

ローカル `artifacts/efficiency-controls/`（git対象外）のbaseline/optimized各report.mdを入口に、manifest、helper、CLIログ、probe、capture、状態保持・清掃記録を保存する。session-metrics.jsonは各session JSONLの最後のtoken_countから抽出する。collect-metrics.ps1とbinary-hashes.jsonに集計手順・比較用DLLの識別を残す。個人のsession全文や素材はソース管理へ追加しない。

最終ソースの回帰は、レシピ展開・未指定引数/無効参照・状態の所有・スキル配布保護・generic型mappingをオフラインで検査する。opt-in実機回帰では、editor参照、toggle参照、選択の両状態/未選択、sliderの端点/中間/縦方向、値復元、編集値保持、タブ表示、再apply無変更を確認する。物理的なクリック・キーボード入力・drag、複数ユーザー、保存再取出しは別の未実施項目として扱う。

最終検証は `dotnet build ResoLoop.slnx --no-restore` が警告/エラー0、`dotnet test ResoLoop.slnx --no-build` がoffline 264件と通常のintegration harness 16件成功。後者のopt-in本体は環境変数なしでは実行されない。別途 `RESOLOOP_RUN_INTEGRATION=1` と接続URLを指定したUixRecipeIntegrationTestsは実機2件成功。スキルのquick_validateとgit diff --checkも成功した。
