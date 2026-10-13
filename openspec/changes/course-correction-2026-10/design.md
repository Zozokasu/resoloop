# Design

## Context

動機は `proposal.md` の Why を見る。ここには、進め方を縛る現在の状態だけを書く（2026-10-13 21 時の時点）。

- main は `03479cf`。origin/main より 31 commit 先で、未 push。
- 未 merge の branch は `agent/better-b2c`（`061b054`、slot3。B2c-1）。offline の受け入れは済み、重いレビューと実機は未実施。指示書は `.ccg/tasks/resoloop-slimming/better-impl-b2c-review-charter.md` にある。
- main の作業ツリーに、ユーザーの未 commit の変更がある（`.claude/agents/feature-manager.md`、`AGENTS.md`、`CLAUDE.md`、`docs/dev/agent-workflow.md`、`scripts/agents/ccg-go-roles.mjs`、未追跡の `.claude/agents/coding-worker.md` と `.vscode/`）。
- `apply` は二系統ある。通常（state v3）は `src/RLoop.Core/WorldService*.cs`・`ApplyWorkflow.cs` など、作り直し（state v4）は `src/RLoop.Core/Creation*.cs`。共有は、client、session の lock、session の証明、定義の検査だけ。
- TSX の key の規則は三通りある（通常は必須、`--draft` は生成するが apply 不可、`--target creation` は省けるが `resoloop-jsx/draft` からの import が要る）。
- Workbench は、`src/RLoop.Workbench`、`tests/RLoop.Workbench.Tests`、`src/RLoop.Cli` の分岐、`src/RLoop.Core/Configuration.cs` の設定に残る。
- 体制と制約: 管理役は Codex `gpt-6.1-sol` high、実装と修正は `gpt-6-luna` high。Codex の利用枠は約 5 時間の窓で、50〜68 万 token で止まる。Codex の sandbox からは実機へつなげない。重いレビューは段階ごとに 1 回。`agent/*` への commit は伺わずに行い、main への merge と push は承認の後。実機は明示の許可の後で、書込みは `ResoLoop_Test*` の下だけ。

## Goals / Non-Goals

**Goals:**

- 計画・進捗・未検証の記録を `openspec/` の 1 か所で追えるようにする。
- B2c-1 と `711ef87` を、レビュー・実機・merge まで閉じる。
- 通常の `apply` に新しい機能が入らない状態を、文書と test の両方で作る。
- Workbench のコード・test・設定・文書を除く。
- TSX の入口を `resoloop-jsx` の 1 つにし、key の規則を 1 つの表で説明する。
- 実行していない test が成功として報告されないようにする。

**Non-Goals:**

- 新しい制作の機能（B3 の Object と親の変数、Script・Asset・Flux の作り直し、Object の単位の更新）。
- 入れ替えの順の追加（無効な新しい root を先に作る順）。
- 二系統の内部（所有の確認、保留、回復、state）の共通化。通常の `apply` の除去。
- `handle`・`ref.export` の変更、型付きの参照の制限の文書、単位 21（保留のまま `openspec/roadmap.md` に載せる）。
- upstream の取り込み、別 repo（resonite-workbench）の変更。
- ccg-workflow の hook と管理ファイルの仕組みの変更。`.claude/settings*.json` の変更。

## Decisions

「決めた人」がユーザーのものは、2026-10-13 の回答による。

### 1. 計画の正本は openspec に置く（ユーザー）

- `openspec/roadmap.md` に全体の順序と対応表、`openspec/unverified.md` に未検証の一覧、`openspec/changes/<名前>/` に進行中の計画を置く。
- 指示書・報告・log の作業ファイルは、これまでどおり `.ccg/tasks/resoloop-slimming/` に置き、名前の先頭を `cc-` にする。`tasks.md` から場所を指す。
- 引き継ぎは `openspec/changes/course-correction-2026-10/handoff.md` の 1 ファイルにする。群を 1 つ終えるたびに、日付の見出しの節を先頭へ足す。
- `plan/README.md` と `plan/resoloop-slimming-plan.md` は、冒頭に案内を足して履歴にする（ユーザー）。`plan/` は git 管理外なので、この編集は commit に入らない。
- Notion は写しにする。群を 1 つ終えるたびに「現在地と引き継ぎ」を直す。
- 退けた案: `plan/` に新しい正式計画を置く（私の当初の推奨。ユーザーが OpenSpec を選んだ）。

### 2. 1 つの change に 7 つの群を置き、群の順に進める

順序は次のとおり。群 3〜6 は、どれも CLI の help と README に触れるので、並べずに直列で進める。

| 群 | 内容 | 場所 | 重いレビュー |
| --- | --- | --- | --- |
| 1 | 記録を揃える | main の作業ツリー（製品コードなし） | なし |
| 2 | B2c-1 を閉じる | slot3 `agent/better-b2c` | 1 回（用意済みの指示書） |
| 3 | 通常の `apply` を凍結する | slot3 `agent/course-correction` | 群 7 でまとめて 1 回 |
| 4 | Workbench を除く | 同上 | 同上 |
| 5 | 入口と key の規則を 1 つにする | 同上 | 同上 |
| 6 | Integration test の skip | 同上 | 同上 |
| 7 | まとめのレビュー・実機・merge・片づけ | — | 1 回 |

- 群 3 を群 4 の前に置く。通常の `apply` の一覧を先に固定しておくと、Workbench の分岐を消す作業が通常の経路の挙動を変えた場合に、test で分かる。群 3 は文書と test 1 本だけなので、Workbench の除去は B2c-1 の merge の後すぐに始まる（ユーザーの「B2c-1 の merge の直後」に沿う）。
- 群 3〜6 は 1 本の branch `agent/course-correction`（群 2 の merge の後の main から切る）に、群ごとの commit で積む。レビューと merge は 1 回にまとめる。
- 退けた案: 群ごとに change を分ける（計画の文書が 5 組になり、ユーザーが読む量が増える）。群ごとに merge する（レビューの回数が増え、利用枠を使う）。

### 3. 通常の apply は凍結し、内部は共通化しない（ユーザー、codex と一致）

- 凍結の対象: `--mode regenerate` を付けない `apply`・`plan`・`diff`。受ける変更は、不具合と安全の修正だけ。
- `711ef87`（同じ session の中だけ、記録した ID で見分ける）は残す。戻しても作り直しの経路は簡単にならず、既存の制作物が再び止まる。
- 凍結は 3 つで支える。(a) `AGENTS.md` の「凍結した経路」の節、(b) option と error code の一覧を固定したファイルと実装を比べる test、(c) `docs/dev/agent-workflow.md` の規則（凍結したファイルに触れる指示書は「凍結の例外」と明記し、ユーザーの承認を得る）。
- 凍結は開発の決まりであり、利用者への案内ではない。作り直しの経路は実験的で、Asset・Flux・Script をまだ扱えないので、help・README・配布する Skill で「新しい制作は作り直しを使う」とは書かない。通常の `apply` の help と挙動は変えない。
- 固定する test のしくみ: error code は、凍結したファイルの中の `APPLY_`・`STABLE_` で始まる文字列リテラルを集めて、固定したファイルと比べる（error code は定数のクラスに集まっておらず、リテラルで散らばっているため）。option は、`src/RLoop.Cli/Program.cs` の、通常の `apply`・`plan`・`diff` の option を読む 3 か所を目印のコメントで囲み、その中の option 名のリテラルを集めて比べる（option は表で宣言されておらず、引数を直接見ているため）。
- 凍結したファイル: `src/RLoop.Core/WorldService.cs`、`WorldService.ApplyPending.cs`、`WorldService.ApplySafety.cs`、`WorldService.ApplyDeletion.cs`、`ApplyPendingDiscard.cs`、`ApplyWorkflow.cs`。`ApplyDocumentCompiler.cs` と定義の検査は作り直しと共有しているので、凍結の対象にしない。
- 共有するもの: client の抽象、session の lock、session の証明、定義の検査。分けたままにするもの: 所有の確認（通常は Slot・Component ごとの記録、作り直しは root の印）、保留の記録、回復、state の保存。無理に 1 つにすると、片方の修正がもう片方の安全の前提を壊す。
- 退けた案: 作り直しが既定になった時点で除くと今決める（Asset・Flux・Script が作り直しで扱えるまで、移行の範囲が分からない）。両方を育てる。

### 4. Workbench は群 2 の merge の直後に除く（ユーザー）

- 消すもの: `src/RLoop.Workbench/`、`tests/RLoop.Workbench.Tests/`、`ResoLoop.slnx` の 2 行、`RLoop.Cli.csproj` の `ProjectReference` と `InternalsVisibleTo`、`src/RLoop.Cli/BackendSupport.cs`、`Program.cs` の backend の分岐・`RunWorkbench`・help の該当行、`ResoniteClientFactory.cs` の分岐、`Configuration.cs` の `WorkbenchPipe`、`scripts/dev/workbench.ps1`、`scripts/agents/verify.ps1` の Workbench の test の行。
- 古い指定の扱い: backend の値 `workbench`、`wb` コマンド、option `--workbench-pipe` は、error code `BACKEND_REMOVED`・終了 code 2（`INVALID_BACKEND` と同じ `InvalidArguments`）で、接続の前に止める。環境変数 `RESOLOOP_WORKBENCH_PIPE` と設定 `workbenchPipe` は読まない（backend が link のとき、いままでも使っていなかった）。`BACKEND_UNSUPPORTED` は無くなる。
- 履歴の文書（`docs/dev/workbench-read-mapping.md`、`workbench-w1-w3-handoff.md`、`workbench-w2-handoff.md`、`slimming-s0-s1-handoff.md`、`slimming-s2-handoff.md`）は消さず、冒頭に「Workbench backend は course-correction-2026-10 で除いた。この文書は履歴」の 1 行を足す。`src/RLoop.Core/FluxDeployGuard.Checks.cs` と `ResoniteLinkClientAdapter.cs` の由来のコメントは変えない。
- 除く前の main に、手元の tag `pre-workbench-removal` を付ける。
- 退けた案: コードを残して検証から外すだけ。ロードマップどおり最後に除く。古い指定を黙って link として扱う（意味が変わったことに気づけない）。

### 5. 入口は resoloop-jsx の 1 つにする（ユーザー）

- `tools/resoloop-jsx/src/elements.ts` の `SlotProps` と `ComponentProps` の `key` を省略可能にする。`src/draft.ts` は、型の差し替えをやめ、主の入口と同じものを再 export するだけにする。
- key を省いてよいかは、build の評価が決める（`src/evaluate.ts` の `resolveKey`）。規則は変えない。通常は `EXPLICIT_KEY_REQUIRED`、`--draft` と `--target creation` は生成する。生成の規則も変えない（親の key、名前の slug、同じ名前の兄弟の中での順番から決まる）。`<Scope>` の `instanceKey` は、どの target でも必須のまま。
- 失うもの: 通常の build で、key の書き忘れを TypeScript の型検査が先に見つける働き。代わりに build が、ファイル・行・列つきの `EXPLICIT_KEY_REQUIRED` で止める。通常の経路は凍結するので、この引き換えを受け入れる。
- 既存の test `tools/resoloop-jsx/test/cli.test.ts` の「missing-key-normal-entry.tsx exits 2 with TypeScript diagnostics」は、`EXPLICIT_KEY_REQUIRED` を確かめる形へ書き換える。書き換えてよい既存の test は、この 1 件だけ。
- 規則の表（`tools/resoloop-jsx/README.md` に置く）:

| 使い方 | import | build | apply | key |
| --- | --- | --- | --- | --- |
| 作り直し（実験的） | `resoloop-jsx` | `build entry.tsx -o out.json --target creation` | `apply out.json --mode regenerate --replace-order delete-first --yes --url auto --session S-ID` | 省ける。Scope の `instanceKey` は必須 |
| 通常 | `resoloop-jsx` | `build entry.tsx -o out.json` | `apply out.json` | 全部に必須。無ければ `EXPLICIT_KEY_REQUIRED` |
| 下書き（形の確認だけ） | `resoloop-jsx` | `build entry.tsx -o out.json --draft` | できない（`APPLY_DRAFT_KEY_UNSTABLE`） | 省ける |

- 退けた案: creation 専用の import を足す（入口が 2 つ残る）。説明をまとめるだけ。

### 6. 暫定だった 4 点を確定する（ユーザー）

| 点 | 確定した内容 |
| --- | --- |
| 書き方（D1） | 候補 3。変数は名前と型を明示して宣言し、片方向は `drive`、双方向は `bind`。親の変数は名前と型の約束で引く |
| 空間の置き方 | 読み A。各 Object と制作物の root が空間を持つ。共有する状態は共通の親に置く |
| 親の変数の指し方 | 親の名前と型で指す（`parent("powered", types.bool)` の形）。無ければ警告 `CREATION_PARENT_VARIABLE_MISSING`、終了 code 0、配線を出さず代わりの値を書く |
| 「親」の範囲 | 上へたどって、その名前の変数を持つ一番近い Object。無ければ制作物の root |

この change では記録を直すだけにする（`.ccg/tasks/resoloop-slimming/better-design-v2/design.md` の §2・§3・§14 の「暫定」を「確定（2026-10-13）」へ、Notion の設計メモへ 1 節）。実装は後続の B3 の change で行う。

### 7. 入れ替えの順は delete-first だけにする

`apply --mode regenerate` は、いまの実装のとおり `delete-first` だけを受ける。無効な新しい root を先に作る順は、無効な Slot の下の ProtoFlux を実機で確かめた後に、別の change（B2c-2）で足す。決めたのは Opus で、codex の推奨と一致した。ユーザーは B2c-2 について「後で判断します」（2026-10-13 13:36）と答えており、この change はその判断を先取りしない。

### 8. 実行しなかった実機の test は、属性で skip にする

xunit は 2.9.3。既存の `InstalledFluxLibrariesFactAttribute`（`tests/RLoop.Tests/FluxSdkElementIdTests.cs`）と同じやり方で、`FactAttribute` を継ぐ属性が、条件を満たさないとき `Skip` に理由を入れる。対象は `tests/RLoop.IntegrationTests` の 14 ファイル・21 か所の早期 return。追加の条件（Blender の場所など）を持つ test は、その条件も属性へ移す。`SessionLockIsolationTests.cs` の 2 件は条件なしのまま。

### 9. 未検証は 2 回の実機のまとまりで閉じる

- 1 回目（群 2）: B2c-1 と `711ef87`（下の表の U01〜U05）。
- 2 回目（群 7）: 残りのうち、実機で作れるもの（U07、U08、U10、U15、U16）。
- 実機で作れないもの（U06、U14）は、offline の test を根拠に、免除してよいかをユーザーに伺う。
- 「実機テストは段階の終わりにまとめる」という従来の進め方は変えない。変えるのは、まとめを実行するか免除を得るまで、change を閉じないこと。

### 10. 進め方の規則を docs/dev/agent-workflow.md に足す

`specs/plan-governance/spec.md` の「指示書は出す前に確かめる」「管理役は利用枠を見積もってから起動する」を、手順として書く。見積もりの目安は、これまでの実績から、設計 35 万、実装の 1 段階 25 万、重いレビュー 22 万、修正 1 回 19 万 token とする。1 つの窓の予算は 50 万 token とし、管理役を並べない。

### 11. ユーザーの未 commit の 5 ファイルは、先に扱いを伺う

群 1 と群 4 は `AGENTS.md`・`CLAUDE.md`・`docs/dev/agent-workflow.md`・`.claude/agents/feature-manager.md`・`scripts/agents/ccg-go-roles.mjs` に触れる。これらにはユーザーの未 commit の変更がある。群 1 の最初に、ユーザーの変更を main へ commit してよいかを伺う。

- 承認された場合: ユーザーの変更だけを 1 つの commit にし、その後の編集は branch で行う。
- 承認されない場合: この 5 ファイルの編集は、main の作業ツリーで行って commit しない。branch では、この 5 ファイルに触れない。

### 12. push は最後に伺う

「まだ push しない」という決定は変えない。群 7 の最後に、push するかを伺う。

## 対応表（openspec/roadmap.md の最初の内容）

状態は 2026-10-13 の時点。「項目」は Notion のロードマップの番号。

| ID | 内容 | plan/ | 項目 | Better | 状態 | 根拠 | 担当する change |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R01 | TSX・JSON の宣言を反映する | S0・S1 | 1 | — | 完了 | main `69a386c` | — |
| R02 | 競合の検出・失敗の照合・削除 | S3 | 2 | — | 完了（故障の場合は未検証 U14〜U17） | main `94c12ed` | course-correction-2026-10（未検証だけ） |
| R03 | 型情報で TSX を検査する | S2 | 3 | — | 完了（代表例） | `5edafc0`、`c9fd625` | — |
| R04 | Slot・Component・Field を ID で書く | — | 4 | — | 完了（代表例） | `5edafc0` | — |
| R05 | Object を定義して使う | S4-3 | 5 | — | 実験（単位 01〜20）。単位 21 と型付きの参照の文書は保留。`handle`・`ref.export` は R13 で置き換える見込み | `2993dab` | b3-objects-parent-variables |
| R06 | ProtoGraph の配置を apply と同じ確認の下で行う | S4-1・S4-2 | 9 | — | 完了（代表例） | `5edafc0` | — |
| R07 | 変数と Field をつなぐ | S4-4 | 8 | B1 | 実験（bool だけ） | `e00ce41` | b3-objects-parent-variables |
| R08 | Slot の有効・無効を書く | — | — | G1 | 完了 | `e00ce41` | — |
| R09 | root を作り直す apply と回復 | — | — | B2a・B2b | 実験（手書きの JSON、delete-first） | `e00ce41` | — |
| R10 | 同じ session の ID での照合 | — | — | — | merge 済み、実機は未検証（U01） | `03479cf` | course-correction-2026-10 |
| R11 | TSX から作り直しの定義を出す | — | — | B2c-1 | 受け入れ済み、未 merge | `061b054` | course-correction-2026-10 |
| R12 | Workbench なしで制作の経路をそろえる | S5 | 14 | — | 未着手 | — | course-correction-2026-10 |
| R13 | Object と親の変数、入れ子、配置口 | — | 6 | B3 | 未着手（4 点は確定） | 本書の決定 6 | b3-objects-parent-variables |
| R14 | 止めない警告を構造化して返す | — | — | B3 の前提 | 未着手 | — | structured-warnings |
| R15 | 無効な新しい root を先に作る入れ替えの順 | — | — | B2c-2 | 待ち（U08 の後） | 本書の決定 7 | stage-inactive-replace-order |
| R16 | 作り直しで Script・Asset・Flux を扱う | — | 7・10 | B4 | 未着手 | — | creation-script-asset-flux |
| R17 | UIX を部品で組み立てる | — | 11 | — | 未着手 | — | 未定 |
| R18 | Object の単位で更新する | — | 12 | B5 | 未着手 | — | object-scoped-regenerate |
| R19 | 作り直しを既定にする。通常の apply の扱いを決め直す | — | — | B6 | 未着手 | — | regenerate-default |
| R20 | 1 つの作品を通しで作り、編集を続ける | S4 の出口 | 13 | — | 未着手 | — | 未定 |
| R21 | Resonite → 定義の別の経路 | — | — | — | 形は未定（`/ccg:spec-research` から） | ユーザーの見解 2026-10-13 | 未定 |
| R22 | 自動で足される Component を Reflection などから引く | — | — | — | 時期は未定 | ユーザーの指示 2026-10-12 | 未定 |
| R23 | `--url auto` の探索（1 回に約 12 秒）を速くする | — | — | — | 未着手 | `better-handoff-2026-10-13.md` §7 | 未定 |

後続の change は、R14、R13、R15、R16、R18、R19 の順に進める。R15 は U08 が済んだ時点で、順を前へ動かしてよい。

## 未検証の一覧（openspec/unverified.md の最初の内容）

| ID | 内容 | 生じた所 | 確かめ方 | 要る条件 | 状態 |
| --- | --- | --- | --- | --- | --- |
| U01 | 同じ session の ID での照合 | `711ef87` | 実機: 同じ Slot に同じ型の Component 2 つ → 同じ session で再 apply は成功、`--url ws://…` では `STABLE_COMPONENT_AMBIGUOUS` | 実機の許可 | 未検証 |
| U02 | TSX から build した定義での初回の作成と置き換え | `061b054` | 実機 | 実機の許可 | 未検証 |
| U03 | key を省いた定義の作り直し | `061b054` | 実機 | 実機の許可 | 未検証 |
| U04 | 同じ名前の兄弟を含む定義の作り直し | `061b054` | 実機 | 実機の許可 | 未検証 |
| U05 | root と同じ名前の Slot が親の下にある場合 | `061b054` | 実機 | 実機の許可 | 未検証 |
| U06 | 回復の `finish` の成功 | `d69a7a5` | 実機では、木が完成した後で止まる状況を作れない。offline の test を根拠にする | ユーザーの免除 | 未検証 |
| U07 | 所有の印つきの root を複製した場合 | `d69a7a5` | 実機: root を複製してから作り直し、どちらも消されずに止まること | 実機の許可、ユーザーによる複製の操作 | 未検証 |
| U08 | 無効な Slot の下の ProtoFlux | `live-1013-l2b-report.md` | 実機: 無効な親の下へ Flux を配置し、有効にした後に動くこと | 実機の許可、`RESONITE_MANAGED_DATA_PATH` の設定 | 未検証 |
| U09 | root を別の親へ移す形 | 同上 | CLI に手段が無い。R15 の change で扱うかを決める | — | 保留 |
| U10 | world の再読込み・Resonite の再起動の後の ID と結びつき（P8） | `spike-dv-p2-report.md` | 実機: 作成 → world の保存 → 再起動 → 作り直し | 実機の許可、ユーザーによる保存と再起動 | 未検証 |
| U13 | 不安定 test `S3RealProcessKillReleasesHandleButPreviousPendingStillBlocks` | 2026-10-05、10-08 | 再発したら、失敗時の記録（`LockFileDiagnostics`）を読む。自動の retry で隠さない | 再発 | 観察中 |
| U14 | S3: 応答だけが失われた場合 | `94c12ed` | 実機では作れない。offline の偽 client の test を根拠にする | ユーザーの免除 | 未検証 |
| U15 | S3: 実行中の取消 | `94c12ed` | 実機: 大きい定義の apply の途中で CLI を止め、次の apply が保留で止まること | 実機の許可 | 未検証 |
| U16 | S3: 切断 | `94c12ed` | 実機: apply の途中で Resonite 側の接続を切り、次の apply が保留で止まること | 実機の許可、ユーザーの操作 | 未検証 |
| U17 | S3 の出口で残った項目（W04 の回復、drift、alias、`Children=null`） | `s3-exit-handoff.md` §5 | 群 7 で 1 件ずつ、実機で作れるか・offline で足りるかを分け、行を分割する | — | 未検証 |
| U18 | skip 4 件（Flux のライブラリの場所が未設定など） | — | `RESOLOOP_TEST_FLUX_LIBRARY_PATH` を設定して実行 | 環境の設定 | 未検証 |
| U20 | 単位 21（正式な例）と、Toggle を含む実機の確認 | `roadmap-5-exit-handoff.md` | R13 の change で扱う | — | 保留 |
| U21 | 値つきの DynamicImpulse の受信、ボタンを実際に押す操作、複数の人がいる session | `spike-dv-p2-report.md` | R13・R16 の change で扱う | — | 保留 |
| U22 | 採用の観測が null の偽 client での `NullReferenceException`、`recover` の取消の分類の test が無い、初回に何も送る前に止まった場合の `rollback` | `better-handoff-2026-10-13.md` §7 | 小さい修正。R14 の change で扱う | — | 保留 |

`b6c3193` 以降の新しい clone での確認と、JSX の contract の検査の再実行は、`061b054` と `03479cf` で済んでいる（`slot3 TestResults/BETTER-B2C/opus/`）ので、一覧に入れない。

## PBT の性質

各性質は、右端の群で test にする。「既存」は、すでにある test を確かめるだけで足りるもの。

| ID | 不変の性質 | 種類 | 崩し方（生成する入力） | 群 |
| --- | --- | --- | --- | --- |
| P01 | 同じ TSX は、import 元が `resoloop-jsx` でも `resoloop-jsx/draft` でも、同じ option で byte 単位に同じ出力になる | round-trip | Slot の木（深さ 1〜4、兄弟 0〜5、同じ名前を混ぜる、key の有無を混ぜる）を生成し、import の行だけを変えて 2 回 build | 5 |
| P02 | 同じ入力の build は、何度でも byte 単位に同じ出力になる | idempotency | P01 の木を、作業 directory を変えて 2 回 build | 5 |
| P03 | `--target creation` で生成した識別子は、1 つの定義の中で重複しない | invariant | 同じ名前の兄弟を 2〜50 個、空の名前、slug が同じになる名前（大文字小文字、記号だけが違う）を混ぜる | 5 |
| P04 | target を付けない build は、key の無い要素が 1 つでもあれば `EXPLICIT_KEY_REQUIRED` で止まり、出力を書かない | bounds | 全部に key を付けた木から、無作為に 1 つだけ key を消す | 5 |
| P05 | `$draftKeys` を持つ定義は、どの option の組でも `validate`・`apply` に拒否される | invariant | `--mode regenerate`・`--yes`・`--adopt`・`--prune` などの組を生成 | 5（既存を確かめる） |
| P06 | backend の値が `workbench` なら、どのコマンドでも接続の前に `BACKEND_REMOVED`・終了 code 2 で止まる | invariant | コマンド名（全部）× 指定のしかた（option・環境変数・設定）を生成。偽の client が接続を 1 回でも受けたら失敗 | 4 |
| P07 | backend を省いた場合と `link` を指定した場合で、同じコマンドは同じ結果になる | commutativity | 読取りのコマンドを偽の client に対して両方の形で実行し、出力を比べる | 4 |
| P08 | 通常の `apply`・`plan` が受ける option と error code の集合は、固定したファイルと一致する | invariant | 実装から集めた集合と、固定したファイルの差が空であること（片側だけにある要素を報告） | 3 |
| P09 | 通常の `apply` は、同じ session の証明が無ければ、記録した ID で Component を決めない | invariant | session の記録（無し・別の session・途中の checkpoint・`--url` の直接指定）× 候補の数（2〜4）× ID の重複の有無 | 3（既存を確かめる） |
| P10 | 作り直しは、所有の印・親・session・世代のどれか 1 つでも記録と違えば、削除を 1 件も送らない | invariant | 各項目を 1 つずつ変える、印を複製する、観測を不完全にする | 2（既存を確かめる） |
| P11 | 作り直しを 2 回続けると、所有する有効な root はちょうど 1 つ残り、値は定義と一致する | idempotency | 偽の client で、値を変えた定義・名前の衝突・作成の応答の重複を生成 | 2（既存を確かめる） |
| P12 | `RESOLOOP_RUN_INTEGRATION` が `1` でなければ、実機の要る test の成功は 0 件で、skip は対象の件数と一致する | bounds | 環境変数を、未設定・空・`0`・`true`・`1` に変えて件数を数える | 6 |

P01〜P04 は `tools/resoloop-jsx/test/` に、生成器を自前で書いた test として置く（新しい依存は足さない。乱数の種は固定し、失敗したときに種と生成した木を出す）。P06〜P08 は `tests/RLoop.Tests/` に置く。

## Risks / Trade-offs

- [通常の build で key の書き忘れに気づくのが、型検査から build へ遅れる] → `EXPLICIT_KEY_REQUIRED` はファイル・行・列を返す。通常の経路は凍結するので、新しく書く人は作り直しの側を使う。
- [`--draft` と `--target creation` を取り違える] → 併用を build の前に拒否する（既存）。規則を 1 つの表にする。
- [Workbench を消すときに、共通の test まで消す] → `tests/RLoop.Tests` の 8 ファイルは、test ごとに「Workbench の backend の挙動を確かめる test は消す、直結の挙動を確かめる test は残す」で分ける。消した test と残した test の名前を、報告に列挙する。除く前と後で、直結の test の件数が減っていないことを確かめる。
- [古い設定が残った利用者のコマンドが止まる] → 黙って別の意味にせず、`BACKEND_REMOVED` で理由と直し方を返す。`README-DETAILS.md` に 1 か所だけ書く。
- [群 3〜6 が同じ help と README を触り、内容がずれる] → 直列に進め、群ごとに help・README・配布 Skill を同期してから次へ進む。
- [配布する Skill（`skills/codex/`）が CLI とずれる] → 群 4 と群 5 のそれぞれで、Skill の該当の行を直す task を持つ。contract の検査（既存）を群の終わりに実行する。
- [Codex の利用枠が、レビューの途中で尽きる] → 起動の前に見積もる（決定 10）。重いレビューは 2 回だけ。
- [実機の確認にユーザーの操作が要り、change が閉じない] → 実機で作れないものは免除を伺う。ユーザーの操作が要る項目は、1 回の session にまとめる。
- [ユーザーの未 commit の変更と、文書の編集がぶつかる] → 決定 11。

## Migration Plan

1. 群 2 の merge の後、main に手元の tag `pre-workbench-removal` を付ける。
2. 群 3〜6 は `agent/course-correction` で進め、群 7 で 1 回だけ main へ `--no-ff` で merge する。
3. 戻す場合は、merge の commit を `git revert -m 1` で戻す。Workbench が要る場合は、tag から branch を切る。
4. 利用者への影響: `--backend workbench`、`wb`、`--workbench-pipe` を使っていた場合は、指定を消す。TSX の import は変えなくてよい（`resoloop-jsx/draft` は残る）。state と定義の文書の移行は無い。

## 後続の change

この change の外。名前は予定で、それぞれ `/ccg:spec-research` から始める。

1. `structured-warnings`: 止めない警告を、build と validate の結果に構造化して返す。U22 の小さい修正を含める。
2. `b3-objects-parent-variables`: 決定 6 の上に、Object と親の変数、入れ子、配置口。`handle`・`ref.export` の置き換えと、単位 21 をここで扱う。
3. `stage-inactive-replace-order`: U08 が済んだ後。U09 を扱うかも、ここで決める。
4. `creation-script-asset-flux`: 作り直しで Script・Asset・Flux を扱う。
5. `object-scoped-regenerate`: Object の単位の更新。
6. `regenerate-default`: 作り直しを既定にし、通常の `apply` の扱いを決め直す。
