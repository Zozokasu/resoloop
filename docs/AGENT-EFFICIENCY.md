# UIXエージェント制作の効率化と実測

2026-09-24。要約出力、検証手順、ビジュアルを固定しない機能レシピを実装した。実装前後のCLIを別ディレクトリへpublishし、独立した2つのサブエージェントに同じ制作課題を実行させた。両方とも `gpt-6-astra`、reasoning effort `low`（Astra軽）。

## 実装

- `--brief`: diff/planは変更対象・理由を一度だけ表示。`--summary --brief`は件数のみ。UIX auditは問題と観測範囲、testは失敗・未検証ケースを残す。既存の通常JSON形式は維持する。
- `--report NEW_FILE.json`: 省略前の成功/エラーJSONを保存する。既存ファイルを上書きせず、接続・world変更より前に保存先を確保する。
- 通常はdiffによる対象確認からapplyへ進む。diff内部のoffline/runtime Reflection検証とapply直前の検証は維持し、同じ確認のためだけにvalidateの2モードを先行実行しない。スキル、init生成AGENTS、クイックスタートを更新した。
- `uix recipe list / describe NAME / export NAME --output NEW_FILE.json`: button、boolean-state、scroll-contentを提供。通常のprototype/includeを使い、独自runtimeやProtoFlux compilerを追加しない。init/skills syncでもレシピJSONを配布し、編集済みファイルの保護を適用する。

レシピにはImage/Text、色、形、素材、文字、固定寸法、固定の押下アニメーションを含めない。`rect`、`off`、`on`などは利用側が指定する値。Buttonは押下/ホバー状態を公開し、ColorDriversを空にして見た目のfieldを奪わない。Boolean state bindingは任意のbool sourceとtyped targetをつなぐ。レシピだけでは完成した可視UIではなく、利用側のGraphic、配置、入力/application wiringが必要。[接続口と例](../skills/codex/resonite-uix/references/recipes.md)を参照。

## 制作課題と成果

小さなworld-spaceパネルに、同じButton機能を持つ2つの異なる表現を作る。片方は押下時に内側の面が移動し、もう片方は色が変わる。表示とヒット領域を分離し、ラベルと前後の背景を備える。押下のfield probe、値復元、実写、再apply収束を確認する。

実機はResonite 2026.9.18.82 / ResoniteLink 0.13.1.0、ユーザー指定port 55855。両エージェントとも独立した `ResoLoop_Test_Efficiency_*` を作成し、finallyで正確なID・名前を確認して削除し、同名Slotが残っていないことを確認した。ユーザーの既存コンテンツは変更していない。

| 項目 | 従来手順 | 改善後 |
| --- | ---: | ---: |
| Slot数 | 20 | 20 |
| 管理Component数 | 37 | 39 |
| 下流fieldのprobe assertion | 3 | 6（押下・解除） |
| 実写 | 通常・押下・背面 | 通常・押下・背面 |
| 再apply mutation | 0 | 0 |
| レシピ使用 | なし | button、boolean-state |

通常/押下/背面の画像とprobe結果を確認済み。実際のクリック、複数ユーザー、保存再取出しを実証したという意味ではない。別のopt-in実機回帰では3レシピを展開し、配線・2種類の下流field・復元・scroll位置保持・再apply無変更を確認した。

## トークン実測

各サブエージェントのローカルsession JSONLの最後の `token_count.info.total_token_usage` を使用。model/effortも `turn_context` で照合した。推測値やCLIのbyte数から換算した値ではない。

| 指標 | 従来手順 | 改善後 | 減少 |
| --- | ---: | ---: | ---: |
| 入力token（キャッシュ込み累積） | 1,640,038 | 863,389 | 47.4% |
| うちcached input | 1,577,984 | 820,352 | 48.0% |
| 未キャッシュ入力token | 62,054 | 43,037 | 30.6% |
| 出力token | 6,688 | 5,231 | 21.8% |
| total_tokens | 1,646,726 | 868,620 | 47.3% |
| 外側のツール呼び出し | 27 | 15 | 44.4% |
| 記録されたCLI呼び出し | 53 | 38 | 28.3% |
| 記録されたCLI出力byte | 134,487 | 109,127 | 18.9% |

未キャッシュ入力はinput_tokens − cached_input_tokens。reasoning_output_tokensはそれぞれ226/160で、outputへ重複加算しない。外側のツール呼び出しはsession中のcustom_tool_callとfunction_callの合計で、functions.exec内部の複数CLI呼び出しとは異なる。

同じ改善後auditの詳細レポートは20,832 byte、エージェント向け出力は291 byteだった（report pathを含む）。詳細を捨てずにコンテキストへの流入を抑えられている。

これは独立した制作1回ずつの比較で、因果を要因別に分離した統計試験ではない。レイアウト・生成コード・Component数・検証内容は完全一致ではない。従来側には初期publishのrestore失敗と再実行、改善後にはmutation前のkey衝突修正が含まれる。CLIログは初期help等を一部含まないが、session token数はそれらも含む。課金額やアカウント使用上限の減少率とは同一視しない。

測定には凍結した改善版snapshotを使用。その後追加したbrief時の進捗抑制、失敗時のaudit短縮、init生成AGENTSの整理については、この削減率に効果を加算していない。最終ソースはbuild警告/エラー0、offline 256件成功、非接続integration harness 15件成功、別途opt-in実機レシピ回帰1件成功。変更した2スキルの形式検証とdiff whitespace検査も成功した。非接続harnessの成功件数をlive成功件数には含めない。

## 再確認用の証跡

ローカルの `artifacts/efficiency/`（git対象外）に以下を保存した。生のsession全文・画像・個人ログはリポジトリへ追加しない。

- `baseline/report.md`、`optimized/report.md`: manifests、実行ログ、probe、capture、収束・清掃の入口。
- `session-metrics.json`: 抽出したmodel/effort/token数と呼出し件数。
- `binary-hashes.json`: 比較に使用したCLI/Core DLLのSHA-256。
- `recipe-regression/result.json`、`cleanup.json`: 最終機能レシピの実機回帰。

回帰コマンド:

```powershell
dotnet build ResoLoop.slnx
dotnet test ResoLoop.slnx --no-build
$env:RESOLOOP_RUN_INTEGRATION="1"
$env:RESONITE_LINK_URL="ws://localhost:<current-port>"
dotnet test tests/RLoop.IntegrationTests/RLoop.IntegrationTests.csproj --no-build --filter FullyQualifiedName~UixRecipeIntegrationTests
```
