# スリム化 S0〜S1 引き継ぎ（2026-10-01、S1出口の修正後に更新）

計画の正本は`plan/resoloop-slimming-plan.md`（git管理外）、受け入れケースは`plan/verification-and-handoff.md`にある。
作業状態の正本は`.ccg/tasks/resoloop-slimming/`（`task.json`、`assignments.md`、`plan.md`）にある。
この文書は、その時点の要約と次の作業をまとめたものである。進捗表をここに複製しない。

## 1. 現在地

- main: S1出口の修正を`69a386c`で2026-10-01にmergeした（未push。origin/main `7d73071`より先行）。
  - 中身: 文書同期（S1-DOCS）、S1出口レビューの指摘C01〜C04・W01〜W05の修正（S1-FIX-A/B/B2）。
  - 統合したbuild/test（`7254cc5`、Opus実行）: build 0/0、RLoop.Tests 508/508、Workbench.Tests 188/188、IntegrationTests 19（実機では未実行）。508は468に、FIX-Aの+15とFIX-Bの+25を足した数と一致する。
- worktree: slot1〜3はdetached HEADで空いている。`agent/*`ブランチは残っていない。
- `feature/hierarchy-observation`（`a4a3f55`）はS1-Aでmainへ統合済み。ユーザーのブランチなので削除していない。

## 2. 完了したこと

### S0（main `0d4bd71`）
- 開発体制・hook・文書を新方針（直結主経路、Workbench凍結、一担当）へ同期した。対象は`docs/dev/agent-workflow.md`、`scripts/agents/ccg-go-roles.mjs`、`.claude/agents/feature-manager.md`、`CLAUDE.md`、`AGENTS.md`、`docs/dev/fork-strategy.md`、`README-DETAILS.md`の1行。
- 基線と手動同期の測定値は`.ccg/tasks/resoloop-slimming/`の`baseline-log.md`・`s0-3-baseline.md`にある。S5-5/V25の比較元に使う。
  - Apply型へfieldを1つ足すと、手で直す箇所が9〜11ある。
  - TSXにはsource mapがない。
  - 担当交代時に読む文書は最低322行。

### S1（main `7db811e`〜`9679845`）
- S1-A: `a4a3f55`を統合した（hierarchy profile/query、snapshot、snapshot diff）。Workbench backendでは、これらをUnsupportedとして登録した。
- S1-I1/I2と、それに対するCodex R1〜R3の修正:
  - `COMPONENT_DEFINITION_UNREADABLE`、`TYPE_DEFINITION_UNREADABLE`、`COMPONENT_TYPE_AMBIGUOUS`、`TYPE_SEARCH_INCOMPLETE`を追加した。これで「読めない・判定できない」と「NotFound」を区別する。
  - ReflectionQueryの結果に`Status`（verified/unknown/notFound）を付けた。失敗は型単位に閉じ込める。
  - `type describe`で定義を読めないときは`membersAvailable:false`を返す。
  - 型カテゴリの走査に上限を設けた（カテゴリ5000、深さ32）。欠落した応答は失敗として扱う。
  - `SessionInfo.ConnectionGeneration`を追加した。`ALREADY_CONNECTED`、`CONNECTION_GENERATION_CHANGED`も追加し、世代の確認とキャッシュ更新を同じlockの中で行う。
  - Disposeが失敗しても、元のエラーを隠さない。
- U5の読み取り専用live（2026-10-01、書き込みなし）: `ws://localhost:47610/`で、ユーザーの自宅world 1つを対象にした。Resonite 2026.9.18.82、ResoniteLink 0.13.1.0。
  - Lightの型検索は11件だった。
  - GradientStripTextureは`membersAvailable:false` / `COMPONENT_DEFINITION_UNREADABLE`になった。
  - **UniqueSessionId（CLIのconnectionId）は、接続ごとの連番（0→1→2）だった。** 記録は`s1-u5-log.md`と`u5/`。

### S1-C1/C2（main `9d575a8`・`cb7ef3e`）
- S1-C1/C1F（WorldService）:
  - 連番を同一性の根拠にしない。
  - 保存IDは、所有Slotを先に解決したうえで、所有の証拠を確かめたときだけ再利用する。
  - 同じ型・同じ名前の候補が複数あれば、曖昧として停止する。componentIndexは使わない。
  - 保存IDの実体は残っているのに検証に失敗した場合は、`APPLY_STORED_ID_UNVERIFIED`で変更前に停止する（手動改名とID衝突は区別しない）。
  - IDも旧パスも存在しないと確かめられたときだけ、作り直す。
  - 祖先の照合は64段まで。循環・深さ超過・親IDの欠落は、理由を付けて検証失敗にする。
- S1-C2/C2F（ObservationService）:
  - `--exclude-user-roots`を追加した（明示的に指定したときだけ除外し、結果は`complete=false`）。
  - reference-onlyのSlotは、観測できていない境界として扱う。
  - 読めなかったmemberやComponentは`unobserved`に入れる。
  - diffは生のIDとconnectionIdを比べない。
  - cursorはv3にした（offsetより前の行のID・path・投影値で照合する）。
  - 旧形式のsnapshotは未観測として読む。
  - 互換性が変わる点:
    - 廃止: `SNAPSHOT_CONNECTION_CHANGED`、`component.replaced`、`CURSOR_CONNECTION_MISMATCH`
    - v1/v2のcursorは`CURSOR_INVALID`になる。
    - 追加: `SNAPSHOT_IDENTITY_UNPROVEN`（常に出す）、`SNAPSHOT_LEGACY_OBSERVATION_FLAGS`

### S1出口（main `69a386c`）
- S1-DOCS: README-DETAILSとdebug・build Skillを同期した。開発者向け文書（`docs/dev/agent-workflow.md`、`.claude/agents/feature-manager.md`、`scripts/agents/ccg-go-roles.mjs`、`CLAUDE.md`）は、Codex一担当とフェーズ単位のレビューに合わせた。
- S1出口の重めのレビュー（Codex、`0d4bd71..1abb0c3`）: Critical 4件、Warning 5件。原文は`codex-s1-exit-review.md`。
- S1-FIX-A（WorldService）:
  - C01: 移動先にある同名の無関係なSlotは採用せず、`APPLY_TARGET_AMBIGUOUS`で停止する。
  - C02/C03: 保存ID集合が一致しても、それを同一性の根拠にしない。参照構造で絞った候補だけを使う。
  - C04: 保存Slot IDの読み取りが`SLOT_NOT_FOUND`以外で失敗したら、`APPLY_STORED_ID_UNVERIFIED`（reason `storedIdReadFailed`）で停止する。
  - W01: 所有Slot上で保存Componentが生きているのに、証拠が不一致または読めない場合は停止する。
- S1-FIX-B:
  - W02: 旧形式snapshotの観測flagを正規化し、reference-onlyの親では不在を推論しない。
  - W03/W04: sessionとキャッシュを読むとき、世代の確認を先に、同じlockの中で行う。
  - W05: 非Component型の定義が読めないときは、NotFoundではなく`TYPE_DEFINITION_UNREADABLE`にする。
- C04のlive（読み取りのみ、ユーザー承認、ws://localhost:47610/「ぞかホーム」、Resonite 2026.9.18.82、ResoniteLink 0.13.1.0）:
  - GetSlotの不在応答は`Slot with ID '<id>' not found.`。空IDでは`Slot ID must be provided.`が返る。記録は`c04-live/raw-getslot.txt`。
  - S1-FIX-B2: この文言と完全に一致したときだけ`SLOT_NOT_FOUND`にし、それ以外は`RESONITE_OPERATION_FAILED`にする。
- 意図して変えた挙動:
  - `identityFields`も管理参照もない同型Componentを同じSlotに複数置いた場合、再applyは`STABLE_COMPONENT_AMBIGUOUS`で停止する。
  - driver交換が中断した後のresumeは、`APPLY_COMPONENT_OWNERSHIP_CONFLICT`で停止する。
  - 所有Slot上の保存IDが別の型を指していたら、`APPLY_STORED_ID_UNVERIFIED`で停止する。
  - inspectなどで不在以外の読み取りに失敗したときの終了コードが5から7に変わった。
- Codexは、worktreeでcommitできない（sandboxが本体の`.git`へ書けない。`--add-dir`を付けても不可）。担当は未commitのまま報告し、Opusが差分を確認してから代わりにcommitする。

## 3. 次のセッションでやること（順番どおり）

1. **S1の残り**:
   - V02〜V05のlive証拠のうち、まだ取れていないもの（§5「未検証」）。読み取りのみで、セッションとportを事前に報告する。
   - V06（Lampと小さなUIのlive取得）は、S2で作るLamp fixtureと一緒に行う。repo内にLampのexampleやrecipeは存在しない。
2. **S2着手**: S2-1（ownershipとproject基準の分離）から始める。下調べは`.ccg/tasks/resoloop-slimming/s2-1-prep.md`にある。
   - stateのオプション名は`--state`である（`--state-file`は存在しない。以前のscout報告は誤り）。
   - 結合している箇所は`ApplyStateStore.ResolvePath`（`src/RLoop.Core/ApplyWorkflow.cs`）。既定のパスは`<projectRoot>/.resoloop/state/<Sanitize(ownership.key)>.json`で、ここにTSXの`ownership: { key: converted.key }`（`tools/resoloop-jsx/src/evaluate.ts`）が結合している。
3. S2が終わったら、S2の全差分に重めのCodexレビューを1回行う。

## 4. 判断の記録（ユーザー決定）

- 一担当はCodex `gpt-6.1-sol`（high）とし、2026-10-01に文書へ反映した。Sonnetは予備とする。
- S1の契約として次を決めた。
  - 新しいエラーコードは上記のとおり。
  - IResoniteClientとstate schemaは変えない。
  - 型キャッシュの内容識別、aliasの正規化、`SESSION_MISMATCH`、Deployerのsession確認は保留する（S4）。
- 連番は同一性の根拠にしない。判定できないときは変更前に停止する。
- 軽微な修正にはCodexの再確認をしない。Codexレビューはフェーズ単位で行う。
- mainへのmergeは毎回ユーザーの承認を得る。pushはしない。
- S1出口: 修正ラウンドを実施し、S1-DOCSは修正と一緒にmergeした。C04は読み取り専用のliveで不在の文言を確かめてから実装した（2026-10-01ユーザー承認）。

## 5. 未検証・後回し

- live未確認の項目:
  - SDKの`Wait`が打ち切った後に届く応答の扱い。
  - `GetAllComponentTypes("*")`の呼び出し自体に副作用があるか。
  - ConnectionGenerationの実機での値（CLIには出力されない）。
  - 短い型名で検索したときのget-allの回数。
  - 深い階層やUserRootで、実機が親IDやreference-onlyをどう返すか。
- 同じ型のComponentを作るとき、保存IDが別のSlotに残っている場合は、止まらずに新しく作る（S1-C1Fの報告）。停止させるかは未決。（所有Slot上で生きている場合は、W01で停止するようになった。）
- GetComponentの失敗は、すべて`COMPONENT_NOT_FOUND`のまま分類している。Componentが不在のときの文言はliveで未確認。現状では、これを不在の証明として再作成や削除に使う箇所はない（S1-FIX-B2の調査）。
- GetSlotの不在の文言は、Resonite 2026.9.18.82とResoniteLink 0.13.1.0でだけ確かめた。他のバージョンで文言が変わると、不在が`RESONITE_OPERATION_FAILED`になり、applyは停止する（安全側に倒れる）。
- S1出口の修正（C01〜C04、W01〜W05）は、liveで未検証（C04の文言の取得を除く）。修正後のCodex再レビューはしていない（方針どおり）。
- 新しいworldで、古いstateのIDが無関係なオブジェクトと衝突した場合は、設計どおり停止する。stateをリセットするコマンドはない。
- 観測のdiffで、snapshotの外を指す参照がある場合は、常に`complete=false`になる（保守的な挙動）。
- request数が増えた。stable Componentの解決は1回から3回以上に、observeの試験は3回から6回に増えた。applyでは、stateにあるSlotを作る前に存在確認を1回行う。
- IntegrationTestsの19件は、環境変数が未設定のため実機では実行されていない。passedと数えられているが、live検証の合格ではない。
- `settings.local.json`に残っているDevin・Workbench・旧repo向けの許可は、S5で扱いを見直す。

## 6. 主な記録の場所

`.ccg/tasks/resoloop-slimming/`:
- `plan.md`: charterとユーザー決定
- `assignments.md`: 追跡表
- `s1-b-gap.md`: 接続と型情報の不足分析
- `codex-s1-*.md`: レビュー原文（S1出口は`codex-s1-exit-review.md`）
- `s1-fix-*-charter.md` / `s1-fix-*-report.md`: S1出口の修正
- `c04-live/`: GetSlotの不在応答のlive記録
- `s2-1-prep.md`: S2-1の下調べ
- `fix-log.jsonl`
- 各段のログ: `s1-*-log.md`
- `s1-u5-log.md`と`u5/`: live記録
