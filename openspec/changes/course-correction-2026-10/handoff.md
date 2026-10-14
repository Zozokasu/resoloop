# Handoff

群を 1 つ終えるたびに、日付の見出しの節を先頭へ足す。進捗は `tasks.md` のチェックだけに書き、ここには写さない。全体の順序は `openspec/roadmap.md`、未検証の一覧は `openspec/unverified.md` にある。

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
