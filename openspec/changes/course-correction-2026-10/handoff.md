# Handoff

群を 1 つ終えるたびに、日付の見出しの節を先頭へ足す。進捗は `tasks.md` のチェックだけに書き、ここには写さない。全体の順序は `openspec/roadmap.md`、未検証の一覧は `openspec/unverified.md` にある。

## 2026-10-14 16:10 JST — 群 7 の途中（task 7.1 の検証を終え、7.2 の重いレビューを起動する）

### 伺いと返事

- 2026-10-14 15:45 ごろ、ユーザーが `/ccg:spec-impl` を実行した（言葉の添えは無い）。Opus は、群 7 の【伺う】でない task（7.1・7.2・7.3）に入ってよいとして受け取った（Opus の解釈）。

### task 7.1: 2 か所の検証（Opus、sandbox の外、`RESOLOOP_RUN_INTEGRATION` なし）

- slot3（`agent/course-correction` `dd4048d`、15:46〜15:55）と、新しい clone `resoloop-worktrees/cc-fresh`（`git clone --branch agent/course-correction` の main の repo から、同じ `dd4048d`。`npm ci`・`dotnet restore` の後、15:55〜16:03）。log は両方とも slot3 `TestResults/CC-G7/opus/slot3/` と `…/fresh/`（各 `summary.txt` に終了 code と秒）。2 か所は順に実行した（並べると、時間に敏感な U13・U24 の test を揺らすため）。

| 検証 | slot3 | 新しい clone |
| --- | --- | --- |
| `dotnet build ResoLoop.slnx --no-restore --no-incremental` | 警告 0、エラー 0 | 警告 0、エラー 0 |
| RLoop.Tests（全体、1 回） | 失敗 0、成功 2027、skip 4、合計 2031 | 失敗 0、成功 2027、skip 4、合計 2031 |
| RLoop.IntegrationTests | 失敗 0、成功 10、skip 21、合計 31 | 失敗 0、成功 10、skip 21、合計 31 |
| `npm.cmd run typecheck` | 終了 code 0 | 終了 code 0 |
| `npm.cmd test` | 341 pass、fail 0、skipped 0 | 341 pass、fail 0、skipped 0 |
| `npm.cmd run contract` | 64・11・4・2、失敗 0 | 64・11・4・2、失敗 0 |

- 2 か所の件数は一致した。今回の 1 回ずつでは U13・U24 の失敗は出なかったが、群 5 で 3 回中 2 回出ているので、不安定さが消えたとは言えない。
- 新しい clone は、7.7 の merge の後に消す予定（消す前に伺う）。

### task 7.2 の準備

- Codex の利用枠: 窓の開始 15:23:30（群 6 の契約）、使用済み 89,304 token、重いレビューの見積もり 22 万 token。合計約 31 万で 50 万に収まるので起動する。窓が閉じるのは 20:23 ごろ。
- 指示書は `.ccg/tasks/resoloop-slimming/cc-review-charter.md`。事前確認（slot3 `dd4048d`、16:05 ごろ、log は slot3 `TestResults/CC-G7/opus/precheck-review/`）: 指示書に書いた build・`git show`・`git archive`・`rg`・`help` が終了 code 0、`--backend workbench status` が `BACKEND_REMOVED` で終了 code 2。

### task 7.2: 重いレビューの結果

| 起動 | 時刻 | token | 結果 |
| --- | --- | --- | --- |
| 1 回目 | 16:05:22〜16:05:31 | — | 終了 code 126。`429 Too Many Requests` で 5 回の再試行を使い切った（log `cc-review-codex-try1.log`）。指示書は変えず、90 秒待って起動し直した |
| 2 回目 | 16:07:30〜16:25:17 | 242,248 | **`PASS_WITH_P2`、P1=0・P2=2・P3=1**（`cc-review-report.md`、再現は slot3 `TestResults/CC-REVIEW/run-20261014/`）。レビューの後の slot3 に追跡ファイルの変更は無い |

- この窓（15:23:30 に開始）の使用済み: 89,304 + 242,248 = 331,552 token。残り約 16.8 万。
- 指摘（どれもレビュー担当が再現で確かめたと報告）:
  - **P2-1（重点 3）**: Workbench の指定が環境変数 `RESOLOOP_BACKEND` か設定の `backend` から来た場合、接続しない command（`help`・`version`・`init`・`manifest scaffold`・`skills sync --update`・offline の `validate`、`apply … --discard-pending … --yes`）が `BACKEND_REMOVED` で止まらず終了 code 0 になる。`--discard-pending` は lock を取って pending を消す。CLI の option・`wb`・`--workbench-pipe` は早く止まっている。spec に接続しない command の除外は無い。
  - **P2-2（重点 2）**: 群 4 の仕分けで消した `BackendSupportTests.BackendUsesCliEnvironmentProjectUserPrecedence` に、直結の設定の優先順（環境変数の `link` が project の `workbench` に勝つ、user の `link` が選ばれる、`Sources["backend"]`）の assert が含まれていた。仕分け (a) で半分を失った。製品の優先順が壊れたという指摘ではない。
  - **P3（重点 6）**: `tools/resoloop-jsx/README.md:213` の「creation target が無ければ key の欠けは `EXPLICIT_KEY_REQUIRED` で止まる」が、`--draft` の例外を落としていて、同じ README の表と食い違う。
- 確認済みとされた点: 凍結した 9 ファイルと `Program.cs` の 3 区間のコードは分岐点と同じ／分岐点から同じ規則で集めた error code 100・option 27 は固定ファイルと一致／Workbench のプロジェクトへの参照は残っていない／`resoloop-jsx/draft` の保存した出力 3 件は、分岐点・HEAD・fixture で一致／Integration の skip 21 件はすべて理由つき。

### task 7.3: 指摘の修正（修正 1 回目）

- 仕組み（Opus の決定）: `ConfigResolver.RejectRemovedBackend(startDirectory, cliBackend, …)` を足し、`Resolve` と同じ順（cli → `RESOLOOP_BACKEND` → project → user）で実効の値を決め、`workbench` のときだけ `BACKEND_REMOVED` を投げる。`Program.cs` の CLI の `--backend` だけを見ていた検査（30〜31 行）をこの呼び出しに替えた。位置は report を開く前で、凍結した 3 区間の外。調べる途中の config を読めないときは投げずに `Resolve` の `CONFIG_INVALID` に任せる（接続しない command が、関係のない設定の誤りで止まるようにはしない）。結果として、`help`・`version` も、環境変数・設定の `workbench` で止まる（CLI の `--backend workbench help` と同じ扱い。spec に接続しない command の除外が無いため）。
- P3 は、README の文の後半を消して表を指すだけにした。
- 下調べは Claude の調査役 1 本（読み取りだけ、slot3 `dd4048d`）。

| 契約 | 時刻 | token | 担当の報告 | Opus の扱い |
| --- | --- | --- | --- | --- |
| cc-g7-fix1 v1（P2-1・P2-2・P3） | 16:29:11〜16:31:52 | 72,115 | `DONE`（slot3 `TestResults/CC-G7/fix1-report.md`）。足した test 21 件（offline の command 7 × 環境変数・project の 14、`EnvironmentLinkOverridesProjectWorkbench` 1、`BackendUsesCliEnvironmentProjectUserPrecedence` 1、`RejectRemovedBackend` 5）、filter の 87 成功、変えたのは契約の 5 ファイルだけ | 差分を読んで受け入れた |

- 見積もり: 13 万 token とした（標準の 19 万は残り約 16.8 万を超えるため、同じ大きさの契約の実績 9〜13 万を根拠にし、sandbox の中の test を filter に絞った）。この窓の使用済み: 331,552 + 72,115 = 403,667 token。
- offline の test は、一時 directory のファイルの一覧と内容が実行の前後で同じであることも確かめる（`init`・`manifest scaffold` が書かない、`--discard-pending` が state を変えない）。
- `cc-workbench-test-triage.md` は直していない（群 4 の記録として残す）。消しすぎた assert は、この修正で `BackendSupportTests` に戻った。
- commit（`agent/course-correction`）: `03dae4b`（`fix(cli): stop on a Workbench backend from the environment or config before any command`）、`bc8e9fd`（`docs(jsx): point the key sentence at the key rules table only`）。

検証（Opus、sandbox の外、`RESOLOOP_RUN_INTEGRATION` なし。log は slot3 `TestResults/CC-G7/opus/fix1-slot3/`・`fix1-fresh/`）:

| 検証 | slot3（commit の前の作業ツリー、16:32〜16:40） | 新しい clone（`bc8e9fd`、16:41〜16:49） |
| --- | --- | --- |
| build | 警告 0、エラー 0 | 警告 0、エラー 0 |
| RLoop.Tests | 失敗 0、成功 2048、skip 4、合計 2052（2027 + 21） | 失敗 0、成功 2048、skip 4、合計 2052 |
| IntegrationTests | 失敗 0、成功 10、skip 21 | 失敗 0、成功 10、skip 21 |
| typecheck／`npm test`／contract | 0／341 pass・fail 0／64・11・4・2 | 0／341 pass・fail 0／64・11・4・2 |

- 2 か所の件数は一致し、失敗 0。7.3 の確かめ方（7.1 の検証をもう一度実行して失敗 0）を満たす。修正は 1 回で済んだ（4 回の上限のうち 1）。

### 次

- task 7.4（【伺う】実機 L5 の許可と対象の session）。slot3 の HEAD は `bc8e9fd`。Codex の窓（15:23:30 開始）は 20:23 ごろ閉じる。


## 2026-10-14 15:40 JST — 群 6 を終えた（実行しなかった実機の test を skip にする）

### 伺いと返事

- 2026-10-14 15:15 ごろ、ユーザーが `/ccg:spec-impl` を実行した（言葉の添えは無い）。Opus は、群 6 に入ってよいとして受け取った。Codex の枠は、11:18 のユーザーの言葉が今日も有効と解釈した（Opus の解釈）。

### 下調べ（Claude の調査役 2 本、読み取りだけ、slot3 `5ff0c18`）

- `RESOLOOP_RUN_INTEGRATION` の一致は 14 ファイル・21 行で、計画と同じ。すべて `[Fact]`、`[Theory]` は 0。別の条件を持つのは 3 か所: Blender（`RESOLOOP_RUN_BLENDER_TESTS`）、Capture（`RESOLOOP_RUN_CAPTURE_INTEGRATION`）、TextureVariant（`RESOLOOP_TEXTURE_SAMPLES` が空でないこと）。
- xunit は 2.9.3（v2）。手本は `InstalledFluxLibrariesFactAttribute`（`tests/RLoop.Tests/FluxSdkElementIdTests.cs:173-181`）。

### 仕組み（Opus の決定）

- 契約は 1 本（`cc-g6-skip-contract.md`、task 6.1〜6.4 の文書の 1 文まで）。別の条件は、`LiveIntegrationGate.SkipReason(value, unmetCondition, conditionMet)` の純粋な関数で決め、P12 と spec の「追加の条件を満たさない」を、環境変数を変えない test で確かめる。理由の順は、`RESOLOOP_RUN_INTEGRATION` が先、別の条件が後。
- TextureVariant は、本体が `samples` を使うので、早期 return を `Assert.NotNull(samples);` に替える（本体の変更はこの 1 行だけ）。
- **task 6.4 の `verify.ps1` は変えない。** 理由: `verify.ps1` は `Category!=Integration` の filter で実機の test を最初から外しており、Integration の結果を「成功の件数だけ」で出してはいない（skip は `summary.json` の `counts.skipped` に別に入る）。
- **気付いた点（群 6 の前からの問題。この change では直さない）**: (1) xunit v2 の skip は、TRX の `Counters` で `notExecuted` に入らず、`total − executed` に現れる（例: slot3 `TestResults/CC-G4/opus/accept2-rloop.trx` は `total=2030 executed=2026 notExecuted=0`）。`verify.ps1:113` は `total == executed + notExecuted` を求めるので、skip が 4 件ある RLoop.Tests の段で失敗するはずである（`verify.ps1:114` の skip の数も 0 になる）。(2) `verify.ps1` は `#requires -Version 7.2` で、このマシンには `pwsh` が無い。どちらも実行して確かめてはいない。

### 事前確認（Opus、slot3 `5ff0c18`、sandbox の外、`RESOLOOP_RUN_INTEGRATION` なし、15:20 ごろ）

- log は slot3 `TestResults/CC-G6/opus/precheck-*`。`dotnet build ResoLoop.slnx --no-restore` は警告 0・エラー 0／`dotnet test tests/RLoop.IntegrationTests --no-build` は合格 23・スキップ 0・合計 23（群 6 の前の基準。21 件は本体を実行せずに合格と数えられている）。

### Codex の利用枠（起動の前の記録）

- 前の窓（09:14:01 に開始）は 14:14 ごろに閉じた。14:19 以降は起動していないので、この契約の起動で新しい窓が始まる（使用済み 0）。見積もりは 15 万 token で、50 万に収まる。

### 実行の記録

| 契約 | 時刻 | token | 担当の報告 | Opus の扱い |
| --- | --- | --- | --- | --- |
| cc-g6-skip v1（task 6.1〜6.4） | 15:23:30〜15:27:15 | 89,304 | `DONE`（slot3 `TestResults/CC-G6/skip-report.md`）。build 警告 0・エラー 0／gate の test 合格 8／Integration 合格 10・スキップ 21・合計 31／`rg` の 2 つの確認どおり／契約と違えた点なし | 差分を読んで受け入れた |

- この窓（15:23:30 に開始）の使用済み: 89,304 token。
- Opus が直した点（commit の前）: `docs/dev/deterministic-verification.md` の足した文で、変数名と理由をバッククォートで囲んだ。同じ文書の 22 行の「three named test projects」を「two」に直した（群 4 で `RLoop.Workbench.Tests` を消した後の消し残し。`-TestProject` の選択肢は `All`・`RLoop.Tests`・`RLoop.IntegrationTests`）。

### task 6.5 の受け入れ（Opus、slot3、sandbox の外、15:30〜15:38）

log は slot3 `TestResults/CC-G6/opus/accept-*`。対象は上の直しを入れた作業ツリー（直後に commit）。

| 検証 | 群 6 の前 | 群 6 の後 |
| --- | --- | --- |
| `dotnet build ResoLoop.slnx --no-restore --no-incremental` | 警告 0、エラー 0 | 警告 0、エラー 0 |
| RLoop.IntegrationTests（`RESOLOOP_RUN_INTEGRATION` なし） | 合格 23、スキップ 0、合計 23 | 失敗 0、合格 10、**スキップ 21**、合計 31 |
| 同じ（値 `0`・`true`・空） | — | 3 通りとも、失敗 0・合格 10・スキップ 21・合計 31（PowerShell で空を入れると変数が消えるので、空は「なし」と同じ実行） |
| skip の理由（trx） | — | 21 件すべてが `RESOLOOP_RUN_INTEGRATION is not 1` |
| trx の `Counters` | — | `total=31 executed=10 passed=10 notExecuted=0`（skip は `total − executed`） |
| RLoop.Tests（全体、1 回） | 合格 2027、スキップ 4（群 5） | 失敗 0、合格 2027、スキップ 4、合計 2031 |

- 合格 10 の内訳: `SessionLockIsolationTests` 2 と `LiveIntegrationGateTests` 8（P12: 値 `1` だけが実行、`null`・空・`0`・`true` は理由つきで止まる。別の条件は、`RESOLOOP_RUN_INTEGRATION` が先、満たさない別の条件が後）。
- commit（`agent/course-correction`）: `dd4048d`（`test(integration): report unexecuted live tests as skipped`）。main への merge と push はしていない。

### 未検証・実行しなかったこと（群 6）

- `RESOLOOP_RUN_INTEGRATION=1` での実行（実機に触れるので実行しない）。そのため、Blender・Capture・TextureVariant の属性が、別の条件を満たさないときに理由つきで skip になることは、属性そのものを実行して確かめてはいない。確かめたのは、属性が呼ぶ純粋な関数 `SkipReason(value, unmetCondition, conditionMet)` の 3 通りの test だけ。
- 21 の実機の test の本体は、この群では 1 度も実行していない（変えたのは属性と早期 return の行、TextureVariant の 1 行だけ）。
- `verify.ps1` の skip の数え方（上の「気付いた点」）は、`pwsh` が無いため実行して確かめていない。直すかは、群 7 の後に別の change として決める。

### 次

- 群 7（まとめのレビュー・実機・merge・片づけ）: task 7.1 から。slot3 の HEAD は `dd4048d`。7.4（実機 L5）・7.5（免除）・7.7（merge）・7.9（push）は【伺う】。

## 2026-10-14 14:40 JST — 群 5 を終えた（主の入口で key を省けるようにし、key の規則を 1 つの表にした）

### 伺いと返事

- 2026-10-14 13:31 ごろ、ユーザーが `/ccg:spec-impl` を実行した（言葉の添えは無い）。Opus は、群 5 に入ってよいとして受け取った。Codex の枠は、11:18 のユーザーの言葉が今日も有効と解釈した（Opus の解釈）。

### 計画と実際の違い（下調べ: Claude の調査役 2 本、読み取りだけ、slot3 `a28b708`）

| 計画（`tasks.md`） | 実際 | Opus の決定 |
| --- | --- | --- |
| 5.4 `elements.ts` の `key` を `key?: string` にする | 必須の `key: string` は生成物 `src/generated/apply-types.ts:116,128` にあり、`SlotProps` はそれを継ぐ | 生成物は変えず、`elements.ts` で `Omit<…,"key"> & { key?: string }` の形にする |
| 5.1 (c) `EXPLICIT_KEY_REQUIRED` と要素の位置／5.4 `resolveKey` は変えない | いまの `EXPLICIT_KEY_REQUIRED` には TSX の位置が付かない（文書内の path だけ） | `resolveKey` の本体と key の規則は変えず、呼び出しの 2 か所（`evaluate.ts:286,379`）で位置を付け、`cli.ts` の位置付きの code の一覧に足す。計画の「変えない」は key の規則を守る条件と読んだ |
| 5.1 (e) `--target creation --draft` が止まる | すでに終了 code 1・出力なしで止まる | test を足すだけ |
| 5.6 `README.md`・`README_JA.md`・`README-DETAILS.md`・`skills` の `resoloop-jsx/draft` を消す | 4 つとも 0 件。`resoloop-jsx/draft` は `tools/resoloop-jsx/README.md:68,253` だけ。`README-DETAILS.md` の行は Workbench の節が縮んで 192〜227 行へずれた | 5.6 の契約で、行を今の位置で指定する |
| 5.5 `--mode regenerate --yes` で拒否 | src はすでに、接続の前に終了 code 6 で止まる。外側の code は `CREATION_DOCUMENT_INVALID`（`CreationDocument.cs:147-150` が包む）で、message に `Draft index-derived` が入る | src は変えない。test はその形で確かめる |

### Codex の利用枠（起動の前の記録）

- 窓の開始は 2026-10-14 09:14:01（14:14 ごろに閉じる見込み）、使用済みは 1,008,958 token。見積もりは entry 25 万、docs と 5.5 で 10 万。目安の 50 万はすでに超えているが、ユーザーの言葉により起動する。容量切れや利用上限の拒否が出たら、契約を書き換えずに止め、ユーザーに伝える。

### 事前確認（Opus、slot3 `a28b708`、sandbox の外、13:34〜13:39）

- log は slot3 `TestResults/CC-G5/opus/precheck-*.log`。`npm.cmd run typecheck` 終了 code 0／`npm.cmd test` 336 pass・fail 0（約 40 秒）／`npm.cmd run contract` 64・11・4・2、失敗 0／build 1 回 約 0.3 秒／`dotnet build … --no-restore` 警告 0・エラー 0／filter `CreationModeBoundary|IndexDerivedDraftBuildCannotLoadForValidateOrApply|CreationTsxSkill|CreationRegenerateSkill|S2Fix` 成功 17。

### 実行の記録

| 契約 | 時刻 | token | 担当の報告 | Opus の扱い |
| --- | --- | --- | --- | --- |
| cc-g5-entry v1（task 5.1〜5.4） | 13:39:53〜14:03:48 | 264,763 | `DONE`（slot3 `TestResults/CC-G5/entry-report.md`）。赤: 実装の前に 340 件中 2 件が失敗（5.1 (a)(b) と (c)）、(d)(e) は成功。実装の後 341 pass・fail 0（336 + 5）、約 322 秒。contract 64／11／4／2。下調べとの違いは 1 点（`markBuildErrorOrigin` には `{ source }` の形で渡す） | 差分を読んで受け入れた。生成物・`resolveKey` の本体・`package.json` は変わっていない。既存の test の変更は `cli.test.ts` の 1 件だけ |
| cc-g5-docs v1（task 5.5・5.6） | 14:10:53〜14:19:22 | 129,023 | `DONE`（slot3 `TestResults/CC-G5/docs-report.md`）。test の filter は 1 回目に失敗 1（SKILL.md の文を大文字で始めたため、test の確かめる `same-name sibling Slots` と一致しなかった）、文書を直して成功 17。表の 3 つの build のコマンドは終了 code 0 | 差分を読んで受け入れ、文書の 3 点を Opus が直した（下） |

- Opus の受け入れ（slot3、sandbox の外、14:04〜14:10。log は slot3 `TestResults/CC-G5/opus/accept-*.log`）: `npm.cmd run typecheck` 終了 code 0／`npm.cmd test` tests 341・pass 341・fail 0（約 341 秒。前は約 40 秒で、増えたのは性質の test の 200 件）／`npm.cmd run contract` 64・11・4・2、失敗 0。
- commit: `agent/course-correction` の `aa2e2f3`（`feat(jsx): make keys optional in the main entry`）。
- 気付いた点（直す）: `one-entry.test.ts:61` は、保存した出力のファイルを読むときに CRLF を LF にそろえていない。この repo は `core.autocrlf` で JSON を CRLF に変えて checkout するので、新しい clone（task 7.1）では一致しないおそれがある。docs の契約が終わってから Opus が直す。
- この窓の使用済み: 1,273,721 token（1,008,958 + 264,763）。窓は 14:14 ごろに閉じる見込みで、docs の契約は、閉じる前に起動した。docs の 129,023 token を足すと 1,402,744。利用上限の拒否は出なかった。

### task 5.5: P05（`$draftKeys` の定義はどの組でも拒否される）を確かめている test

| 経路 | test | 止まり方 |
| --- | --- | --- |
| 通常の `validate`・`apply`（`ApplyDocument.Load`） | `ApplyWorkflowTests.IndexDerivedDraftBuildCannotLoadForValidateOrApply`（`ApplyWorkflowTests.Scopes.cs`） | `APPLY_DRAFT_KEY_UNSTABLE` |
| 作り直しの envelope の読込み | `CreationDocumentTests.RejectsOwnershipMismatchAndInvalidDefinitionShape`（`CreationDocumentTests.cs:62`） | `CREATION_DOCUMENT_INVALID` |
| `apply --mode regenerate --replace-order delete-first --yes`（新しく足した） | `CreationModeBoundaryTests.RegenerateApplyWithYesRejectsDraftKeysBeforeConnection` | 終了 code 6、接続 0 回、`CREATION_DOCUMENT_INVALID`、message に `Draft index-derived` |
| TSX の build から C# の `validate` まで | `tools/resoloop-jsx/scripts/contract.ts:63`（`missing-key-draft.tsx` の `--draft` の出力） | `APPLY_DRAFT_KEY_UNSTABLE` |

- `--adopt`・`--prune` との組は、`$draftKeys` を見る前に `CREATION_MODE_MISMATCH`（終了 code 2）で止まる（既存の `RegenerateRejectsLegacyMutationAndFilterOptions`）。「どの組でも拒否」は「実機へ何も送らず、0 でない終了 code」の意味で成り立つ。code は 1 つにそろわない。

### Opus が直した点（commit の前）

- `tools/resoloop-jsx/README.md`: 担当が見出し「Experimental TSX creation input」を消し、作り直しの説明が「Key rules」の節の下に入っていた。見出しを戻し、指す先を失った「This relaxed rule applies to creation envelopes only.」を消した。3 つの README から消えた「通常の build は兄弟の Slot の名前が重ならないこと（`DUPLICATE_SIBLING_NAME`）」を、「Key rules」の節に 1 文で足した。
- `skills/codex/resonite-build/SKILL.md:72`: 小文字で始まった文を、前の文と `;` でつないだ（test の確かめる `same-name sibling Slots` は残る）。
- `tools/resoloop-jsx/test/one-entry.test.ts:61`: 保存した出力のファイルを読むときに CRLF を LF にそろえた（上の「気付いた点」）。

### task 5.7 の受け入れ（Opus、slot3、sandbox の外、`RESOLOOP_RUN_INTEGRATION` なし、14:20〜14:35）

log は slot3 `TestResults/CC-G5/opus/accept2-*.log`。対象は上の直しを入れた作業ツリー（直後に commit）。

| 検証 | 結果 |
| --- | --- |
| `dotnet build ResoLoop.slnx --no-incremental` | 警告 0、エラー 0 |
| RLoop.Tests（全体、1 回目） | **失敗 1**、成功 2026、skip 4。失敗は U13 の `S3RealProcessKillReleasesHandleButPreviousPendingStillBlocks`（`SessionWriteLock.cs:41` の `IOException`） |
| RLoop.Tests（全体、2 回目） | **失敗 1**、成功 2026、skip 4。失敗は `CaptureTests.RejectsWrongFormatAndSize(… "CAPTURE_FORMAT_MISMATCH")`（期待と違う `CAPTURE_EXPORT_TIMEOUT`）。初めて見る失敗で、`openspec/unverified.md` に U24 として書いた |
| `CaptureTests` だけ（3 回） | 3 回とも失敗 0、成功 40 |
| RLoop.Tests（全体、3 回目） | 失敗 0、成功 2027、skip 4（4.7 の 2026 + 5.5 の 1） |
| filter `CreationModeBoundary\|CreationTsxSkill\|CreationRegenerateSkill\|S2Fix` | 失敗 0、成功 17 |
| `npm.cmd run typecheck`／`npm.cmd test`／`npm.cmd run contract` | 終了 code 0／341 pass・fail 0／64・11・4・2、失敗 0 |
| `rg -n "resoloop-jsx/draft" README.md README_JA.md README-DETAILS.md skills`／同じ語の `tools/resoloop-jsx/README.md` の一致 | 0 件／1 件 |

- src（C#）は `a28b708` から変わっていない（`git diff --stat a28b708 -- src` が空）。失敗した 2 つの test も、その対象のコードも、この群では変えていない。**3 回のうち 2 回で失敗が出たので、「slot3 の RLoop.Tests は失敗 0」とは言えない。**
- commit（`agent/course-correction`）: `aa2e2f3`（`feat(jsx): make keys optional in the main entry`）、`b694490`（`test: cover draft keys in regenerate apply and CRLF checkouts of saved outputs`）、`5ff0c18`（`docs(jsx): explain the key rules in one table`）。計画の 2 つの commit の間に、test の 1 つを足した。main への merge と push はしていない。

### 未検証・実行しなかったこと（群 5）

- 実機。主の入口 `resoloop-jsx` から import して key を省いた TSX の作り直しは、L5（task 7.4 (i)）で確かめる。
- 重いレビュー（群 7 の 7.2）。
- `npm test` は約 40 秒から約 340 秒に延びた（性質の test の 200 件）。件数は計画どおりにしている。
- Integration は、この群では実行していない（C# の src を変えていない。群 7 の 7.1 で実行する）。

### 次

- 群 6（実行しなかった実機の test を skip にする）: task 6.1 から。slot3 の HEAD は `5ff0c18`。

## 2026-10-14 12:30 JST — 群 4 の途中（task 4.1 の基準を取り、4.2〜4.5 の契約を起動する）

### 伺いと返事

- 2026-10-14 12:17 ごろ、ユーザーが `/ccg:spec-impl` を実行した（言葉の添えは無い）。Opus は、群 4 に入ってよいとして受け取った。Codex の枠は、11:18 のユーザーの言葉（「codexの枠にかなり余裕があるので気にしないでください。」）が今日も有効と解釈した（Opus の解釈）。

### task 4.1: 除く前の基準（slot3 `fdf1f74`、Opus、sandbox の外、`RESOLOOP_RUN_INTEGRATION` なし、12:18〜12:24）

- `rg -l -i workbench src tests tools skills scripts docs README.md README_JA.md README-DETAILS.md AGENTS.md CLAUDE.md` は 62 ファイル。結果は `.ccg/tasks/resoloop-slimming/cc-workbench-inventory.txt`。`AGENTS.md` は一致しない。
- log は slot3 `TestResults/CC-G4/opus/baseline-*.log`。

| 検証 | 結果 |
| --- | --- |
| `dotnet build ResoLoop.slnx --no-incremental` | 警告 0、エラー 0 |
| RLoop.Tests | 失敗 0、成功 2013、skip 4（3.7・3.9 の値と同じ） |
| RLoop.Workbench.Tests | 失敗 0、成功 190 |
| RLoop.IntegrationTests | 失敗 0、「成功」23（実機の要る test は実行せずに return して成功と数えられている） |
| `npm.cmd run typecheck` | 終了 code 0 |
| `npm.cmd test` | tests 336、pass 336、fail 0 |
| `npm.cmd run contract` | 64／11／4／2、失敗 0 |

### 仕組み（群 4 の分け方。Opus の決定）

- 契約は 2 本を順に `gpt-6-luna` high へ渡す: `cc-g4-remove-contract.md`（task 4.2〜4.5: 仕分け、先に書く test、コードと script の除去）と、その後の `cc-g4-docs-contract.md`（task 4.6: 文書と配布する Skill）。
- 下調べは Claude の調査役 2 本（読み取りだけ、slot3 `fdf1f74`）。計画の行番号は `Program.cs` で約 +2 ずれていた（例: `RunWorkbench` は 546〜592、help は 1694・1781・1789）。`README-DETAILS.md` の節外の 4 か所も +2。
- 仕分けの下調べ: (a) で消える test の件数は 47（`BackendSupport` の class を消すので、`LinkBackendAndUnlistedCommandsPassThrough` も (a) に数えた）、(b) で消える InlineData は 1。ファイルごと消えるものは無い。確定の数は担当の仕分け（`cc-workbench-test-triage.md`）で取る。
- **Opus が決めた点**: (1) option の `--backend workbench`・`--workbench-pipe`・`wb` は、引数を読んだ直後、`help`・`version` を含むすべての分岐の前で止める。(2) 環境変数と設定の `backend` は、設定を読む所（`ConfigResolver.Resolve`、`Program.cs:176`）で止める。設定を読まずに return する offline のコマンド（`help`、`version`、`init`、`skills`、`uix recipe`、`schema`・`manifest`、`catalog types`）は、環境変数・設定の `workbench` では止まらない（backend の値をそもそも読まない）。spec の「どれから来ても」を、「backend の値を読むすべてのコマンドで」と読んだ。P06 の表は、tasks.md のとおり接続の後の `switch` の名前にする。(3) 環境変数とカレント directory を変える test は、`DisableParallelization` の collection に入れる（既存の 3 つの collection と同じ形）。
- 事前確認（12:24〜12:25）は契約の「事前確認」の表にある。
- remove v1 の起動: 12:24 ごろに 1 度起動したが、Opus の起動のしかた（背景の command の 10 分の時間切れ）で途中で切れるおそれがあったため、1 分ほどで止めた（slot3 の変更なし、`git status` で確かめた）。12:25:52 に、切り離した process（`.ccg/tasks/resoloop-slimming/cc-g4-run.cmd`）で同じ契約を起動し直した。止めた 1 本の token は数えていない。

### Codex の利用枠（起動の前の記録）

- 窓の開始は 2026-10-14 09:14:01（14:14 ごろに閉じる見込み）、使用済みは 571,819 token。見積もりは remove 25 万、docs 8 万。目安の 50 万はすでに超えているが、上のユーザーの言葉により起動する。容量切れや利用上限の拒否が出たら、契約を書き換えずに止め、ユーザーに伝える。

### 実行の記録（task 4.2〜4.5）

| 契約 | 時刻 | token | 担当の報告 | Opus の扱い |
| --- | --- | --- | --- | --- |
| cc-g4-remove v1 | 12:25:52〜12:31 ごろ | 123,606 | `BLOCKED`: `ApplySessionObservationTests.cs:18` が、消した引数 `workbenchClientFactory` の位置に `null` を渡していて、test の build が CS1501。変えてよい test の外なので止まった | 正しい止め方。**契約の書き落としは Opus の誤り**（調査役に見せたのが 8 ファイルだけで、`ResoniteClientFactory.ConnectAsync` の呼び出し元を全部は探させなかった）。v1 の変更は戻さずに v2 で続けた |
| cc-g4-remove v2 | 12:57:13〜13:05:10 | 197,068 | `BLOCKED`: build 警告 1（削除した `RLoop.Workbench.csproj` への参照。restore できないため古い `project.assets.json` が残る）、RLoop.Tests 失敗 2（`LockFileDiagnosticsTests` の 2 件、`RmStartSession` が 29）。ほかは契約どおり: 仕分け (a) 47・(b) 1、`BackendRemoved` 61 成功 | 2 つとも sandbox の中だけの現象と判断し、sandbox の外で受け入れた（下の表）。外では build 警告 0、2 件の test も成功 |

- v1 の時刻ファイルに終わりの行が無いのは、起動の `.cmd` が `codex.cmd` を `call` なしで呼び、codex の終了で `.cmd` も終わったため（v2 から `call` を付けた）。
- この窓の使用済み: 892,493 token（571,819 + 123,606 + 197,068。止めた 1 本は数えていない）。

### task 4.3 の赤（Opus、別の作業ツリー）

- v1 の担当は、test を build する前にコードの変更を始めたため、赤を取れなかった。v2 の契約で、赤は Opus が確かめることにした。
- 手順: 一時の作業ツリー `resoloop-worktrees/tmp-g4-red`（`fdf1f74`、detached）に、v2 の最後の `BackendRemovedTests.cs` だけを置いて build し、`--filter "FullyQualifiedName~BackendRemoved"` を実行した（13:06 ごろ）。結果は **失敗 57・成功 4**。失敗は `EveryConnectedCommandRejectsRemovedBackend`（54）、`BackendWorkbenchFromCliEnvironmentAndProjectFailsBeforeConnect`、`ApplyWithProjectWorkbenchSettingFailsBeforeConnect`、`HelpOmitsRemovedBackendAndCommandOptions`。成功の 4 つ（`UnknownBackendRemainsInvalidBeforeConnect`、`LegacyWorkbenchPipeEnvironmentIsIgnored`、`LegacyWorkbenchPipeProjectKeyIsIgnored`、`ExplicitAndDefaultLinkHaveSameJsonOutputAndExitCode`）は、除く前から同じはずの link の挙動を確かめる test なので、除く前に成功するのが正しい。
- 証拠は slot3 `TestResults/CC-G4/opus-red/`（`red2-*.log`、`red2.trx`、test の写し `BackendRemovedTests.v2.cs`）。一時の作業ツリーは確かめた後に `git worktree remove --force` で消した（中身は、Opus が置いた test の写しと build の出力だけ）。
- v1 の時点の test（`BackendRemovedTests.v1.cs`）を同じ形で実行したときは、link の 3 つも失敗した（`--url` を渡さず、URL の解決が環境に依存していた）。v2 の担当が `--url ws://localhost:1` を足して直していた。

### task 4.2〜4.5 の受け入れ（Opus、slot3、sandbox の外、`RESOLOOP_RUN_INTEGRATION` なし、13:06〜13:10）

log は slot3 `TestResults/CC-G4/opus/accept-*.log`。仕分けは `.ccg/tasks/resoloop-slimming/cc-workbench-test-triage.md`（slot3 `TestResults/CC-G4/` からの写し）。

| 検証 | 結果 |
| --- | --- |
| `dotnet build ResoLoop.slnx --no-incremental` | 警告 0、エラー 0 |
| RLoop.Tests | 失敗 0、成功 2026、skip 4。**式: 2013 − 48（消した件数。(a) 47 + (b) の InlineData 1）+ 61（`BackendRemovedTests`）= 2026** |
| `BackendRemoved`（担当の実行） | 成功 61（P06 は接続の後の `switch` の 18 の名前 × 3 通り = 54。P07 は `data.latencyMs` を除いて比べた） |
| RLoop.IntegrationTests | 失敗 0、「成功」23（除く前と同じ。実機の要る test は実行していない） |
| `npm.cmd run typecheck`／`npm.cmd test`／`npm.cmd run contract` | 終了 code 0／336 pass・fail 0／64・11・4・2、失敗 0（除く前と同じ） |
| `rg -n -i workbench src` | `Program.cs:27-31`・`Configuration.cs:74-75` の `BACKEND_REMOVED` の処理と、コメント 4 か所（`FluxDeployGuard.Checks.cs:4,495,539`、`ResoniteLinkClientAdapter.cs:558`）だけ |
| `rg -n -i workbench scripts`／`node --check scripts/agents/ccg-go-roles.mjs` | 0 件／終了 code 0 |
| 凍結した 9 ファイルと `fixtures/` の差分 | なし。`src/RLoop.Core` の変更は `Configuration.cs` だけ |

- Opus が足した片づけ: 消した 2 つの project の directory に残った、git の無視する `bin/`・`obj/` と空の `Protocol/` を消した（受け入れの script の Workbench の段が、その directory を見て実行し、`MSB1003` で終了 code 1 になったため。test の結果ではない）。
- commit: `agent/course-correction` の `c48f33f`（`feat(cli)!: remove the Workbench backend`。47 ファイル、+237／−9808）。

### task 4.6（文書と配布する Skill）

- 契約 `cc-g4-docs-contract.md`（`gpt-6-luna` high、13:11:31〜13:14:38、116,465 token、報告は `DONE`。報告は slot3 `TestResults/CC-G4/docs-report.md`）。変えたのは契約の 15 ファイルだけ（+29／−86）。この窓の使用済みは 1,008,958 token（892,493 + 116,465）。
- Opus が差分を読んで受け入れた。`README-DETAILS.md` の Workbench の節（91〜161 行）は、節「Workbench backend の除去」（91・93 行）に置き換わった。節の外の 4 か所の句も消えた。`fork-strategy.md` は 1・8・27・46 行を直し、9・29〜32・50 行は記録の事実として残した。
- Opus の確かめ（slot3、sandbox の外）: `rg -n -i workbench README.md README_JA.md tools/resoloop-jsx/README.md skills docs/DECLARATIVE.md` は 0 件（前は 6 件）／`README-DETAILS.md` の一致は 91・93 行だけ（前は 30 行）／`rg -n "凍結|除去予定" docs/dev/fork-strategy.md CLAUDE.md docs/dev/agent-workflow.md` は `agent-workflow.md:162` の「凍結の例外」（Workbench ではない）だけ（前は 7 行）。

### task 4.7 の受け入れ（Opus、slot3、sandbox の外、`RESOLOOP_RUN_INTEGRATION` なし、13:15〜13:19）

log は slot3 `TestResults/CC-G4/opus/accept2-*.log`。対象は `c48f33f` と文書の変更（直後に `a28b708` で commit）。

| 検証 | 除く前（4.1） | 除いた後 |
| --- | --- | --- |
| build | 警告 0、エラー 0 | 警告 0、エラー 0 |
| RLoop.Tests | 成功 2013、skip 4 | 失敗 0、成功 2026、skip 4（2013 − 48 + 61） |
| RLoop.Workbench.Tests | 成功 190 | （project を消した） |
| RLoop.IntegrationTests | 「成功」23 | 「成功」23（同じ。実機は実行していない） |
| `npm.cmd run typecheck`／`test`／`contract` | 0／336 pass／64・11・4・2 | 0／336 pass／64・11・4・2 |

- commit（`agent/course-correction`）: `c48f33f`（`feat(cli)!: remove the Workbench backend`）、`a28b708`（`docs: describe the removal of the Workbench backend`）。main への merge と push はしていない。
- `.claude/settings.local.json`（main の作業ツリー、git の追跡外）には、Workbench に触れる許可が 3 行残っている: 8・9 行（別の repo `resonite-workbench` の `dotnet test`）と 23 行（消した `scripts/dev/workbench.ps1` の実行）。task 4.7 のとおり、設定は変えていない。消すかはユーザーが決める。
- `.devin/config.local.json` にも Workbench の語がある（調査役の報告。中身は見ていない。追跡外で、この群の対象外）。
- RLoop.Tests の全体は、除いた後に 2 回（13:06・13:15）実行し、2 回とも失敗 0 だった。U13・U23 はこの 2 回では出なかった。

### 未検証・実行しなかったこと（群 4）

- 実機。`--backend workbench status` が実機の session に対しても `BACKEND_REMOVED` で止まることは、L5（task 7.4 (i)）で確かめる。offline では、偽の client で `connect` 0 回を確かめた。
- 重いレビュー（群 7 の 7.2 で `main..agent/course-correction` を見る）。
- spec の「`backend` の値が `workbench` の場合（どれから来ても）」のうち、設定を読まない offline のコマンドに、環境変数・設定の `workbench` を与えた場合は止まらない（上の「Opus が決めた点」(2)）。これを test で確かめてはいない。

### 次

- 群 5（入口と key の規則を 1 つにする）: task 5.1 から。slot3 の HEAD は `a28b708`。

## 2026-10-14 12:17 JST — 群 3 を終えた（task 3.2〜3.9。通常の apply の表面を test で固定し、error code の範囲を 14 ファイルへ広げた）

### 伺いと返事

- 2026-10-14 11:18 ごろ、ユーザーが `/ccg:spec-impl` を実行し、次の言葉を添えた（原文）:「codexの枠にかなり余裕があるので気にしないでください。」Opus は、これを「下の 11:15 の節の『この窓では起動しない』を取り消し、窓の残りを待たずに群 3 の契約を起動してよい」として受け取った（範囲の解釈は Opus のもの）。

- 2026-10-14 11:50 ごろ、群 3 の報告で「固定した error code の一覧は 6 ファイルの分だけで、通常の `apply` が返しうる code の全部ではない。このままでよいか、増やすか」を伺った。返事（原文）:「凍結コードを増やします」。Opus は「固定する error code の範囲を、通常の経路が返しうる code へ広げる」として受け取り、対象のファイルを調べてから task を足す（どのファイルを足すかは、調査の結果を示して決める）。
- 2026-10-14 12:00 ごろ、調査の結果（下の「固定の範囲を広げた」）を示して、範囲を伺った（選択肢から選ぶ形）。返事:「code は A+B、凍結は A (Recommended)」= error code は 14 ファイルから集めて固定する／機能の追加を禁じる「凍結したファイル」は 6 + A の 9 つ／共用の B の 5 つは凍結しないが、`APPLY_`・`STABLE_` の code の増減は「凍結の例外」。`tasks.md` に task 3.8・3.9 を足した。

### Codex の利用枠（起動の前の記録）

- 窓の開始は 2026-10-14 09:14:01、使用済みは 404,605 token。`docs/dev/agent-workflow.md` の目安（1 つの窓 50 万）では残り 約 9.5 万で、群 3 の見積もり（実装の 1 段階 25 万）は収まらない。
- 判断: ユーザーの言葉（上）により、目安を超えて起動する。1 本ごとの時刻と `tokens used` は、これまでどおり下へ書く。容量切れ（`Selected model is at capacity`）や利用上限の拒否が出たら、契約を書き換えずに止め、ユーザーに伝える。

### 仕組み（群 3 の分け方。Opus の決定）

- task 3.2・3.3・3.4 は、契約 1 本（`.ccg/tasks/resoloop-slimming/cc-g3-freeze-contract.md`）にして `gpt-6-luna` high へ渡した。報告は同 `cc-g3-freeze-report.md`（同じ内容が slot3 `TestResults/CC-G3/freeze-report.md`）、log は同 `-codex.log`、時刻は同 `-times.txt`。
- task 3.5（既存の test の実行と表）と 3.6（`AGENTS.md`・`docs/dev/agent-workflow.md`）は Opus が行った。3.6 は開発エージェント向けの文書だけの変更で、製品コードではない。
- 下調べは Claude の調査役 2 本（読み取りだけ。slot3 `85f8ad5`）。事前確認（slot3、sandbox の外、11:21〜11:26）の log は slot3 `TestResults/CC-G3/opus/`: 契約の build のコマンドは終了 code 0・警告 0・エラー 0／filter `FullyQualifiedName~ApplyWorkflowTests|…LegacyCommandsRejectCreationEnvelopeWithoutMode` は失敗 0・成功 471。

### 計画と実際の違い（契約で合わせた点）

| 計画（`tasks.md`） | 実際 | 契約での扱い |
| --- | --- | --- |
| 3.2 (i) は 44〜69 行 | 検査の終わりは 68 行（69 行は `ApplyDocument.Load`）。(ii)・(iii) は行番号どおり | end の目印は 68 行の直後 |
| 3.3 の確かめ方の filter `OrdinaryApplySurface` | そのままで一意 | 変更なし |
| 3.4 の test の名前は `StateV3FromThisVersionPlansNoChanges`、filter `OrdinaryApplyFrozenContract` で成功 1 | 偽の client `FakeResoniteClient` は `ApplyWorkflowTests` の private な入れ子の class。新しいファイルは `partial class ApplyWorkflowTests` にするしかなく、class の名前では filter に一致しない | test の名前を `OrdinaryApplyFrozenContractStateV3FromThisVersionPlansNoChanges` にした |
| 3.4「同じ world を持つ偽の client」 | 偽の client に、state から world を組み立てる仕掛けは無い。ID は `S1`・`C1` からの連番で決まる | test の中で、同じ定義を別の一時の state へ 1 回 `apply` して world を作り、その後 fixture の state で `plan` する |
| 3.5 の `SameSessionComponentIdentityTests`・`StoredIdIdentityTests` は class | どちらもファイルの名前で、中身は `ApplyWorkflowTests` の partial（test の method は 7 つで、数は計画どおり） | 表は method の名前で書いた |
| （計画に無い） | filter `FullyQualifiedName~OrdinaryApply` は、既存の `CreationSiblingNamesTests.OrdinaryApplyCliRejectsDuplicateSiblingNamesBeforeAnySend`（3 件）にも一致する | 契約の filter を `…~OrdinaryApplySurface\|…~OrdinaryApplyFrozenContract` にした |

### task 3.5: `specs/ordinary-apply/spec.md` の requirement を確かめている既存の test

実行（Opus、slot3 `85f8ad5`、sandbox の外、11:23 ごろ）: `dotnet test tests/RLoop.Tests --no-build --filter "FullyQualifiedName~ApplyWorkflowTests|FullyQualifiedName~CreationModeBoundaryTests.LegacyCommandsRejectCreationEnvelopeWithoutMode"` は失敗 0・成功 471・skip 0。下の名前は、結果のファイル（slot3 `TestResults/CC-G3/opus/cc-g3-3.5.trx`）で 1 つずつ `Passed` を確かめた。class はどれも `RLoop.Tests.ApplyWorkflowTests`（最後の 1 つだけ `CreationModeBoundaryTests`）。既存の test は変えていない。

| requirement | scenario | test（ファイル） | 件数 |
| --- | --- | --- | --- |
| 通常の apply は凍結する | option・error code を足す | 新しい `OrdinaryApplySurfaceTests`（task 3.3。下の結果） | — |
| 既存の文書と state を読み続ける | 以前の state v3 で `plan` | 新しい `OrdinaryApplyFrozenContract…`（task 3.4。下の結果） | — |
| 同上（v1・v2） | — | `S3V1V2EmptyIdsRemainUnprovedLegacyCreations`（`ApplyWorkflowTests.S3I2.cs`） | 4 |
| 記録した ID での照合（P09） | 同じ session で再 apply | `RecordedIdsContinueMatchingTwinsOnlyInTheSameSessionAndPlanDoesNotWriteState`（`SameSessionComponentIdentityTests.cs`） | 1 |
| 同上 | session が変わった／確かめられない接続 | `UnprovenOrChangedSessionKeepsAmbiguousStop`、`ChangedSessionDoesNotPersistProofWhenStateHasUnverifiedComponentRecord`（同） | 3、1 |
| 同上 | 条件 (b)・(c) | `OldStateWithoutSessionAndMissingOrDuplicateRecordedIdsStillStop`、`MissingStoredIdAmongAmbiguousCandidatesStops`、`IdentityFieldsReestablishSessionProofAndFailedApplyDoesNotPersistIt`（同） | 各 1 |
| 同上 | 途中で失敗した後 | `FailedWriteCheckpointDoesNotKeepSessionAndNextApplyStopsAmbiguous`（同）、`RootUpdateFailureClearsSessionAndRetryStopsAmbiguous`（`SameSessionInterruptedApplyTests.cs`） | 1、2 |
| 同上 | state の ID を入れ替えた（条件 (d)） | `SwappedRecordedIdsStopAmbiguous`、`StateWithoutBindingStopsAmbiguous`、`CompletedApplyWritesBindingAndInterruptedApplyDoesNot`（`SameSessionBindingTests.cs`） | 各 1 |
| 同上 | 片方を prune で消そうとする | `PruneNeverSelectsByRecordedId`（`SameSessionInterruptedApplyTests.cs`） | 1 |
| 結果の分からない書込みが残っていれば止まる | 保留が残っている | `PendingCheckpointStopsWhenCreateResponseIsLost`（`ApplyWorkflowTests.cs`） | 1 |
| 削除は所有を確かめた正確な ID だけ | 記録した ID の先が変わっていた | `S3OwnedSubtreeDeletesOnlyExactIdsAndReadbacksAbsence`（`ApplyWorkflowTests.S3I3.cs`）、`PruneDoesNotDeleteAnObjectBecauseItOnlyHoldsAStoredId`、`PruneDoesNotDeleteAComponentThatOnlySharesTheStoredId`（`StoredIdIdentityTests.cs`） | 各 1 |
| 作り直しの定義は通常の apply で受け付けない | mode を付け忘れた | `CreationModeBoundaryTests.LegacyCommandsRejectCreationEnvelopeWithoutMode`（`plan`・`diff`・`apply`） | 3 |

- 「session が変わった」の scenario は「Resonite を開き直す」形で、偽の client の上では S-ID・URL を変える形で確かめている。実機では L4 の U01 が、URL の直接指定の側だけを確かめた（開き直しは U10）。

### 実行の記録と受け入れ（task 3.2〜3.4・3.6・3.7）

| 契約 | 時刻 | token | 担当の報告 | Opus の検証（slot3、sandbox の外、11:38〜11:42） |
| --- | --- | --- | --- | --- |
| cc-g3-freeze v1 | 11:26:55〜11:38:11 | 120,861 | `DONE`（build 警告 0・エラー 0、新しい test 3 pass、error code 68 件・option 27 件で照らし合わせ用の値と一致、検出の確認は「足すと失敗・消すと成功」。sandbox の中の `ApplyWorkflowTests` は 469 pass・失敗 0 で、契約の期待 472 より 3 少ない） | 受け入れた。下の表 |

| 検証 | 結果 |
| --- | --- |
| `dotnet build ResoLoop.slnx --no-restore --no-incremental …` | 警告 0、エラー 0 |
| filter `…~OrdinaryApplySurface\|…~OrdinaryApplyFrozenContract` | 失敗 0、成功 3 |
| 検出の確認（`WorldService.cs` の末尾に `// "APPLY_PIN_PROBE"` を足す → 戻す） | 足すと `ErrorCodesMatchPinnedList` が失敗（`Implementation only: [APPLY_PIN_PROBE]`）、戻すと成功。`git diff --stat -- src/RLoop.Core` は空 |
| RLoop.Tests（全体、1 回） | 失敗 0、成功 2013、skip 4（基準 2010 + 3） |
| `rg -c "ordinary-apply-surface:" src/RLoop.Cli/Program.cs` | 6。`Program.cs` の差分はコメント 6 行だけ |
| `AGENTS.md` の「凍結した経路」／`agent-workflow.md` の「凍結の例外」 | 1 件／1 件。差分に `skills/`・`README*` は無い |

- log は slot3 `TestResults/CC-G3/opus/`（`accept-build.log`、`accept-ordinary.log`、`accept-probe-added.log`、`accept-probe-removed.log`、`accept-full.log`・`.trx`）。
- commit: `agent/course-correction` の `91e88b8`（`test(apply): pin the frozen surface of the ordinary apply`。9 ファイル、追加 236 行）。main への merge と push はしていない。
- Opus が直した 1 点: 1 つ目の end の目印の字下げ（12 桁 → 16 桁。コメントの空白だけ）。
- 固定したもの: error code 68 件（`APPLY_` 60、`STABLE_` 8）、option の文字列 27 件。state の fixture の最上位の member は `schemaVersion`（3）・`ownershipKey`・`sessionId`・`session`・`sessionBinding`・`slots`・`components`・`assets`・`pending`。
- `AGENTS.md` は英語の文書なので、節の見出しは `## Frozen path (凍結した経路)`、本文は英語で書いた。
- この窓の使用済み: 525,466 token（404,605 + 120,861）。目安の 50 万を超えている（ユーザーの言葉による）。利用上限の拒否は出なかった。

### 未検証・実行しなかったこと

- sandbox の中で `ApplyWorkflowTests` が 3 件少なかった理由は調べていない（sandbox の外では、全体が基準 + 3 で一致した）。
- 重いレビューは実行していない（1 つの段階につき 1 回の決まりで、群 7 の 7.2 で `main..agent/course-correction` を見る）。ccg の `verify-change` は実行したが、「変更 8 ファイル、+0／−0、通過」と返しただけで、検査の根拠にならない。
- Workbench・Integration・`npm` の検証は、この群では実行していない（変えていない。群 4 の 4.1 と群 7 の 7.1 で実行する）。
- RLoop.Tests の全体は 1 回だけ実行した。不安定な 2 件（U13・U23）は、この 1 回では出なかった。

### 次

- 群 4（Workbench を除く）: task 4.1 で、除く前の基準（`rg -l -i workbench …` の結果と、RLoop.Tests の件数 = 成功 2013・skip 4）を取る。slot3 の HEAD は `fdf1f74`。

### 固定の範囲を広げた（task 3.8・3.9。2026-10-14 11:50〜12:17）

調査（Claude の調査役 1 本、読み取りだけ、slot3 `91e88b8`）: 6 ファイルのほかに `"APPLY_`・`"STABLE_` を持つ `src/RLoop.Core/` の 18 ファイルを、呼び出し元で仕分けた。

| 分類 | ファイル | 扱い（ユーザーの決定） |
| --- | --- | --- |
| A: 通常の経路だけが使う | `ApplyCatalogValidator.cs`、`ApplyBuildBundle.cs`、`ComponentIdentityDiagnostics.cs` | 凍結する。code も固定する |
| B: 作り直しの経路と共用 | `ApplyDocumentCompiler.cs`（`CreationDocument.cs:123` が `CompileBundleIr` を呼ぶ）、`ApplyProjectContext.cs`、`SessionWriteLock.cs`、`StableReferences.cs`、`CheckpointFiles.cs` | 凍結しない。`APPLY_`・`STABLE_` の code だけ固定する |
| C: 作り直しの経路だけ | `Creation*.cs` の 7 つ | 対象外 |
| D: そのほか | `CatalogTypesGenerator.cs`（`catalog` コマンド）、`ApplyDiagnostics.cs`（code の比較だけ） | 対象外 |

- 固定した error code は 68 → 100 件（`APPLY_` 92、`STABLE_` 8）。前の 68 件は 1 つも変わっていない。増えた 32 件の大半は `ApplyDocumentCompiler.cs` の定義の検査の code。
- 契約 `cc-g3-codes-contract.md`（`gpt-6-luna` high、12:07:40〜12:09:41、46,353 token、報告は `DONE`）。変えたのは `OrdinaryApplySurfaceTests.cs` の一覧と `error-codes.txt` だけ。`AGENTS.md` の「Frozen path」は Opus が直した（凍結 9 つと、共用 5 つを分けて書いた）。
- Opus の検証（slot3、sandbox の外、12:10〜12:16。log は slot3 `TestResults/CC-G3/opus/accept2-*.log`）: build 警告 0・エラー 0／固定したファイルは、Opus が別に `grep` で数えた集合と一致／`ApplyDocumentCompiler.cs` の末尾に `// "APPLY_PIN_PROBE"` を足すと `ErrorCodesMatchPinnedList` が失敗し、戻すと `git diff --stat -- src` が空。
- **RLoop.Tests の全体は、1 回目が失敗 1・成功 2012・skip 4、2 回目が失敗 0・成功 2013・skip 4。** 1 回目の失敗は `SessionWriteLockTests.S3RealProcessKillReleasesHandleButPreviousPendingStillBlocks`（U13 の再発。`SessionWriteLock.cs:41` の `IOException`）。この変更は test の一覧と固定したファイルだけで、lock のコードにも、その test にも触れていない。`openspec/unverified.md` の U13 に書き足した。「slot3 の RLoop.Tests は失敗 0」とは言えない。
- commit: `agent/course-correction` の `fdf1f74`（`test(apply): pin the error codes of the files shared with regenerate`）。
- この窓の使用済み: 571,819 token（525,466 + 46,353）。
- 残る範囲の外: `src/RLoop.Cli/Program.cs` が直接投げる `APPLY_CATALOG_UNAVAILABLE`・`APPLY_WRITE_UNVERIFIED`（どちらも一覧にある code）と、`test` コマンドの `APPLY_TEST_FAILED`（一覧に無い。`apply`・`plan`・`diff` の code ではない）。`src/RLoop.ResoniteLink/` の `CatalogCapture.cs`・`CatalogMapper.cs`・`ResoniteLinkClientAdapter.cs` の code も一覧にあるが、これらのファイルは test の集める対象ではない。調査役は、`RLoopException` 以外の投げ方（`FailAlias`・`Error(`・`Fail(` の helper 経由）を 1 行ずつは確かめていない。

### 気付いた点（群 3 の報告の時点。1 つ目は、上の「固定の範囲を広げた」で扱った）

- **固定する error code の一覧は、通常の `apply` が返しうる code の全部ではない。** 計画どおり 6 ファイルから集める（68 件）。`src/RLoop.Core/` には、ほかに `"APPLY_`・`"STABLE_` を含むファイルが 18 ある（調査役の数え。`ApplyDocumentCompiler.cs` 30 行、`StableReferences.cs` 8 行、`ApplyCatalogValidator.cs` 6 行、`SessionWriteLock.cs` 5 行 ほか。`Creation*` は作り直しの側）。定義の検査や参照の解決が返す code に足し引きがあっても、`OrdinaryApplySurfaceTests` は気付かない。spec の文（「返しうる error code の一覧は、固定 SHALL」）より狭い。凍結するファイルを増やすかは、計画の決定なので変えていない。
- 固定する option の一覧（27 件）には、option でない文字列（コマンドの名前 `apply`・`plan`・`diff`・`validate`・`flux`、操作の種類 `create`・`delete`、辞書の key `filters`）が混ざる。目印の中の文字列を正規表現で集める、という計画の形のとおり。

## 2026-10-14 11:15 JST — 群 2 を終えた（task 2.8・2.9。`agent/better-b2c` を main へ merge した）

### 伺いと返事

- 2026-10-14 10:55 ごろ、L4 の結果、レビューと修正、offline の検証、残る未検証を示して、「`agent/better-b2c`（`ba57564`、9 commit）を main へ `--no-ff` で merge してよいか。merge の後、main で build と test を実行し、手元の tag `pre-workbench-removal` を付ける。push はしない」と伺った（選択肢から選ぶ形）。返事:「merge する」。

### 確認済みの事実

- merge: `b88ed8a`（`Merge branch 'agent/better-b2c': close B2c-1 (review fixes, P10 and P11 tests)`）。変更元は `03479cf`、49 ファイル、+1509／−66、競合なし。手元の tag `pre-workbench-removal` は `b88ed8a` を指す。push はしていない。
- main での検証（Opus、sandbox の外、`RESOLOOP_RUN_INTEGRATION` なし、Resonite は起動中。10:56〜11:09）。log は `.ccg/tasks/resoloop-slimming/cc-main-merge-*.log`。

| 検証 | 結果 |
| --- | --- |
| `dotnet build ResoLoop.slnx --no-incremental` | 警告 0、エラー 0 |
| RLoop.Tests（全体、1 回目） | **失敗 1**、成功 2009、skip 4。失敗は `FluxDeployEntryTests.WatchHoldsTheLockOnlyDuringADeploymentAndStopsWhenTheNextOneFindsItBusy`（`main.pg` が使用中の `IOException`） |
| RLoop.Tests（全体、2 回目） | **失敗 1**、成功 2009、skip 4。失敗は `SessionWriteLockTests.S3RealProcessKillReleasesHandleButPreviousPendingStillBlocks`（U13 の再発） |
| RLoop.Tests（全体、3 回目） | 失敗 0、成功 2010、skip 4 |
| `FluxDeployEntryTests` だけ（3 回） | 3 回とも失敗 0、成功 32 |
| RLoop.Workbench.Tests | 失敗 0、成功 190 |
| RLoop.IntegrationTests | 失敗 0、「成功」23（実機の要る test は、実行せずに return して成功と数えられている。実機では実行していない） |
| `npm.cmd run typecheck` | 終了 code 0 |
| `npm.cmd test` | tests 336、pass 336、fail 0 |
| `npm.cmd run contract` | 終了 code 0。64／11／4／2、失敗 0 |

- 失敗した 2 つの test と、その対象のコード（`SessionWriteLock.cs`、`src/RLoop.Flux`、2 つの test のファイル）は、この merge で変わっていない（`git diff --name-only 03479cf b88ed8a` で 0 件）。どちらも、一時 directory のファイルを別の process・thread がまだ開いている瞬間に当たった形。原因は調べていない。`openspec/unverified.md` の U13 に再発を、U23 に新しい 1 件を「観察中」で書いた。**3 回のうち 2 回で失敗が出たので、「main の RLoop.Tests は失敗 0」とは言えない。** 群 3 の基準の件数は、成功 2010・skip 4（合計 2014）とする。
- `openspec/roadmap.md` の R10・R11 を「完了」にし、根拠に `b88ed8a` を書いた。Notion の「現在地と引き継ぎ」を直した（現在地の callout、あらすじに 1 行、未検証の一覧の範囲）。`.ccg/tasks/resoloop-slimming/task.json` の案内を直した。

### 後回しにした項目

- 不安定な test 2 件（U13・U23）の原因の調査。slot1 の `agent/lock-flake-diag`（`d9367f2`）は未 merge のまま。
- TSX の build が、`{"type":"module"}` の `package.json` の無い場所で `ROOT_MUST_BE_SINGLE_SLOT` を出す件（L4 の実行担当の報告。再現していない）。

### 未検証

- 修正の後の差分の重いレビューは実行していない。群 7 の 7.2 の対象は `main..agent/course-correction` なので、そのままでは修正の分（`061b054..ba57564`）が入らない。7.2 の指示書の対象に足すかを、そのときに決める。
- 新しい clone での検証（群 7 の 7.1）。
- 主の入口 `resoloop-jsx` から import した TSX の実機での作り直し（L5、task 7.4 (i)）。

### 次

- task 3.1 は済み（11:12）: slot3 で `git switch -c agent/course-correction main` を実行した。branch は `85f8ad5`（main の、この記録の 1 つ前の commit）から出ている。未追跡の `examples/object-lamp-a/`・`-b/` は残っている。
- task 3.2〜3.6: 群 3 の実装は Codex `gpt-6-luna` high へ渡す。前の窓は 2026-10-14 09:14 開始、使用済み 404,605 token で、残りは約 9.5 万。実装の 1 段階の目安は 25 万なので、**この窓では起動しない**。窓は 14:14 ごろに閉じる見込み。起動の前に、新しい窓の開始時刻・使用済み・見積もりをこの文書に書く。

## 2026-10-14 11:00 JST — task 2.6・2.7 を終えた（実機 L4: U01〜U05 はすべて PASS）

### L4 の結果（実機。session「ぞかホーム」`S-01a13803-91cf-76b4-b635-eb730117be13`、`ws://localhost:25410/`、Resonite `2026.9.18.82`、ResoniteLink `0.13.1.0`。テスト用の親 `ResoLoop_Test_L4` = `Reso_30FA2`。10:35:32〜10:50:01）

実行は Claude の `coding-worker`（実効のモデルは `claude-sonnet-5-5`）、CLI は `live-1012\bin5\resoloop.exe`（slot3 `ba57564`）。報告は `.ccg/tasks/resoloop-slimming/cc-live-l4-report.md`、証拠は `resoloop-worktrees/live-1012/evidence/L4`（77 ファイル。`ledger.md` に ID）。失敗・未実施の項目は無い。

| 項目 | 判定 | 要点 | 証拠 |
| --- | --- | --- | --- |
| U02 | PASS | TSX（key はすべて明示）を `--target creation` で build → 作成（`action` `create`）→ `Intensity` を 1.5 から 2.5 に変えて置き換え（`replaced` true）。親の直下の子は 1 つ、古い root は `SLOT_NOT_FOUND` | `032`〜`038` |
| U03 | PASS | key を省いた TSX。割り当てられた key は `root/l4u03#0`、`…/alpha#0`、`…/beta#1`。作成と置き換えが成功 | `041`〜`046` |
| U04 | PASS | 同じ名前 `Lamp` 3 つ（key は `room/lamp#0`〜`#2`）。値 1・2・3 → 置き換えの後 1・7・3 で、3 つのまま | `051`〜`056` |
| U05 | PASS | 先に置いた `L4U05`（`Reso_3194B`）と子 `Marker`（`Reso_31956`）は、作成と置き換えの後も同じ ID で残った。置き換えの `previousRootId` は作り直しの root（`Reso_31969`）で、先客ではない。親の直下の `L4U05` は常に 2 つ | `061`〜`068` |
| U01 | PASS | 同じ Slot に `Light` 2 つ。同じ session の再 `plan`・`apply` は変更 0 件で、Component の ID も同じ。`--url ws://localhost:25410/` では `plan`・`apply` とも `STABLE_COMPONENT_AMBIGUOUS`（終了 code 6、`sessionProof` `unknown`）で書込みなし。その後の `plan` は変更 0 件 | `071`〜`079` |
| Z | PASS | `Reso_30FA2` を正確な ID で削除。`find --name ResoLoop_Test` 0 件 | `090`〜`093` |

Opus が確かめたこと（10:50〜10:55、読み取りだけ）: `discover` は同じ 1 件／`find --name ResoLoop_Test` と `find --name L4U` はどちらも 0 件／証拠のファイルの時刻が 10:35〜10:50 に並んでいる／`061`・`062`・`066`・`067`・`074`・`076`・`077`・`091` の出力を読み、上の要点と一致する／`065`・`068` の `inspect` に、先客と `Marker` の ID と、名前 `L4U05` が 2 つある。ほかの証拠は、実行担当の報告のとおりとして受け取った（Opus は開いていない）。

### L4 で分かったこと（不具合ではない。記録だけ）

- TSX の import 元は、5 項目とも `resoloop-jsx/draft`（fixture に合わせた）。主の入口 `resoloop-jsx` からの import は、実機では使っていない。Opus が offline で、U02 の TSX の import 元だけを `resoloop-jsx` に替えて build した: 終了 code 0 で、出力の違いは `source` のファイル名だけ。主の入口で key を省く形は、群 5 の後に L5（task 7.4 (i)）で確かめる。
- 作業場所に `{"type":"module"}` の `package.json` が無いと、TSX の build が `ROOT_MUST_BE_SINGLE_SLOT` で失敗した（原因と結びつかないメッセージ。実行担当の報告。Opus は再現していない）。
- 作り直しの `apply` は、成功しても `phase` が `staged` で返る（L3 の `012_M3_apply.txt` と同じ）。`pending` は null、`engineAddedComponents` は 0。
- 通常の `apply` で作った Slot には、`FrooxEngine.AI_GeneratedContent` の Component が付き、state でも管理される（`074`。`componentsUnchanged` は 3）。
- U01 の `plan` は、同じ session でも警告 `APPLY_COMPONENT_IDENTITY_RISK` を出す。
- 残っているもの: `work-l4` の `u01`〜`u05-state.json.lock` 5 つ、`node_modules` の junction、`.resoloop/cache/reflection`。pending を持つ state は無い。

### 次

- task 2.8: `agent/better-b2c` を main へ merge してよいかを伺う。

## 2026-10-14 10:30 JST — task 2.6 の途中（実機 L4 の許可を得た。対象の session はまだ決まっていない）

### 伺いと返事

- 2026-10-14 10:25 ごろ、ユーザーが `/ccg:spec-impl` を実行した。Opus は slot3（`agent/better-b2c`、`ba57564`、追跡ファイルの変更なし）の build で `discover --json` を実行した（読み取りだけ）。結果は session 0 件（探索 12 秒）。
- 2026-10-14 10:28 ごろ、「実機テスト L4（U01〜U05。書込みは `ResoLoop_Test_L4` の下だけ、終わりに片づけ）を実行してよいか。いま discover は 0 件」と伺った（選択肢から選ぶ形）。返事:「許可する（これから起動）」= ユーザーが Resonite を起動して ResoniteLink を有効にした後、Opus が `discover` をやり直す。1 件ならその session を対象にし、2 件以上なら対象を改めて伺う。

- 2026-10-14 10:31 ごろ、ユーザー:「resonite linkを有効にしました」。

### 確認済みの事実（Opus が slot3 の build で読み取りだけを実行。2026-10-14 10:31〜10:33）

- `discover --json`: session は 1 件。名前「ぞかホーム」、URL `ws://localhost:25410/`、S-ID `S-01a13803-91cf-76b4-b635-eb730117be13`。1 件なので、これを L4 の対象にする。
- `status`: Resonite `2026.9.18.82`、ResoniteLink `0.13.1.0`、connected true。
- `find --name ResoLoop_Test`: 0 件（L4 の前に残っているテスト用の Slot は無い）。
- slot3 `ba57564` を `dotnet build ResoLoop.slnx` で build し（警告 0・エラー 0）、出力 `src/RLoop.Cli/bin/Debug/net10.0` を `resoloop-worktrees/live-1012/bin5` へ複製した（172 ファイル）。L4 はこの `bin5\resoloop.exe` だけを使う。

### 仕組み（L4 の進め方）

- 指示書は `.ccg/tasks/resoloop-slimming/cc-live-l4-charter.md`。「事前確認」の表に、Opus が offline で実行したコマンドと終了 code がある（help、fixture `creation-keys/main.tsx` の `--target creation` の build、`validate`）。実機で実行していない書式（`plan`／`apply` の `--mode regenerate`、`slot create`／`delete`、`inspect`、URL の直接指定）は、そう明記した。
- 下調べは Claude の調査役 2 本（読み取りだけ）。分かったこと: 実機で使える TSX の fixture は無い（fixture の型 `Probe` は test 用）ので、実行担当が実在の型で TSX を書く／同じ名前の兄弟の fixture は 2 つまでで、3 つは実行担当が書く／root の照合は名前ではなく、state の ID・親・印による（`CreationOwnershipVerifier.cs`）／slot3 の未追跡の `examples/object-lamp-a/`・`-b/` には `node_modules/resoloop-jsx` の link しか無い。
- 2026-10-14 10:35 ごろ、Claude の `coding-worker` を起動した。順序は S → U02 → U03 → U04 → U05 → U01 → Z（片づけ）。証拠は `resoloop-worktrees/live-1012/evidence/L4`、報告は `.ccg/tasks/resoloop-slimming/cc-live-l4-report.md`。

### 未検証

- この節を書いた時点では、U01〜U05 は「未検証」だった（結果は上の 11:00 の節）。

## 2026-10-14 09:55 JST — task 2.4・2.5 を終えた（レビューの指摘 4 件と help を直し、P10・P11 の test を足した）

### 伺いと返事

- 2026-10-13 22:00 に伺った 2 点（修正に入るか／Codex をどこまで使ってよいか）への、言葉での返事は無かった。2026-10-14 08:57 ごろ、ユーザーが `/ccg:spec-impl` を実行した。Opus は、これを「task 2.4 に入ってよい」として受け取った（Opus の解釈で、ユーザーの言葉ではない）。
- 2026-10-14 09:13 ごろ、契約の一覧・見積もり・Opus の決めた 5 点を示して、Codex をどこまで起動してよいかを伺った（選択肢から選ぶ形）。返事:「5 本まで (Recommended)」= fix2a・b・c・d・f（約 30 万 token）を順に起動する。P10・P11 の test（fix2e、約 12 万）は、残りを見てから改めて伺う。Opus の決めた 5 点への異論は無かった。

### 確認済みの事実（指摘の裏取り。Claude の調査役 4 本が slot3 `061b054` のコードを読んだ結果と、Opus が読んだ範囲）

- 指摘 1〜4 は、どれもコードと合う。レビュー担当の再現の log（slot3 `TestResults/BETTER-B2C/review-final/repro-a-run1.log`、`repro-a-verified-run2.log`、`-run3.log`）は Opus が読んだ。Opus は再現を実行し直していない。
- 指摘 1: ID での選択の条件は、`sessionProof == "matched"`、記録 ID が候補の中、ID の重複なしの 3 つだけ（`WorldService.cs:1863-1873`、`1931-1938`）。state に、対応を確かめる hash のたぐいは無い（`ApplyWorkflow.cs:692-709`）。
- 指摘 2: `ExecutePendingAsync` の送信の前の保存は `ApplyStateStore.Copy(prepared.State)` で、`session` を写す（`WorldService.ApplyPending.cs:51-53`）。`Checkpoint` だけが消す（`WorldService.cs:2254`）。再現の log: root の更新の拒否は「保留 0・`session` あり・再 `apply` 成功（書込み 3）」、取消は「保留 1・`session` あり・再 `apply` 成功（書込み 2）」、応答の喪失は「保留 1・再 `apply` は `APPLY_WRITE_UNVERIFIED`」。
- 指摘 2 のうち state の保存の失敗（`state-write-failure`）: 最初の保存が失敗して `APPLY_STATE_WRITE_FAILED` で止まり、何も送信していない。古い記録は実機と合ったままなので、直さない（書けないファイルの記録は消せない）。
- 指摘 3: prune の照合は `MatchComponent` を通る（`WorldService.cs:1379`）。調査役が見つけた追加: その直前の `ResolveStateTopologyTargets`（1378 行）から `ResolveStateComponent` に入る経路でも、ID での選択が効く。契約では両方を止める。`711ef87` の前は、候補が 2 つ以上なら無条件に `STABLE_COMPONENT_AMBIGUOUS` だった。
- 指摘 4: 検査は Scope を適用する前の key（`evaluate.ts:451-457`）、`creationId` は適用した後の key（`evaluate.ts:462`、`cli.ts:219`）。B2c-1 の指示書（`better-impl-b2c-charter.md:52-57`）と spec は、Scope の下の root を決めていなかった。C# は `creationId` を `definition.ownership.key` とだけ比べる（`CreationDocument.cs:118-119, 211`）。
- help: 古い文は `Program.cs` の 4 か所（1623・1634・1649・1785 行）。README 4 つと配布する Skill は、すでに TSX の入口を説明している。`CreationRegenerateUsageTests.cs:27,29` が、古い文のうち 2 つを assert している。
- 文書: `README.md:44`、`README_JA.md:147`、`README-DETAILS.md:261` は、「実機で未検証」を作り直しの全体について書いたまま（同じファイルの後ろの行は、TSX の経路と同じ名前の兄弟に限っている）。
- P10・P11: 前回読まなかった 4 つの test ファイルは、穴のどれも確かめていない。穴の (1)〜(8) は、偽の client `CreationMutationTestClient` の公開の member だけで書ける。

### 仕組み（Opus が契約で決めた形。一覧と理由は `.ccg/tasks/resoloop-slimming/cc-b2c-fix2-contract.md`）

- 契約は 6 本（`cc-b2c-fix2a`〜`fix2f-contract.md`）。順に 1 つずつ `gpt-6-luna` high へ渡す: a = 指摘 2・3、b = 指摘 1、c = 指摘 4、d = help、f = 文書、e = P10・P11 の test（task 2.5）。
- 指摘 1: state に省略できる `sessionBinding`（Component ごとの key・SlotKey・Slot の ID・Component の ID を並べた JSON の SHA-256）を足す。`SchemaVersion` は 3 のまま。最後まで成功した `apply` だけが書き、読むときに計算し直して、違えば記録 ID では選ばない。error code・option・`sessionProof` の値は増やさない（群 3 の「凍結の例外」には当たらない）。
- 指摘 4: レビュー担当の案を採った。`creationId` は、検査した root の明示の key（Scope を適用する前）から出す。
- help の「実機で未検証」は、TSX の経路と同じ名前の兄弟、回復の `finish` の成功に限る（手書きの JSON の定義での作り直しは L1・L3 で確かめたため）。
- spec を 2 か所直した: `specs/ordinary-apply/spec.md` の「記録した ID での照合」に条件 (d) と、途中で止まった `apply` の後・prune では使わないこと、scenario 3 つ／`specs/creation-regenerate/spec.md` に、Scope で包んだ root の `creationId` と scenario 1 つ。
- 事前確認（2026-10-14 09:02〜09:06、slot3、sandbox の外）: `ApplyWorkflowTests`＋`StoredId` 462、`Creation` 308、`CreationRegenerateUsage` 1、`Skill` 16、`npm.cmd test` 335、`codex exec --help` 終了 code 0。log は slot3 `TestResults/BETTER-B2C/opus/fix2-precheck/`。

### Codex の利用枠

- 前の窓は 2026-10-13 21:34:52 に開き、使用済みは約 21.6 万。2026-10-14 02:35 ごろに閉じた見込み。次の起動が新しい窓の開始になる（使用済み 0、残り 50 万）。
- 見積もり: 6 本で約 42 万 token（a 8、b 8、c 5、d 5、f 4、e 12。同じ形の fix1 が約 5 万だったことからの目安で、実測ではない）。残りには収まるが、修正の出し直しの余裕は約 8 万しかない。
- 判断: ユーザーの返事（上）で、5 本（見積もり 約 30 万）を起動する。残り 50 万に収まる。fix2a の起動の時刻を、新しい窓の開始とする。起動と終了の時刻は `.ccg/tasks/resoloop-slimming/cc-b2c-fix2<x>-times.txt`、log は同 `-codex.log`。1 本ごとの `tokens used` を、下へ書き足す。担当が動いている間、slot3 では Opus は build と test を実行しない。

### 実行の記録（この窓。開始は 2026-10-14 09:14:01 JST）

| 契約 | 時刻 | token | 担当の報告 | Opus の検証（sandbox の外） |
| --- | --- | --- | --- | --- |
| fix2a v1 | 09:14:01〜09:15:40 | 80,100 | `BLOCKED`（restore がネットワークの制限で失敗し、test を実行できなかった） | 製品コードの差分は契約どおり（3 ファイル、17 行の追加・11 行の削除）。build はエラー 1 件: 新しい test の 64 行が `JsonObject.Values`（無い member）を使う（CS1061）。fix2a の test は 1 件も実行できていない |
| fix2b v2 | 09:19:19〜09:23:13 | 69,945 | `BLOCKED`（build 警告 0・エラー 0。467 pass／1 fail。失敗は fix2a の `RootUpdateFailureClearsSessionAndRetryStopsAmbiguous("cancelled")` で、例外が出なかった。fix2b の 3 件と、fix2a の残り 2 件は成功） | 同じ結果を再現した（build 警告 0・エラー 0、467 pass／1 fail。log は slot3 `TestResults/BETTER-B2C/opus/fix2b-*.log`）。製品コードの差分は契約どおり（4 ファイル） |
| fix2c v1 | 09:26:03〜09:27:22 | 49,151 | `DONE`（`npm.cmd test` 336 pass） | 受け入れた。`npm.cmd test` は tests 336・pass 336・fail 0（log は slot3 `TestResults/BETTER-B2C/opus/fix2c-npm-test.log`）。レビュー担当の再現 `repro-b-scope.mjs` を写して実行し、Scope が `part`・128 文字・制御文字つきの 3 つとも、`creationId` が `room` になり、`resoloop validate` が終了 code 0（slot3 `TestResults/BETTER-B2C/opus-fix2c/run1.log`）。`agent/better-b2c` へ `baed047` で commit した |
| fix2d v2 | 09:28:30〜09:31:52 | 47,159 | `DONE`（build 警告 0・エラー 0、`ApplyWorkflowTests`＋`StoredId` 468 pass、`CreationRegenerateUsage` 1、`Skill` 16） | 受け入れた（fix2a・fix2b も、ここで受け入れた）。build 警告 0・エラー 0、`ApplyWorkflowTests`＋`StoredId` 468 pass・失敗 0、`CreationRegenerateUsage`＋`Skill` 17 pass（log は slot3 `TestResults/BETTER-B2C/opus/fix2d-*.log`）。`agent/better-b2c` へ commit した: `403c100`（fix2a・fix2b。製品 4 ファイルと test 2 ファイル）、`9ecc1c2`（help と、その test） |
| fix2f v1 | 09:34:24〜09:35:53 | 34,564 | `DONE`（5 ファイル、6 行の追加・5 行の削除。`Skill` 16 pass） | 差分は契約の 6 文だけ（`git diff --check` は終了 code 0）。受け入れは、下の全体の検証で行う |

- この窓の使用済み: 280,919 token（a 80,100、b 69,945、c 49,151、d 47,159、f 34,564）。承認の範囲（5 本、約 30 万）に収まった（見積もりの合計は 8+8+5+5+4 = 30 万）。出し直しの起動は無い（fix2a の 2 つの直しは、fix2b と fix2d の契約に入れた）。この窓の残りは約 21.9 万。
- 取消の場合（`"cancelled"`）は、準備を直した後に通った: 取消の後の state に `session` が無く、再 `apply` は `STABLE_COMPONENT_AMBIGUOUS`・`sessionProof` は `notRecorded`・書込み 0。
- fix2c の再現で見えたこと（直していない。この修正の前からの挙動で、`ownership` を export した場合と同じ）: root の Slot の key は Scope つきのままなので、134 文字の key や制御文字を含む key（`part\u0001::room`）が定義に入り、`validate` はそれを通す。
- **`"cancelled"` の失敗は、test の準備の不足**（Opus の契約が書き落とした）。偽の client の `CancelAfterWrites = 1` は書込みの数を初回の `apply` から数えるので、初回の後に `client.ResetWriteCounts()` を呼ばないと取消が起きない（レビュー担当の再現 `repro-a.cs:54` は呼んでいる）。製品コードの不具合を示すものではないが、この場合の修正の後の挙動は、まだ確かめられていない。直し（1 行）は fix2d v2 の手順 0 に入れた。
- **fix2a の `BLOCKED` は Opus の契約の誤り**。検証のコマンドに `--no-restore` を付けなかった。事前確認を sandbox の外でしか行わなかったので、気付けなかった。以前の契約（`apply-same-session-id-contract.md:51`）は `dotnet build ResoLoop.slnx --no-restore --no-incremental --disable-build-servers -nr:false -p:UseSharedCompilation=false` と `dotnet test … --no-build` を使っていた。fix2b（v2）・d・e・f の検証を、この形へ直した（build のコマンドの形は、sandbox の外で使い方の誤りが無いことを確かめた。sandbox の中で動くことは、fix2b の結果で分かる）。
- compile エラーの直し（1 行）は、fix2b v2 の手順 0 に入れた。fix2a と fix2b は、fix2b の後にまとめて検証する。

### task 2.4 の受け入れ（Opus、slot3、sandbox の外、実機に触れない。2026-10-14 09:36:19〜09:40:01）

`agent/better-b2c` の `2a22483` と同じ内容の作業ツリーで実行した（実行の時点の HEAD は `9ecc1c2` で、fix2f の文書 5 ファイルが未 commit。直後に `2a22483` で commit した）。log は slot3 `TestResults/BETTER-B2C/opus/accept-fix2-*.log`。

| 検証 | 結果 |
| --- | --- |
| `dotnet build ResoLoop.slnx --no-incremental` | 警告 0、エラー 0 |
| RLoop.Tests | 失敗 0、成功 2000、skip 4（変更の前は 1994。足したのは 6 件） |
| RLoop.Workbench.Tests | 失敗 0、成功 190 |
| RLoop.IntegrationTests（環境変数なし） | 失敗 0、「成功」23。このうち実機の要る test は、実行せずに return して成功と数えられている（群 6 で skip に直す対象）。実機では 1 件も実行していない |
| `npm.cmd run typecheck` | 終了 code 0 |
| `npm.cmd test` | tests 336、pass 336、fail 0（変更の前は 335） |
| `npm.cmd run contract` | 64／11／4／2、失敗 0 |

- commit（`agent/better-b2c`、変更元 `061b054`）: `baed047`（指摘 4）、`403c100`（指摘 1〜3）、`9ecc1c2`（help と、その test）、`2a22483`（文書と Skill）。main への merge と push はしていない。
- 修正の回数は 1 回（上限 4）。出し直しの起動は無い。
- 実行しなかったもの: 修正の後の重いレビュー（1 つの段階につき 1 回の決まりで、群 7 のまとめのレビューで見る）／実機（L4 は task 2.6）／新しい clone での検証（群 7 の 7.1）。ccg の変更の検査 `verify-change` は実行したが、作業ツリーの未追跡の `examples/` 2 つだけを見て「通過」と返した。commit の範囲は見ていないので、検査の根拠にならない。

### 次

- 2026-10-14 09:41 ごろ、2 点を伺った: (1) task 2.5 の契約 `cc-b2c-fix2e-contract.md`（見積もり 約 12 万 token）を起動してよいか、(2) `.ccg/tasks/resoloop-slimming/task.json` の古い案内を直してよいか。返事（原文）:「1.2は進めてください。」Opus は、1 と 2 の両方の承認として受け取った。
- (1) fix2e を 2026-10-14 09:42 に起動した。起動の前の記録: 窓の開始 09:14:01、使用済み 280,919、見積もり 約 12 万、残り 約 21.9 万に収まる。結果は「実行の記録」の表へ書き足す。
- (2) `task.json` の `currentPhase` と `nextAction` を、2026-10-14 09:45 の案内に置き換えた（262 字と 231 字。ほかの member は変えていない）。
- 次は task 2.6（実機 L4 の許可と対象の session を伺う）。

### task 2.5 の結果（P10・P11 の test。2026-10-14 09:55）

- fix2e v1: 09:42:54〜09:48:56、123,686 token、担当の報告は `DONE`（build 警告 0・エラー 0、`Creation` 318 pass）。
- Opus の検証（slot3、sandbox の外）: build 警告 0・エラー 0、`dotnet test tests/RLoop.Tests --no-build --filter "FullyQualifiedName~Creation"` は失敗 0・成功 318（変更の前は 308）、RLoop.Tests の全部は失敗 0・成功 2010・skip 4。log は slot3 `TestResults/BETTER-B2C/opus/fix2e-*.log`。`agent/better-b2c` へ `ba57564` で commit した（新しい test 2 ファイルだけ。製品コードと既存の test は変えていない）。
- この窓の使用済み: 404,605 token（280,919 + 123,686）。残りは約 9.5 万。窓は 2026-10-14 14:14 ごろに閉じる見込み。

**P10 を確かめている test**（`apply` を通し、例外が出て、`SendLog` と `RequestHistory` が空）

| 崩し方 | test | error code |
| --- | --- | --- |
| 印が違う／親が違う／印の複製（兄弟・子孫）／root の `Children` が欠ける／session が `unknown` | 既存 `CreationApplyOwnershipTests.OwnershipAndEvidenceFailuresStopBeforeAnyMutation`（6 通り） | 下の 21:35 の節の表 |
| 接続の世代が変わる | 既存 `CreationApplyOwnershipTests.GenerationChangeAtReplacementDeleteGuardPreventsDeleteAndCreate` | — |
| 記録の `GenerationId` だけが違う | 新 `CreationApplyOwnershipGapTests.RecordedGenerationMismatchSendsNothing` | `CREATION_OWNER_UNPROVEN` |
| 同じ root に印の Component が 2 つ | 新 `…DuplicateStampComponentOnRootSendsNothing` | `CREATION_OWNER_UNPROVEN` |
| root の `Components` が欠ける／子の `Children` が欠ける | 新 `…MissingListsSendNothing`（2 通り） | `CREATION_OBSERVATION_INCOMPLETE` |
| 接続先の S-ID が別／URL が別 | 新 `…DifferentSessionSendsNothing`（2 通り） | `CREATION_SESSION_UNPROVEN` |

**P11 を確かめている test**（state の無い状態から `Apply` を 2 回。間に偽の client の `SeedOwnedRoot` を挟まない）

| 崩し方 | test | 確かめること |
| --- | --- | --- |
| 同じ定義で 2 回 | 新 `CreationApplyTwiceTests.SameDefinitionTwiceLeavesOneOwnedRootWithDefinedValues` | 印を持つ root が 1 つで、state の `ActiveRoot` と同じ ID。Field の値が定義と一致 |
| 値を変えた定義で 2 回目 | 新 `…ChangedDefinitionSecondTimeLeavesOneOwnedRootWithNewValues` | 上に加えて、値が 2 回目の定義の値。1 回目の root の ID が無い |
| root と同じ名前の、印の無い Slot が親の下にある | 新 `…UnownedSameNamedSiblingSurvivesTwoApplies` | その Slot が残り、印を持つ root は 1 つ |
| 同じ名前の子 3 つ | 新 `…SameNamedSiblingsTwiceLeaveOneOwnedRoot` | 印を持つ root が 1 つで、子が 3 つ |
| 作成の応答の重複 | **test なし**（上の「後回しにした項目」） | — |

- 残る穴（Opus の見立て）: `DifferentSessionSendsNothing` は、要求の S-ID（`RequestedSessionId`）を記録の session のままにしている。止まった理由が「要求と接続先の不一致」か「記録と接続先の不一致」かを分けていない。要求の S-ID も新しい session に合わせた場合（記録だけが古い）を `apply` で通す test は無い。所有の確認だけの層では、既存の `CreationOwnershipObservationTests.SessionMustBeProvenAtStartAndEndAndConnectionGenerationMustHold` が確かめている。足すかはユーザーに伺う。
- 事前に未確認だった 2 点は、test が通ったことで確かめられた: 2 回目の `Apply` は `SeedOwnedRoot` なしで通る／同じ名前の兄弟の定義を `CreationApplyService.ApplyAsync` が通す（どちらも偽の client の上）。

### 後回しにした項目

- P11 の「作成の応答の重複」は、test を足さない（偽の client の interface では表せない。応答が不明になる場合は `CreationApplyFaultMatrixTests.NewRootUnknownReceiptRetainsCandidateContextAndNoLaterFrames` が確かめている）。`design.md` の P11 の崩し方の 1 つが、test の無いまま残る。
- `711ef87` の build が書いた state（`sessionBinding` が無い）は、同じ型 2 つの場面で、修正の後は `STABLE_COMPONENT_AMBIGUOUS` で止まる。移行の手段は用意しない（`711ef87` は main にあるが未 push）。

### 未検証

- 記録の session だけが古く、要求の S-ID は接続先と合っている場合に、作り直しが削除を送らないこと（`apply` を通す test が無い。上の「残る穴」）。
- 対応と hash の両方を書き換えた state は、`sessionBinding` では見分けられない。
- root を `defineObject` の中に置いた場合に Scope で包まれるか／`CreationApplyExecution.cs`・`CreationApplyService.cs` が root の key と `creationId` を照合していないこと（grep では出ていない）。
- `gpt-6-luna` を `codex exec` で起動するコマンドの実例は残っていない。管理役のコマンドの model だけを替えた形で起動する（option の名前は `--help` で確かめた）。

## 2026-10-13 22:00 JST — 重いレビューの結果は FAIL（task 2.3 済み、2.4 は未着手）

### 判定と使用量

- 判定: **FAIL**。A（同じ session の ID での照合、`711ef87`）は FAIL（P1 1 件、P2 2 件）。B（B2c-1、`03479cf..061b054`）は PASS_WITH_P2（P2 1 件）。P3 は 0 件。
- 報告: `.ccg/tasks/resoloop-slimming/better-impl-b2c-review-report.md`（同じ内容が slot3 `TestResults/BETTER-B2C/review-final/report.md`）。再現の script と log は slot3 `TestResults/BETTER-B2C/review-final/`。
- 実行: 2026-10-13 21:34:52〜21:58:08 JST。使用量は 216,083 token（ユーザーの上限 約 20 万を、約 1.6 万超えた）。この窓（21:34:52 開始）の使用済みは約 21.6 万、残りは約 28.4 万（予算 50 万）。
- レビュー担当は製品・test・文書を変えていない（終了後の slot3 の `git status --short` は、未追跡の `examples/object-lamp-a/`・`-b/` だけ）。

### 指摘（4 件。どれもレビュー担当が再現で確かめたと報告）

| # | 対象 | 重さ | 場所 | 起きること | レビュー担当の直し方の案 |
| --- | --- | --- | --- | --- | --- |
| 1 | A | P1 | `src/RLoop.Core/WorldService.cs:1867`（1144・1934 行も） | 成功した `apply` の state で、同じ Slot の同じ型の Component 2 つの ID だけを手で入れ替え、session の記録を残すと、`plan`／`apply` が成功し、値が逆の Component へ書かれる。さらに片方を定義から外して `--prune --yes` を付けると、残すはずの Component を消す。確かめているのは S-ID／URL、候補の中の ID、ID の重複なしだけで、key と ID の対応が、完了した `apply` の保存したものかを確かめていない | 完了のときの session の証拠を、key・SlotKey・ID の対応に結び付ける。対応が変わった state では ID での選択を無効にする。`MatchComponent` と `ResolveStateComponent` の両方に同じ検査 |
| 2 | A | P2 | `src/RLoop.Core/WorldService.cs:2253`、`WorldService.ApplyPending.cs:51` | 最初の root の更新が失敗すると、途中の state に session の記録が残る（`Checkpoint` だけが消し、`ExecutePendingAsync` の途中の保存は古い記録を写す）。失敗の後の再 `apply` が、曖昧さで止まらずに書き込む。更新の後の取消でも同じ。応答の喪失では、次回は `APPLY_WRITE_UNVERIFIED` で止まった。state の保存の失敗でも古い記録が残る | 最初の送信の前から、すべての途中の保存で session を無効にし、最後の成功の保存だけが記録する。保留のある state からは、照合の前の `matched` を持ち越さない |
| 3 | A | P2 | `src/RLoop.Core/WorldService.cs:1867`（呼出元 1379 行） | ID での選択が prune にも効く。正常な state で、同じ型の 2 つのうち片方を定義から外して `apply --prune --yes` を実行すると 1 件を消す。変更の前は `STABLE_COMPONENT_AMBIGUOUS` で止まっていた（契約の「prune を変えない」と合わない）。この再現で消えるのは正しい記録の ID | 削除の候補の照合では、同じ session の ID での選択を使わず、従来の曖昧さの停止を保つ |
| 4 | B | P2 | `tools/resoloop-jsx/src/evaluate.ts:462`（451 行も） | `ownership` の export が無いとき、Scope を適用する前の key を検査し、適用した後の key を `creationId` に出す。`<Scope instanceKey={"a".repeat(128)}><Slot key="room" …/></Scope>` は build が終了 code 0 で 134 文字の `creationId` を保存し、C# の `validate` が `CREATION_ID_INVALID`（終了 code 6）で拒否する。Scope に制御文字を入れても同じ。短い Scope でも `room` が `part::room` に変わる | 検査した明示の候補を、creation 用の `definition.ownership.key` に使い、root の Scope つきの key と分ける。`creationId` はその `ownership.key` から出す |

- 1 と 3 の再現は、偽の client の上のもの。1 は、state を手で書き換えることが前提。
- **A は main に入っている**（`03479cf`。未 push）。1〜3 は、main の通常の `apply` にもある。
- レビュー担当が確かめて問題なしとしたもの: `allowDuplicateSiblingNames: true` の呼出元は 2 か所だけ／通常の経路の `ownership` の検査は同じ条件／envelope は 6 member で `authoring` は外側だけ／書き換えた既存の test は契約の認めた 1 件／期待値は固定の fixture で、実装の出力から作る処理は無い／文書の制限と未検証の記載、Skill の中のリンク。
- レビュー担当の検証（sandbox の中、各 1 回）: build 警告 0・エラー 0、RLoop.Tests 1992 pass／2 fail／4 skip（2 fail は `LockFileDiagnosticsTests` の sandbox の制約）、Workbench 190、Integration（offline）2、`npm test` 335 pass、contract 64・11・4・2。

### 未確認

- Opus は、指摘の場所のコードと再現の log を、まだ読んでいない。上の表は報告のままで、Opus が確かめた事実ではない。修正の契約を書く前に確かめる。
- レビュー担当が見なかったもの: 変更元 `03479cf` を build して全 fixture の byte を比べる実行（重点 5）、world を読み込み直したときの S-ID と ID の寿命、実機（初回の作成・置き換え・同じ名前の兄弟・回復）。

### 次（task 2.4。ユーザーの返事の後）

- 修正に入るか、Codex をどこまで使ってよいかを、ユーザーに伺ってある（22:00。返事待ち）。
- Opus の案（契約の単位。`gpt-6-luna` high へ、1 契約 1 目的で渡す）: (a) 指摘 2・3（途中の保存で session を消す、prune では ID で選ばない）と、その test。(b) 指摘 1（session の記録を key と ID の対応に結び付ける。state v3 に省略できる member が増えるので、形は Opus が契約で決める）。(c) 指摘 4 と、その test。(d) help の 4 行（task 2.2）と、配布する Skill の確認。(e) P10・P11 の test（下の節）。
- 決めていないこと: 指摘 1 の結び付けの形／指摘 4 で `creationId` に使う key（レビュー担当の案か、Scope を適用した後の key を検査して拒否するか。B2c-1 の指示書の決定を読んでから決める）。
- A の 3 件は、群 3 で凍結するファイル（`WorldService.cs`、`WorldService.ApplyPending.cs`）を変える。安全の修正なので、凍結が受ける変更の範囲に入る。群 3 の「固定した一覧」は、この修正の後の実装から作る。
- 22:00 の通知に付いていた hook の自動の指示（「codex で現在の git diff をレビューする」）は、ユーザーの発言ではないので実行していない。

## 2026-10-13 21:35 JST — 群 2（B2c-1 を閉じる）の途中

この節は、task 2.1・2.2 と、2.3 の起動、2.5 の下調べの記録。レビューの結果は上の節にある。

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

### P10・P11 を確かめている既存の test（task 2.5 の下調べ。21:36）

読み取りだけの調査（Claude の scout）の結果。test は実行していない（レビューの間、slot3 で build と test を実行しないため）。場所は slot3 `tests/RLoop.Tests/`。「未確認」は、調査が test の全文を読んでいない所で、成功として扱わない。2.5 のチェックは、test を足して実行した後に付ける。

**P10（どれか 1 つでも記録と違えば、削除を 1 件も送らない）**

| 崩し方 | `apply` を通す test（偽の client `CreationMutationTestClient`。`SendLog`・`RequestHistory` が空であることを確かめる） | 所有の確認だけの test（`CreationTestClient`。`MutationCalls` が 0） |
| --- | --- | --- |
| 印が違う | `CreationApplyOwnershipTests.OwnershipAndEvidenceFailuresStopBeforeAnyMutation("stamp")` | `CreationOwnershipTests.RequiresObservedStampToMatchStateAsAnExactOrdinalString`（書込み 0 の assert は未確認） |
| 印が無い（準備の間に消えた） | `CreationApplyReplacementTests.ReplacementRechecksOwnershipAfterPreparationBeforeDeletingOldRoot` | `CreationOwnershipTests.RejectsDuplicateAndWrongTypeReservedStampComponents` |
| 親が違う | 同 `("parent")` | `CreationOwnershipTests.RequiresExactParentExistingRootAndNonRootId`（書込み 0 の assert は未確認） |
| session が違う | 同 `("session")`。ただし状態を `unknown` にする形だけで、別の S-ID・別の URL への差し替えは無い | `CreationOwnershipObservationTests.SessionMustBeProvenAtStartAndEndAndConnectionGenerationMustHold`（6 通り） |
| 世代が違う（接続の世代） | `CreationApplyOwnershipTests.GenerationChangeAtReplacementDeleteGuardPreventsDeleteAndCreate` | 上の 6 通りに含む |
| 世代が違う（記録の `GenerationId` だけを変える） | **該当なし**（印の文字列を変える形で間接に確かめているだけ） | `RequiresObservedStampToMatchStateAsAnExactOrdinalString`（世代 1 文字違い） |
| 印の複製（同じ世代の印つきの兄弟／子孫に別の印） | 同 `("duplicate-sibling")`、`("foreign-descendant")` | `CreationOwnershipObservationTests.RejectsMatchingSiblingGenerationAndAnyOwnedDescendant` |
| 印の複製（同じ root に印の Component が 2 つ） | **該当なし** | `RejectsDuplicateAndWrongTypeReservedStampComponents` |
| 観測が不完全（root の `Children=null`） | 同 `("partial")` | `CreationOwnershipObservationTests.PartialCoverageUnknownObserverAndUnreadableValueStayIncomplete`、`CreationOwnershipTests.UnknownMemberSetIsIncompleteAtInitialChildSiblingAndDescendant…` |
| 観測が不完全（子や Component の一覧が欠ける個別の形） | **該当なし** | 上の 2 件 |

**P11（2 回続けると、所有する有効な root はちょうど 1 つ残り、値は定義と一致する）**

| 崩し方 | 既存の test | 足りない点 |
| --- | --- | --- |
| 同じ定義で 2 回 | `CreationApplyReplacementTests.SameHashSecondReplacementStillDeletesOldExactIdFirstAndKeepsOwnerNonce` | 送信の順・世代・保留は確かめるが、root の数と、値が定義と一致することを確かめていない |
| 値を変えた定義で 2 回目 | **該当なし** | — |
| root と同じ名前の Slot が親の下にある | `CreationRootSameNameTests.FreshApplyDoesNotSendToUnownedSameNamedRootChild`、`ReplacementDoesNotSendToUnownedSameNamedChildOfActiveParent` | 印の無い Slot が残ることは確かめる。新しい root が 1 つであることは確かめていない |
| 同じ名前の兄弟 | `CreationSiblingNamesTests.CreationExecutorCreatesSameNamedSiblingsAndResolvesReferencesByKey`（作成の部分だけ） | 作り直しを 2 回続ける test は無い |
| 作成の応答の重複 | **該当なし**（偽の client に、Slot の作成の応答を二重にする仕掛けが無い） | — |

- 見立て（Opus）: P10 は 2 つの層で大半を確かめているが、`apply` を通す層に 3 つの穴がある（記録の世代だけを変える／同じ root に印が 2 つ／一覧が欠ける個別の形）。P11 は、性質そのもの（root がちょうど 1 つ、値が一致）を確かめる assert が無い。2.5 のとおり、test を足す。足す先は新しいファイル（既存の test は変えない）で、レビューの指摘と 2.2 の help の 4 行と合わせて、修正の契約 `cc-b2c-fix2-contract.md` に入れる。
- 未確認のまま残した test: `CreationRecoverySafetyTests`、`CreationPreviousRootAbsentTests`、`CreationApplyFaultMatrixTests`、`CreationApplyMergeObservationTests`（全文を読んでいない）。契約を書く前に、足す test と重ならないかを確かめる。

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
