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
