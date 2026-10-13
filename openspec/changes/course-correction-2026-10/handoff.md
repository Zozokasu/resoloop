# Handoff

群を 1 つ終えるたびに、日付の見出しの節を先頭へ足す。進捗は `tasks.md` のチェックだけに書き、ここには写さない。全体の順序は `openspec/roadmap.md`、未検証の一覧は `openspec/unverified.md` にある。

## 2026-10-13 21:35 JST — 群 2（B2c-1 を閉じる）の途中

task 2.1 と 2.2 だけを終えた。2.3（重いレビューの起動）はユーザーに伺う前で、レビューは起動していない。

### Codex の利用枠（task 2.1）

- 前の窓: 16:35 に開いた。使用済みは約 58 万 token（15.8 万 + 24.3 万 + 13.0 万 + 5.0 万）。予算 50 万を超えているので、この窓では起動しない。
- 次の窓: 21:35 ごろに開く見込み。開いた後の使用済みは 0、重いレビューの見積もりは 22 万 token で、残り（50 万）に収まる。
- 判断: 21:35 より後で、ユーザーが 2.3 を承認した後に起動する。起動した時刻を、新しい窓の開始時刻として、ここへ書き足す。同じ窓に入るのは、レビュー（22 万）と修正 1 回（19 万）まで（計 41 万）。修正が 2 回になる場合は、次の窓を待つ。
- **2.3 の返事**（2026-10-13 21:33 ごろ、原文）:「200kくらいまでならつかっていいです。レビューを始めて下さい。」重いレビューの起動を承認。上限は約 20 万 token（見積もり 22 万より少ない）。
- **起動した**: 2026-10-13 21:34:52 JST（Codex の session `01a13542-301a-7e22-bf74-7fb2541a4129`。利用上限の拒否は出なかった）。新しい窓の開始は、この時刻とする。報告は `.ccg/tasks/resoloop-slimming/better-impl-b2c-review-report.md`、log は同 `better-impl-b2c-review-codex.log`、開始と終了の時刻は同 `cc-b2c-review-times.txt`。レビューの間、slot3 では build と test を実行しない（1 つの作業ツリーで build するのは 1 担当）。
- **起動の前の記録**（21:34）: 新しい窓の開始は、この起動の時刻。使用済み 0、見積もり 22 万、ユーザーの上限 約 20 万。指示書 `better-impl-b2c-review-charter.md` は書き換えず、上限を伝える前書き `cc-b2c-review-budget-note.md` を前に付けて渡す（重点の番号の順に見て、足りなくなったら打ち切って「見なかった範囲」を書く）。Codex は使用量を自分では正確に止められないので、上限を超えることがありうる。終了後に log の `tokens used` を、ここへ書く。
- **指示書の事前確認**（21:35、slot3。実機に触れない）: `git status --short` は未追跡の `examples/object-lamp-a/`・`-b/` だけ、HEAD は `061b054`。`npm.cmd run`（終了 code 0）で、指示書が使う `test` と `contract` の script があることを確かめた。`src/RLoop.Cli/obj` と `tools/resoloop-jsx/node_modules` はあり、restore は要らない。build と test のコマンドは、同じ形を 2026-10-13 午後の受け入れで実行済み（slot3 `TestResults/BETTER-B2C/opus/accept*.log`）。指示書の報告先 `TestResults/BETTER-B2C/review-final/` は、まだ無い。

### help と README の残り（task 2.2）

slot3（`agent/better-b2c`、`061b054`）で `rg -n -i "hand-?written|TSX" src/RLoop.Cli/Program.cs README.md README-DETAILS.md` を実行した（21:31）。

| 場所 | 内容 | 扱い |
| --- | --- | --- |
| `src/RLoop.Cli/Program.cs:1623` | `apply` の help:「It accepts hand-written JSON envelopes only (no TSX creation entrypoint)」 | 直す（2.4 の契約に入れる） |
| `src/RLoop.Cli/Program.cs:1634` | `plan` の help:「It accepts hand-written JSON envelopes only (no TSX entrypoint)」 | 直す |
| `src/RLoop.Cli/Program.cs:1649` | `creation recover` の help:「This workflow accepts hand-written JSON envelopes only; TSX creation, … are unavailable」 | 直す（TSX creation を「unavailable」の列から外す） |
| `src/RLoop.Cli/Program.cs:1785` | 全体の help:「Experimental creation regenerate uses handwritten JSON envelopes」 | 直す |
| `README.md:46`、`README-DETAILS.md:263` | TSX を `--target creation` で build する説明（B2c-1 で足した文） | 残す |
| `README.md:219` | 「handwritten runtime table」（Object の例の注意。作り直しとは無関係） | 残す |
| そのほかの一致（`README.md` 9 行、`README-DETAILS.md` 12 行） | 通常の TSX の build・bundle・診断・Scope の説明 | 残す |

- 直す 4 行は、B2c-1 で TSX の入口（`resoloop-jsx build --target creation`）が入った後も、古い文のまま残っている。README とは食い違っている。
- 同じ 4 行にある「live-unverified」「Live behavior is unverified」は、2.2 の検索の対象ではないので、扱いを決めていない。L1・L3 で確かめた範囲（`handoff` の下の節）と合わせて、2.4 の契約を書くときに、直すかをユーザーに示す。
- help を変えるので、2.4 では `AGENTS.md` の規則（help・README・配布する Skill の同期）に従い、配布する Skill の該当の文も確かめる。

## 2026-10-13 21:30 JST — 群 1（記録を揃える）を終えた

`better-handoff-2026-10-13.md`（12:20 JST）と `task.json` の内容のうち、いまも正しいものを移した。以後の引き継ぎは、この文書だけに書く。

### 仕組み

- **記録の置き場**（2026-10-13、ユーザーの決定）: 計画の正本は `openspec/`。進捗は `openspec/changes/<名前>/tasks.md`、順序は `openspec/roadmap.md`、未検証は `openspec/unverified.md`、引き継ぎはこの文書。指示書・報告・log は `.ccg/tasks/resoloop-slimming/` に、名前の先頭を `cc-` にして置く。`plan/`、`assignments.md`、`better-handoff-2026-10-13.md` は履歴。Notion は写し。
- **作り直しの経路**（`apply FILE --mode regenerate --replace-order delete-first`。実験的）: 定義（`creation-1`）から root ごと作り直す。所有は、root の `DynamicValueVariable<string>` の印 `_resoloop_creation_stamp` で確かめる。流れは「offline の検査 → lock → session の証明 → 型の準備 → 不在の判定 → 所有の確認 → 旧 root の削除 → 作成 → 検証 → 保存」。所有の確認から削除までの間に、実機の読取りを入れない。途中で止まったら `creation recover --action rollback|finish` か `--discard-pending`。
- **Resonite が自動で足す Component**（ユーザーの決定）: Slot の数は定義と一致、Component は定義以上で成功。定義した Component を Resonite が重複として消した場合は、足されていた方を採用して値を書く（持ち主の Slot を完全に観測でき、同じ型の候補がちょうど 1 つのときだけ）。
- **同じ session の ID での照合**（`711ef87`。ユーザーの決定「c で」）: 通常の `apply`／`plan` で、同じ型の候補が絞れないとき、(a) state を書いたのが同じ session で最後まで成功した `apply`、(b) 記録した ID が候補の中、(c) 同じ Slot で ID の重複なし、をすべて満たせば、その ID で決める。満たさなければ `STABLE_COMPONENT_AMBIGUOUS`。`--url ws://…` の直接指定では session を確かめられない（`--url auto` なら確かめられる）。
- **B2c-1**（`agent/better-b2c`、未 merge）: TSX を `build --target creation` で作り直しの定義にする入口、key を省いた定義、同じ名前の兄弟。いまは、key を省く TSX は `resoloop-jsx/draft` から import する必要があり、`--draft` とは併用できない（群 5 で入口を 1 つにする）。
- **進め方の注意**:
  - 安全の規則に触れる修正では、既存の停止を確かめている test（S3 系）を先に見る。
  - Codex の担当の報告の件数は sandbox の中の値。受け入れでは、Opus が sandbox の外で build と test を実行する（`LockFileDiagnosticsTests` の 2 件は、sandbox の中だけ失敗する）。Codex は worktree の中で commit できないので、Opus が commit する。
  - `node -e` に日本語や改行を含む script を直接渡すと、何も実行されずに終わる（2026-10-13 21:25 にも再現）。`.cjs` のファイルにして実行する。
  - 実機の検証は、Claude の `coding-worker` が行う（Codex の sandbox からは実機へつなげない）。書込みは一度に 1 担当。指示書の雛形は `live-1012-l3-charter.md`、`live-1013-l2b-charter.md`。
  - 定型の検証 `scripts/agents/verify.ps1` は、この環境に PowerShell 7 が無く、実行できなかった（2026-10-08）。`dotnet build`・`dotnet test`・`npm.cmd test` を直接実行している。
  - Notion の tool が無いときは、`.claude/settings.local.json` の `allowedMcpServers` を見る（設定は、ユーザーが言ったときだけ変える）。
  - B2c-1 では、Opus の指示書の誤りで管理役を 2 回起動し直した（変えてはならない既存の test の書き換えを指示した／存在しないコマンドの形 `validate --mode regenerate` を書いた）。対策は task 1.7 (c) の「指示書を出す前の確認」。

### 確認済みの事実

- **repo**（2026-10-13 21:30 JST）: main は、製品コードの最後の merge `03479cf` の上に、文書だけの commit が 2 つある（`7e8575c` = task 1.1 のユーザーの体制の文書、その次 = task 1.14 の `docs(openspec): move the plan of record to openspec`）。origin/main `bb5c4ec` より先で、未 push。worktree は slot1 `agent/lock-flake-diag`（`d9367f2`）、slot2 `agent/better-resoloop`（`31395b5`）、slot3 `agent/better-b2c`（`061b054`。変更元は `03479cf`）。branch は、ほかに未 merge の `agent/roadmap-5-proto`（`26e261e`）だけが残る。
- **2026-10-13 に main へ入ったもの**（どちらもユーザーの承認あり、`--no-ff`）: `e00ce41`（B1 の変数 API、G1 の `slot set/create --active`、B2a・B2b の `apply --mode regenerate` と `creation recover`、修正 1〜4。すべて実験的）、`03479cf`（`711ef87` の merge）。
- **main `03479cf` の offline**（sandbox の外。記録 `TestResults/merge-03479cf/`）: build 警告 0・エラー 0、RLoop.Tests 1984 pass／4 skip、Workbench 190、Integration（offline）2。
- **`agent/better-b2c` `061b054` の offline**（Opus が sandbox の外と、新しい clone で実行。log は slot3 `TestResults/BETTER-B2C/opus/accept*.log`、`fresh-*.log`）: build 警告 0・エラー 0、RLoop.Tests 1994 pass／4 skip、Workbench 190、Integration（offline）2、`npm test` 335 pass、contract の検査 64／11／4／2。commit は `01477f0`（jsx の入口と key、修正 1）、`a0bcb57`（同じ名前の兄弟）、`85751fe`（creation の contract の検査）、`061b054`（文書）。管理役の報告は slot3 `TestResults/BETTER-B2C/report.md`。
- **実機**（session「ぞかホーム」、Resonite `2026.9.18.82`、ResoniteLink `0.13.1.0`。すべて片づけ済みで、`find --name ResoLoop_Test` 0 件）:
  - L1（2026-10-12）: 作り直しの初回、置き換え、所有を確かめられないときの拒否、rollback、discard-pending、自動で足される Component の数。
  - L2 前半（2026-10-12）: G1、変数 API の片方向・双方向・再 `apply`、無効な親の下への `apply`。
  - L3（2026-10-13、build `b6c3193`）: 重複として消される `RectTransform` の採用、置き換え、`plan` の型の検査、期限切れの案内と `rollback`。
  - L2b（2026-10-13）: 変数と配線は、Slot が無効な間も動く。「古い側を無効 → 新しい側を有効 → 古い側を削除」の後も、何もせずに両方向で動く。同じ名前の空間どうしは混ざらない。同じ名前の Slot は、同じ親の下に 2 つ作れる。
  - Integration test を実機で実行したのは 2026-10-11 が最後（15 pass。画面 capture・Blender・texture を使う 6 クラスは除いた）。
  - 報告: `live-1012-b2b-report.md`、`live-1012-l2-report.md`、`live-1012-l3-report.md`、`live-1013-l2b-report.md`。証拠: `resoloop-worktrees/live-1012/evidence/`（L1、L2、L3、L2b）。
- **ユーザーの決定と見解**（2026-10-13）:
  - この change の決定 1・3・4・5・6（`design.md`）。4 点の選択は確定した。
  - 見解（原文）:「恐らく、loopは重視しない方が良いというのがここ数日の結論。必要な物を必要に応じてレンダリング（配置）するような大富豪的な使い方にしてしまったほうが次へ次へと進めるだろうという見解」
  - 補足（原文）:「だいたい合ってます。ただ、Resonite -> BetterResoloop経路は完全に0にしない。双方向的な経路ではなく、また別の経路を用意するべき（同じ経路でないほうがよい）という考えに基づきます。」（`openspec/roadmap.md` の R21）
  - 「まだ push しない」。入れ替えの順（B2c-2）は「後で判断します」（13:36）。
- **Codex の利用枠**: 16:35 に開いた窓で約 58 万 token を使った（15.8 万 + 24.3 万 + 13.0 万 + 5.0 万）。次の窓は 21:35 ごろに開く見込み。重いレビューの予約（21:40）は、18:36 ごろに Claude Code が background の shell を止めたため消えた。レビューは始まっていない（log も報告も無い）。起動は task 2.3 でユーザーに伺う。
- **群 1 で変えたもの**（2026-10-13 21:21〜21:30）: `CLAUDE.md`・`AGENTS.md`・`.claude/agents/feature-manager.md`・`scripts/agents/ccg-go-roles.mjs`・`docs/dev/agent-workflow.md` の `plan/` の記述を `openspec/` へ直し、`agent-workflow.md` に「指示書を出す前の確認」「Codex の利用枠」「記録の置き場」の節を足した（S3 の試験の節は消した）。merge 済みの branch 9 本と worktree `tmp-s2-cand`（未 commit の変更なし）を消した（`git branch -d`、`git worktree remove`。force なし）。 `openspec/roadmap.md`、`openspec/unverified.md`、この文書を作った。`plan/README.md` と `plan/resoloop-slimming-plan.md` の冒頭、`assignments.md` の末尾に案内を足した。`better-design-v2/design.md` の §2・§3・§14 の 4 点を「確定（2026-10-13、ユーザー）」に直した。`task.json` を短い案内に置き換えた（管理役の起動のコマンドは `tasks.md` の 2.3 にある）。Notion は 3 ページを直し、読み直して確かめた（21:25 ごろ）: 「現在地と引き継ぎ」を縮め、整理の前の全文を子ページ「開発履歴（2026-10-13 整理前）」（`3f820d7e5d1b817caeaed2aea3b0dd78`）へ残した／「ロードマップ（実装計画）」の冒頭の callout に正本の場所を足した／「設計メモ」に節「2026-10-13 夜：修正計画の決定」を足した。保存ページは Notion の複製で作ったので、中に古い保存ページ 2 つの複製も入っている。

### 後回しにした項目

- 入れ替えの順の追加（B2c-2、R15）。ユーザーの判断待ち。U08 の後。
- 型付きの参照の制限の文書と、単位 21（R05、U20）。B3 の change で扱う。
- `--url auto` の探索が、接続つきのコマンド 1 回ごとに約 12〜13 秒かかる（R23）。
- 期限切れの案内の 2 件目に「Increase --command-timeout」の文が残る（L3 の担当の指摘。害は無い）。`structured-warnings` の change で、U22 と一緒に扱うかを決める。
- `better-design-v2/design.md` の §2 は、新しい entry を `resoloop-jsx/creation` と書いている。この change の決定 5（入口は `resoloop-jsx` の 1 つ）と食い違う。task 1.9 の範囲（4 点）の外なので変えていない。B3 の change で直す。
- 自動で足される Component を Reflection などから引く（R22。時期は未定）。

### 未検証

一覧は `openspec/unverified.md`（U01〜U22）。ここには、群 1 の作業そのものについて確かめていない点だけを書く。

- 群 1 は文書と記録だけを変えた。build と test は実行していない（製品コードは変えていない）。`scripts/agents/ccg-go-roles.mjs` は `node --check` が終了 code 0 で、注入文の中身を確かめる test は repo に無い。
- `openspec validate course-correction-2026-10 --strict` は通った（21:29）。

### 伺いと返事

【伺う】の task の返事（言葉と日付）と、選んだ進め方を書く。

2026-10-13 21:27 ごろ、4 つをまとめて伺った（選択肢から選ぶ形）。

- 1.1（未 commit の 6 ファイルを main へ commit してよいか）: 「commit する」。6 つだけを `7e8575c` で commit した。以後、この 5 ファイルの編集は commit してよい（群 3〜6 では branch で編集する）。
- 1.13（branch と worktree の片づけ）: 「merge 済みの branch 9 本」と「worktree tmp-s2-cand」。どちらも消した。
- 1.14（`openspec/`、`.claude/skills/openspec-*`、`.claude/commands/opsx/`、群 1 で変えた文書を main へ commit してよいか）: 「commit する」。
- 1.14 つづき（以後、`openspec/` の下だけを変える commit を、伺わずに main へ行ってよいか）: 「伺わずに commit してよい」。範囲は `openspec/` の下だけ。製品コード、merge、push は今までどおり伺う。
