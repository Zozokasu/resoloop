# Unverified

成功として扱えない未検証の項目の正本。最初の内容は `openspec/changes/course-correction-2026-10/design.md` の「未検証の一覧」を写したもの（2026-10-13 の時点）。offline の検証だけで機能を受け入れたときは、実機で確かめていない点を 1 行ずつ足す。

## 状態の値

- 未検証: まだ確かめていない。成功として扱わない。
- 確認済み: 確かめた。証拠の場所（報告・log と、対象の session・Slot）を、その行に書く。
- 免除: 確かめずに閉じることを、ユーザーが認めた。ユーザーの言葉と日付、代わりにした offline の検証を、その行に書く。
- 保留: この change では扱わない。扱う change を「確かめ方」に書く。
- 観察中: 再発したときに調べる。

change を archive する前に、その change が生んだ項目は、すべて「確認済み」か「免除」でなければならない。

## 一覧

| ID | 内容 | 生じた所 | 確かめ方 | 要る条件 | 状態 |
| --- | --- | --- | --- | --- | --- |
| U01 | 同じ session の ID での照合 | `711ef87` | 実機: 同じ Slot に同じ型の Component 2 つ → 同じ session で再 apply は成功、`--url ws://…` では `STABLE_COMPONENT_AMBIGUOUS` | 実機の許可 | 確認済み（2026-10-14、L4 の U01、`071`〜`079`）。同じ session の再 `apply` は変更 0 件で Component の ID も同じ、URL の直接指定は `plan`・`apply` とも `STABLE_COMPONENT_AMBIGUOUS`（終了 code 6、`sessionProof` は `unknown`）で書込みなし。 証拠: `resoloop-worktrees/live-1012/evidence/L4`、報告 `.ccg/tasks/resoloop-slimming/cc-live-l4-report.md`。session「ぞかホーム」`S-01a13803-91cf-76b4-b635-eb730117be13`、`ResoLoop_Test_L4` = `Reso_30FA2`（slot3 `ba57564` の build） |
| U02 | TSX から build した定義での初回の作成と置き換え | `061b054` | 実機 | 実機の許可 | 確認済み（2026-10-14、L4 の U02、`032`〜`038`）。作成の後、値を変えて置き換え（`replaced` true）、古い root は `SLOT_NOT_FOUND`。TSX の import 元は `resoloop-jsx/draft`。 証拠: `resoloop-worktrees/live-1012/evidence/L4`、報告 `.ccg/tasks/resoloop-slimming/cc-live-l4-report.md`。session「ぞかホーム」`S-01a13803-91cf-76b4-b635-eb730117be13`、`ResoLoop_Test_L4` = `Reso_30FA2`（slot3 `ba57564` の build） |
| U03 | key を省いた定義の作り直し | `061b054` | 実機 | 実機の許可 | 確認済み（2026-10-14、L4 の U03、`041`〜`046`）。作成と置き換えが成功。 証拠: `resoloop-worktrees/live-1012/evidence/L4`、報告 `.ccg/tasks/resoloop-slimming/cc-live-l4-report.md`。session「ぞかホーム」`S-01a13803-91cf-76b4-b635-eb730117be13`、`ResoLoop_Test_L4` = `Reso_30FA2`（slot3 `ba57564` の build） |
| U04 | 同じ名前の兄弟を含む定義の作り直し | `061b054` | 実機 | 実機の許可 | 確認済み（2026-10-14、L4 の U04、`051`〜`056`）。同じ名前 `Lamp` 3 つ。置き換えの後も 3 つで、2 つ目の値だけが変わった。 証拠: `resoloop-worktrees/live-1012/evidence/L4`、報告 `.ccg/tasks/resoloop-slimming/cc-live-l4-report.md`。session「ぞかホーム」`S-01a13803-91cf-76b4-b635-eb730117be13`、`ResoLoop_Test_L4` = `Reso_30FA2`（slot3 `ba57564` の build） |
| U05 | root と同じ名前の Slot が親の下にある場合 | `061b054` | 実機 | 実機の許可 | 確認済み（2026-10-14、L4 の U05、`061`〜`068`）。先に置いた Slot `Reso_3194B` と子 `Reso_31956` は、作成と置き換えの後も同じ ID で残った。 証拠: `resoloop-worktrees/live-1012/evidence/L4`、報告 `.ccg/tasks/resoloop-slimming/cc-live-l4-report.md`。session「ぞかホーム」`S-01a13803-91cf-76b4-b635-eb730117be13`、`ResoLoop_Test_L4` = `Reso_30FA2`（slot3 `ba57564` の build） |
| U06 | 回復の `finish` の成功 | `d69a7a5` | 実機では、木が完成した後で止まる状況を作れない。offline の test を根拠にする | ユーザーの免除 | 免除（2026-10-14 17:07 ごろ、ユーザーの返事「Exempt both」。task 7.5）。代わりの offline の test: `CreationRecoveryFinishTests.FinishAcceptsStagedExactCandidateOnlyWhenTreeEvidenceAndCountsMatch`、`CreationRecoveryFinishTests.IncompleteStageOrCountEvidenceRetainsPendingWithoutWorldWrites`、`CreationEngineAddedComponentsTests.FinishAcceptsExtraComponentsAndReportsTheCount` |
| U07 | 所有の印つきの root を複製した場合 | `d69a7a5` | 実機: root を複製してから作り直し、どちらも消されずに止まること | 実機の許可、ユーザーによる複製の操作 | 未検証 |
| U08 | 無効な Slot の下の ProtoFlux | `live-1013-l2b-report.md` | 実機: 無効な親の下へ Flux を配置し、有効にした後に動くこと | 実機の許可、`RESONITE_MANAGED_DATA_PATH` の設定 | 未検証 |
| U09 | root を別の親へ移す形 | 同上 | CLI に手段が無い。R15 の change で扱うかを決める | — | 保留 |
| U10 | world の再読込み・Resonite の再起動の後の ID と結びつき（P8） | `spike-dv-p2-report.md` | 実機: 作成 → world の保存 → 再起動 → 作り直し | 実機の許可、ユーザーによる保存と再起動 | 未検証 |
| U13 | 不安定 test `S3RealProcessKillReleasesHandleButPreviousPendingStillBlocks` | 2026-10-05、10-08、10-14 | 再発したら、失敗時の記録（`LockFileDiagnostics`）を読む。自動の retry で隠さない | 再発 | 観察中 2026-10-14 11:01 に再発（main `b88ed8a`、全体の実行の 2 回目。Resonite は起動中）。記録: holder は 02:01:49.666Z に終了、同じ時刻に取得を試みて `IOException` 0x80070020、`holders: none`、`busyForMs: 0`。log は `.ccg/tasks/resoloop-slimming/cc-main-merge-RLoop.Tests-run2.log`。2026-10-14 12:12 ごろにも再発（slot3 `agent/course-correction`、test と固定したファイルだけを変えた作業ツリー、全体の実行の 1 回目。2 回目は失敗 0）。`SessionWriteLock.cs:41` で同じ `IOException`。log は slot3 `TestResults/CC-G3/opus/accept2-full.log`。2026-10-14 14:23 ごろにも再発（slot3 `5ff0c18` の手前、jsx・文書・test 1 件だけを変えた作業ツリー、全体の実行の 1 回目。3 回目は失敗 0）。log は slot3 `TestResults/CC-G5/opus/accept2-tests.log` |
| U14 | S3: 応答だけが失われた場合 | `94c12ed` | 実機では作れない。offline の偽 client の test を根拠にする | ユーザーの免除 | 免除（2026-10-14 17:07 ごろ、ユーザーの返事「Exempt both」。task 7.5）。代わりの offline の test: `ApplyWorkflowTests.S3LostCreateResponseNeverRecreates`（slot・component）、`ApplyWorkflowTests.S3LostDeletionResponseRemainsPendingWithoutSecondDelete`、`ApplyWorkflowTests.S3FailureAfterMutationStopsNextSendAndCannotReplay`、作り直しの側の `CreationApplyFaultMatrixTests.OldDeleteUnknownAfterExactDeletionIsNeverConfirmedAndStopsReplacement`・`NewRootUnknownReceiptRetainsCandidateContextAndNoLaterFrames`・`CancelAndTimeoutRetainPendingEvidence` |
| U15 | S3: 実行中の取消 | `94c12ed` | 実機: 大きい定義の apply の途中で CLI を止め、次の apply が保留で止まること | 実機の許可 | 未検証 |
| U16 | S3: 切断 | `94c12ed` | 実機: apply の途中で Resonite 側の接続を切り、次の apply が保留で止まること | 実機の許可、ユーザーの操作 | 未検証 |
| U17a | S3: W04 の回復（失敗した driver の入れ替えからの回復、`apply-recovery.md` の 7 手順） | `s3-exit-handoff.md` §5 | offline で足りる（本物の失敗の状態を実機で安全に作れない）。専用の test の名前は未確認で、近いのは `ApplyWorkflowTests.S3DriverReplacementSucceedsWhenOldDriverIsPrunedFirst`。免除するなら test を特定して伺う | ユーザーの免除 | 未検証 |
| U17b | S3: drift（plan から送信までの間の world の変化） | `s3-exit-handoff.md` §5 | 実機で作れる（plan と apply の間に、ユーザーが対象を変える操作が要る）。offline は `ApplyWorkflowTests.S3PlanDriftStopsBeforeSend` | 実機の許可、ユーザーの操作 | 未検証 |
| U17c | S3: alias（`ws://127.0.0.1:<port>/` と `localhost` の同一視） | `s3-exit-handoff.md` §5 | offline で足りる（実機の server は `127.0.0.1` を HTTP 400 で拒むので届かない）。offline は `ApplySessionObservationTests`（`127.0.0.1` と `localhost`）と `SessionLockIsolationTests` | ユーザーの免除 | 未検証 |
| U17d | S3: `Children=null`（子の一覧が欠けた応答） | `s3-exit-handoff.md` §5 | offline で足りる（実機で欠けた応答を起こせない。prune の成功から間接に支持されるだけ）。offline は `CreationApplyOwnershipTests`（`partial` → `CREATION_OBSERVATION_INCOMPLETE`）と `CreationApplyOwnershipGapTests.MissingListsSendNothing` | ユーザーの免除 | 未検証 |
| U18 | skip 4 件（Flux のライブラリの場所が未設定など） | — | `RESOLOOP_TEST_FLUX_LIBRARY_PATH` を設定して実行 | 環境の設定 | 未検証 |
| U20 | 単位 21（正式な例）と、Toggle を含む実機の確認 | `roadmap-5-exit-handoff.md` | R13 の change で扱う | — | 保留 |
| U21 | 値つきの DynamicImpulse の受信、ボタンを実際に押す操作、複数の人がいる session | `spike-dv-p2-report.md` | R13・R16 の change で扱う | — | 保留 |
| U22 | 採用の観測が null の偽 client での `NullReferenceException`、`recover` の取消の分類の test が無い、初回に何も送る前に止まった場合の `rollback` | `better-handoff-2026-10-13.md` §7 | 小さい修正。R14 の change で扱う | — | 保留 |
| U23 | 不安定 test `FluxDeployEntryTests.WatchHoldsTheLockOnlyDuringADeploymentAndStopsWhenTheNextOneFindsItBusy` | 2026-10-14 10:57（main `b88ed8a`、全体の実行の 1 回目。Resonite は起動中） | `RLoopException` を期待する所で `IOException`（`main.pg` が使用中。`FluxManifestOrchestrator.Fingerprint` の `File.ReadAllBytes`、`FluxManifestOrchestrator.cs:624`）。その class だけの実行は 3 回とも成功 32。再発したら log を残す。自動の retry で隠さない。log は `.ccg/tasks/resoloop-slimming/cc-main-merge-RLoop.Tests.log` | 再発 | 観察中 |
| U24 | 不安定 test `CaptureTests.RejectsWrongFormatAndSize(output: "out.jpg", width: 1, code: "CAPTURE_FORMAT_MISMATCH")` | 2026-10-14 14:27 ごろ（slot3、群 5 の受け入れ、全体の実行の 2 回目） | 期待の `CAPTURE_FORMAT_MISMATCH` ではなく `CAPTURE_EXPORT_TIMEOUT`（`CaptureTests.cs:118`）。src は `a28b708` から変わっていない。`CaptureTests` だけの実行は 3 回とも成功 40、全体の 3 回目は失敗 0。時間に依存する待ちを疑う（原因は調べていない）。再発したら log を残す。自動の retry で隠さない。log は slot3 `TestResults/CC-G5/opus/accept2-tests-run2.log` | 再発 | 観察中 |

`b6c3193` 以降の新しい clone での確認と、JSX の contract の検査の再実行は、`061b054` と `03479cf` で済んでいる（`slot3 TestResults/BETTER-B2C/opus/`）ので、一覧に入れない。
