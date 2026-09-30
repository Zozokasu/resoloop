# スリム化 S0〜S1 引き継ぎ（2026-10-01）

計画の正本は`plan/resoloop-slimming-plan.md`（git管理外）、受け入れケースは`plan/verification-and-handoff.md`にある。
作業状態の正本は`.ccg/tasks/resoloop-slimming/`（`task.json`、`assignments.md`、`plan.md`）にある。
この文書は、その時点の要約と次の作業をまとめたものである。進捗表をここに複製しない。

## 1. 現在地

- main: S1-C1（`9d575a8`）とS1-C2（`cb7ef3e`）を2026-10-01にmergeした（未push。origin/main `7d73071`より先行）。
  - 単独のbuild/test: S1-C1 `9fccb27`はbuild 0/0、RLoop.Tests 452。S1-C2 `12dee5f`はbuild 0/0、RLoop.Tests 449。
  - 2本を合わせた状態のbuild/testは、この文書のcommit時点で実行中または未実行。結果は`.ccg/tasks/resoloop-slimming/assignments.md`のS1-MERGE行を見る。
- worktree: slot1〜3はdetached HEADで空いている。
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

## 3. 次のセッションでやること（順番どおり）

1. **合わせた状態のbuild/testを確かめる**: S1-MERGE行に結果がなければ、main HEADで`dotnet build ResoLoop.slnx`と`dotnet test ResoLoop.slnx --no-build`を実行する。RLoop.Testsの期待値は、452と449から共通の基線439を引いて合算した約462件。失敗があれば、S1-C1とS1-C2の相互作用を調べる。
2. **文書の同期（S1-C1Fが残したもの）**:
   - `README-DETAILS.md`の349行付近: 「component index」で再解決するという記述を消す。
   - `skills/codex/resonite-debug/SKILL.md`の24行付近: `STABLE_COMPONENT_AMBIGUOUS`の説明から「index」を消す。
   - `APPLY_STORED_ID_UNVERIFIED`の説明と対処（記録された名前と親に戻す、またはstateを明示的に直す）をREADME-DETAILSとdebug Skillに加える。
   - 同型のComponentを複数置くときに、記録済みの集合が完全である必要があるという規則を、必要に応じてbuild Skillに書く。
3. **担当体制の変更を文書へ反映する（ユーザー指示 2026-10-01）**:
   - Opus直属のマネージャを、Sonnetのsubagentからcodexの`gpt-6.1-sol`（high effort）に変える。
   - 2026-10-01にcodex-cli 0.159.2で確認した。`gpt-6.1-sol`は実行できる。`--effort`という引数は存在しないため、`-c model_reasoning_effort=high`で指定する（ログに`reasoning effort: high`と出ることを確認済み）。
   - 起動の形: `codex exec --model gpt-6.1-sol -c model_reasoning_effort=high --sandbox workspace-write -C <worktree slot> -o <報告ファイル> - < charter.md`
   - `docs/dev/agent-workflow.md`、`.claude/agents/feature-manager.md`、`scripts/agents/ccg-go-roles.mjs`、`CLAUDE.md`を、この体制に同期する。
   - charterはファイルに書き、標準入力で渡す（Windowsでは、改行を含む引数は1行目しか届かない）。
4. **Codexレビューの頻度を文書へ反映する（ユーザー指示 2026-10-01）**: 実装や修正のたびではなく、フェーズがおおむね終わった時点で、そのフェーズの全差分に重めのレビューを1回行う。agent-workflow.mdに反映する。
5. **S1出口の重めのレビュー**: 対象は`a4c0d64..<merge後のmain>`のS1全差分。重点は、state・所有・削除、公開JSON・エラーコードの互換性、並行性。
6. **S1の残り**:
   - V06（Lampと小さなUIのlive取得）は、S2で作るLamp fixtureと一緒に行う。repo内にはLampのexampleやrecipeが存在しない。
   - V02〜V05のlive証拠のうち未取得のもの（下記「未検証」）。
7. **S2着手**: S2-1（ownershipとproject基準の分離）から始める。`ApplyStateStore.ResolvePath`とTSXの`ownership: { key: converted.key }`の結合を外す。stateのオプション名は、scoutが`--state-file`と報告しており、計画は`--state`としている。どちらが正しいか、実名を確認すること。

## 4. 判断の記録（ユーザー決定）

- 一担当の標準形は、Sonnetが調査から報告まで一貫して行う形（S0-2）。2026-10-01にCodexへ変更する指示があり、上の3で保留中。
- S1の契約として次を決めた。
  - 新しいエラーコードは上記のとおり。
  - IResoniteClientとstate schemaは変えない。
  - 型キャッシュの内容識別、aliasの正規化、`SESSION_MISMATCH`、Deployerのsession確認は保留する（S4）。
- 連番は同一性の根拠にしない。判定できないときは変更前に停止する。
- 軽微な修正にはCodexの再確認をしない。Codexレビューはフェーズ単位で行う。
- mainへのmergeは毎回ユーザーの承認を得る。pushはしない。

## 5. 未検証・後回し

- live未確認の項目:
  - SDKの`Wait`が打ち切った後に届く応答の扱い。
  - `GetAllComponentTypes("*")`の呼び出し自体に副作用があるか。
  - ConnectionGenerationの実機での値（CLIには出力されない）。
  - 短い型名で検索したときのget-allの回数。
  - 深い階層やUserRootで、実機が親IDやreference-onlyをどう返すか。
- 同じ型のComponentを作るとき、保存IDが別のSlotに残っている場合は、止まらずに新しく作る（S1-C1Fの報告）。停止させるかは未決。
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
- `codex-s1-*.md`: レビュー原文
- `fix-log.jsonl`
- 各段のログ: `s1-*-log.md`
- `s1-u5-log.md`と`u5/`: live記録
