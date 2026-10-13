# Proposal

## Why

2026-10-11 に方向が「差分で合わせる」から「定義から作り直す」へ変わった後、古い方向の資産と新しい方向の実装が並んだまま進み、実装と進め方の両方にずれが出た。ユーザーは 2026-10-13 に「最近の実装が迷走している」と指摘し、直す対象を次の 6 点と決めた。

1. `apply` が二系統で並び、通常の側も伸び続けている（`711ef87`）。
2. TSX を書く人から見た入口と key の規則が三通りある。
3. 凍結した Workbench backend のコード・test 190 件・CLI の分岐が残っている。
4. 計画が三本ある（`plan/` の S0〜S5、Notion のロードマップ 14 項目、BetterResoloop の B1〜B6）。対応表が無い。
5. 指示書のやり直しが多く、追跡文書が古い。
6. 実機で未検証の項目が増え続け、Integration test は実行していなくても成功として返る。

この change は、新しい機能を足す前に、上の 6 点を直す。

## What Changes

- **計画の正本を `openspec/` に集約する。** `plan/` は冒頭に案内を足して履歴にする。Notion は人間が確認するための写しにする。`assignments.md` は凍結し、進捗は change の `tasks.md` だけに書く。
- **B2c-1 を閉じる。** 未 merge の `agent/better-b2c`（`061b054`）と `711ef87` を、重いレビュー・修正・実機の確認・main への merge まで進める。
- **Workbench backend を除く。**（**BREAKING**）`src/RLoop.Workbench`、`tests/RLoop.Workbench.Tests`、CLI の backend の分岐、`wb` コマンド、設定を消す。古い指定は `BACKEND_REMOVED` で止める。
- **入口と key の規則を 1 つにする。** `resoloop-jsx` の型で key を省けるようにする。`--target creation` では省いてよく、通常の build では build の時点で `EXPLICIT_KEY_REQUIRED` で止める。`resoloop-jsx/draft` は互換の別名として残す。規則の説明は 1 つの表にまとめる。
- **通常の `apply` を凍結する。** 不具合と安全の修正だけを受け、新しい機能は足さない。`711ef87` は残す。公開している option と error code の一覧を test で固定する。所有の確認・保留・回復・state は、作り直しの経路と共通化しない。
- **未検証を閉じる決まりを作る。** Integration test は、実行しなかった場合に skip として報告する。未検証の一覧を `openspec/unverified.md` の 1 か所に置き、段階を閉じる条件に入れる。
- **進め方の規則を足す。** 指示書を出す前の確認、Codex の利用枠の見積もり、記録の置き場を `docs/dev/agent-workflow.md` に書く。
- **暫定だった 4 点を確定する。** 書き方は候補 3（名前と型の約束）、空間は Object ごとと制作物の root、親の変数は名前と型で指す、「親」は上へたどって変数を持つ一番近い Object。B3 はこの上に作る（B3 自体は後続の change）。

この change の範囲に入れないもの（後続の change。順序は `design.md` にある）: 止めない警告を構造化して返す基盤、B3（Object と親の変数）、入れ替えの順の追加（B2c-2）、作り直しの経路での Script・Asset・Flux（B4）、Object の単位の更新（B5）、作り直しを既定にする（B6）、Resonite → 定義の別の経路。

## Capabilities

### New Capabilities

- `plan-governance`: 計画・進捗・未検証の記録の正本と、段階を閉じる条件、指示書を出す前の確認。
- `backend-selection`: CLI が使う backend は直結（ResoniteLink）だけであること、除いた backend の指定の扱い。
- `tsx-authoring-entry`: TSX の import の入口、key を省ける条件、build の target ごとの規則。
- `ordinary-apply`: 差分で合わせる通常の `apply` の凍結した契約。
- `creation-regenerate`: 作り直しの経路の入口（TSX から出す定義）、入れ替えの順、通常の経路と共有する範囲。
- `integration-test-reporting`: 実機が要る test を実行しなかった場合の報告のしかた。

### Modified Capabilities

（なし。`openspec/specs/` はこの change が最初の内容になる。）

## Impact

- **コード**: `src/RLoop.Workbench/`（削除）、`src/RLoop.Cli/Program.cs`・`ResoniteClientFactory.cs`・`BackendSupport.cs`・`RLoop.Cli.csproj`、`src/RLoop.Core/Configuration.cs`、`ResoLoop.slnx`、`tools/resoloop-jsx/src/`（型の入口、`draft.ts`）、`tests/RLoop.IntegrationTests/`、`tests/RLoop.Workbench.Tests/`（削除）、`tests/RLoop.Tests/`（追加と、Workbench を対象にした test の置き換え）。
- **公開の契約**: `--backend workbench`・`wb`・Workbench の pipe の指定が使えなくなる（BREAKING）。TSX の主の入口で key が型の上では省けるようになる（通常の build は build の時点で止める）。通常の `apply` の挙動は変えない。
- **文書**: `README.md`、`README_JA.md`、`README-DETAILS.md`、`tools/resoloop-jsx/README.md`、`skills/codex/*/SKILL.md`（製品の一部）、`AGENTS.md`、`CLAUDE.md`、`docs/dev/agent-workflow.md`、`docs/dev/fork-strategy.md`、`plan/README.md`、`plan/resoloop-slimming-plan.md`。
- **記録**: `.ccg/tasks/resoloop-slimming/assignments.md`・`task.json`、Notion「現在地と引き継ぎ」「ロードマップ（実装計画）」。
- **外部**: 別 repo の resonite-workbench には触れない。upstream（orange3134/resoloop）は取り込まない。
- **承認が要る操作**: 実機への接続と書込み、main への merge、push、branch と worktree の削除。どれも task の中で止まって伺う。
