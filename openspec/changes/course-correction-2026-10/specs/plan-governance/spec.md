# Spec Delta

## Purpose

計画・進捗・未検証の記録をどこに置き、段階をいつ閉じてよいかを定める。計画が複数の場所に分かれて食い違うことと、未検証の項目が閉じられないまま積み上がることを防ぐ。

## ADDED Requirements

### Requirement: 計画の正本は openspec に置く
計画・段階・完了条件の正本は、repo の `openspec/` SHALL とする。`plan/` の文書は履歴として残し、冒頭で正本の場所を案内しなければならない（MUST）。

#### Scenario: 新しい作業を始める
- **WHEN** 開発エージェントが、どの change にも載っていない製品コードの作業を始めようとする
- **THEN** 先に `openspec/changes/` に change を作り、ユーザーの承認を得てから着手する

#### Scenario: plan の文書を開く
- **WHEN** 読む人が `plan/README.md` または `plan/resoloop-slimming-plan.md` を開く
- **THEN** 冒頭の案内から、正本が `openspec/` へ移ったことと、この文書が履歴であることが分かる

### Requirement: 全体の順序は 1 つの表で示す
`openspec/roadmap.md` は、`plan/` の S0〜S5、Notion のロードマップの 14 項目、BetterResoloop の B1〜B6 の対応と、それぞれの状態・根拠・担当する change を 1 つの表で示さなければならない（MUST）。

#### Scenario: ある項目の状態を調べる
- **WHEN** 読む人が「Workbench の除去」の状態を調べる
- **THEN** `openspec/roadmap.md` の 1 行から、旧計画の番号（S5、項目 14）、状態、根拠の commit または文書、担当する change の名前が分かる

### Requirement: 進捗は tasks.md だけに書く
進行中の作業の進捗は、その change の `tasks.md` のチェックだけに記録 SHALL する。別の追跡表へ同じ進捗を書いてはならない（MUST NOT）。

#### Scenario: 作業を 1 つ終える
- **WHEN** 担当が task を 1 つ終え、その task に書かれた確かめ方が通る
- **THEN** `tasks.md` の該当のチェックを付け、`assignments.md` には書かない

#### Scenario: 古い追跡表を開く
- **WHEN** 読む人が `.ccg/tasks/resoloop-slimming/assignments.md` を開く
- **THEN** 末尾の案内から、2026-10-13 より後の進捗が `openspec/` にあることが分かる

### Requirement: Notion は人間が確認するための写しとする
Notion のページは、`openspec/` の内容を人間が確認するための写し SHALL とする。食い違う場合は `openspec/` を優先しなければならない（MUST）。

#### Scenario: 記述が食い違う
- **WHEN** Notion の「現在地と引き継ぎ」と `openspec/` の `tasks.md` で、ある作業の状態が違う
- **THEN** 開発エージェントは `openspec/` の状態に従い、Notion を直す

### Requirement: 未検証の項目は 1 か所に置く
成功として扱えない未検証の項目は、`openspec/unverified.md` の表に置かなければならない（MUST）。各行は、ID、内容、生じた commit または change、確かめ方、確かめるのに要る条件、状態（未検証・確認済み・免除）を持つ。

#### Scenario: 実機で確かめていない機能を受け入れる
- **WHEN** 担当が、offline の検証だけで機能を受け入れる
- **THEN** 実機で確かめていない点を `openspec/unverified.md` に 1 行ずつ足す

#### Scenario: 確認が済む
- **WHEN** 実機の確認が済み、対象の session・Slot・証拠の場所が報告に書かれている
- **THEN** 該当の行の状態を「確認済み」にし、証拠の場所を書く

### Requirement: 未検証を残したまま change を閉じない
change を archive する前に、その change が生んだ未検証の項目は、すべて「確認済み」または「免除」でなければならない（MUST）。「免除」には、ユーザーの言葉と日付を書く。

#### Scenario: 未検証が残っている
- **WHEN** change の task がすべて済んだが、その change が生んだ項目に「未検証」が残っている
- **THEN** archive せず、残りの項目と、確かめるのに要る条件をユーザーに示す

#### Scenario: 実機では作れない場合
- **WHEN** 実機で作れない止まり方（例: 応答だけが失われる）の項目について、ユーザーが免除すると答える
- **THEN** その行を「免除」にし、ユーザーの言葉と日付、代わりにした offline の検証を書く

### Requirement: 指示書は出す前に確かめる
担当へ渡す指示書に書いたコマンドは、出す前に対象の worktree で実行し、結果を指示書に記録しなければならない（MUST）。変えてよい test と、変えてはならない test は、ファイル名で列挙する。

#### Scenario: 存在しないコマンドの形を書いた
- **WHEN** 指示書に書いたコマンドが、対象の worktree で使い方の誤りとして終わる
- **THEN** その指示書は出さず、コマンドを直してから出す

#### Scenario: 安全の規則に触れる変更
- **WHEN** 指示書が、書込みの停止・保留・所有の確認の挙動を変える
- **THEN** その挙動を確かめている既存の test をファイル名で挙げ、「通り続けること」と書く

### Requirement: 管理役は利用枠を見積もってから起動する
Codex の管理役を起動する前に、利用枠の窓の開始時刻、使用済みの量、今回の見積もりを記録しなければならない（MUST）。見積もりが残りを超える場合は起動しない。

#### Scenario: 残りが足りない
- **WHEN** 窓の残りの見積もりが 15 万 token で、起動しようとする重いレビューの見積もりが 22 万 token である
- **THEN** 起動せず、次の窓が開く時刻を記録する

#### Scenario: 容量切れで止まった
- **WHEN** 管理役が「Selected model is at capacity」で止まる
- **THEN** 指示書を書き換えず、同じ指示書で後から起動し直す

### Requirement: 元に戻しにくい操作はユーザーの承認の後に行う
実機への接続と書込み、main への merge、push、branch と worktree の削除は、ユーザーの明示の承認の後に行わなければならない（MUST）。以前の承認や、計画の承認を、これらの承認として扱ってはならない（MUST NOT）。

#### Scenario: 計画が承認された
- **WHEN** ユーザーが、この change の計画を承認する
- **THEN** 開発エージェントは、実機への書込みと main への merge を、それぞれの task の所で改めて伺う
