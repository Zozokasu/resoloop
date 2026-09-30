# ResoLoop

ResoLoopは、AIコーディングエージェントがResoniteLink経由でResoniteのSlot・Component・ProtoFluxを観測・編集・検証するための.NET CLIである。
利用者向けの説明は`README.md` / `README_JA.md`、詳細は`README-DETAILS.md`にある。

アーキテクチャ、コマンド、Resonite安全規則、上流の扱い、テストとSkillの同期規則は[`AGENTS.md`](AGENTS.md)を正とする。
この文書はClaude向けの入口で、AGENTS.mdと重複する規則は書かない。

このリポジトリはorange3134/resoloopのforkである。当面はupstreamを取り込まない。通常制作は直結（ResoniteLink）主経路へ統一し、Workbench backendは凍結・S5で除去予定である（計画: `plan/resoloop-slimming-plan.md`）。方針と調査結果は[`docs/dev/fork-strategy.md`](docs/dev/fork-strategy.md)にある。

## 製品ファイルと開発用ファイルの区別

- `skills/codex/*/SKILL.md`は、`resoloop init`で利用者のプロジェクトへ配布される**製品の一部**である。開発エージェントへの指示ではない。CLIの挙動を変えたときだけ、AGENTS.mdの規則に従って同期する。
- 開発エージェント向けの指示は、この文書、`AGENTS.md`、`docs/dev/agent-workflow.md`、`.claude/agents/`に置く。

## 調査と出力

大量のコード調査は実装担当や調査役に委譲し、概要と特徴的な実装のファイルパス・1-based行番号を受け取る。
これは読み取りコストを下げるための協働方法であり、メイン担当がファイルを読めないという意味ではない。

## 計画とフィードバック

- 正式な計画は`plan/`（git管理外）に置く。ユーザーが明示的に頼まない限り、エージェントは`plan/`を変更しない。
- 方針変更の提案は`feedback/`（git管理外）に書く。提案は正式計画を自動で置き換えない。反映するかはユーザーが決める。
- ccg-workflowの手順、起動方法、モデル解決、managed領域（`.ccg/`）はccg-workflowに委ねる。独自の起動コマンドを作らず、ccg管理ファイルを自動変更しない。
- フェーズやマイルストーンの区切りでは引き継ぎ文書を書く。仕組み、確認済みの事実、後回しにした項目、未検証の事項を分け、未達の完了条件を完了扱いにしない。

## エージェント体制

`/ccg:go`の役割、決定権、検証、Devin利用の正本は[`docs/dev/agent-workflow.md`](docs/dev/agent-workflow.md)とする。
2026-10-01のユーザー決定により、Opus直属の一担当はCodex `gpt-6.1-sol`（high reasoning effort）とする。Opusが目標・優先順位・機能間契約・charter・受け入れ・merge判断を担い、担当が調査・実装・関連build/test・agent/*ブランチでcommit・短い報告までを一貫して行う。
担当のcharter受領・報告・ブランチとcommit・ログ置き場の正本は[`.claude/agents/feature-manager.md`](.claude/agents/feature-manager.md)。Codexへ渡すcharterにこの規則へ従うよう書く。Sonnet subagentはCodexが使えないときの予備とする。
OpusはBashのbackground実行で`codex exec --model gpt-6.1-sol -c model_reasoning_effort=high --sandbox workspace-write -C "<worktree slot>" -o "<報告ファイル>" - < "<charter.md>" &`を起動し、報告ファイルとslotのcommitで受け入れる（codex-cli 0.159.2確認済み）。`--effort`は存在せず、Windowsでは改行入り引数が1行目しか届かないためcharterはファイルから標準入力で渡す。
worktreeのgit管理領域は本体repoの`.git`にあり、Codex sandboxはそこへ書けない（`--add-dir`でも不可）。Codex担当は未commitで残してcommitの分け方と件名を報告し、Opusが差分確認後に`agent/*`ブランチで代わりにcommitする。
sandbox内はネットワークが遮断される。NuGet restoreやnpm ciはOpusが外で先に行い、charterでは`dotnet build ResoLoop.slnx --no-restore`を指示する。Devin SWE-2 Max（`--model swe-2-max`）はsandbox外で使える任意手段として残すが、Codex sandbox内からは起動できない。
実装や修正ごとのCodexレビューは行わず、担当のbuild/testとOpusの差分確認で受け入れる。フェーズがおおむね完了した時点で全差分（開始commit..現main）の重めのCodexレビューを1回行う。重点は契約違反・state/所有・削除・公開JSONとエラーコードの互換性・並行性。対象commitを明示し、レビュー後の修正ラウンドはユーザーが決める。再レビューは指摘が大きく設計が変わった場合だけとし、軽微な修正には行わない。

## 完了の報告

- 成功・失敗・skip・未検証を分けて報告する。skipしたテストやlive未実行を成功扱いしない。
- live検証は、どのセッション・どの`ResoLoop_Test*` Slotに対して行ったかを証拠と一緒に報告する。
- 依頼されていないcommit、push、リリースはしない。
