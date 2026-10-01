# スリム化 S2 途中の引き継ぎ（2026-10-01、S2-2完了時点）

計画の正本は`plan/resoloop-slimming-plan.md`（git管理外）、受け入れケースは`plan/verification-and-handoff.md`にある。
作業状態の正本は`.ccg/tasks/resoloop-slimming/`（`task.json`、`assignments.md`、`plan.md`）にある。
S1までの経緯は`docs/dev/slimming-s0-s1-handoff.md`にある。この文書は、それ以降の要約と次の作業をまとめたものである。

## 1. 現在地

- main: `5974b60`（S2-2のmerge）。未push。origin/mainの`7d73071`より先行している。
- worktree: slot1〜3はdetached HEADで空いている。`agent/*`ブランチは残っていない。
- 体制: Opusが議長。一担当はCodex `gpt-6.1-sol`（high）とし、`docs/dev/agent-workflow.md`が正本。
  - 起動: `codex exec --model gpt-6.1-sol -c model_reasoning_effort=high --sandbox workspace-write -C <slot> -o <報告> - < charter.md`（Bashのbackground実行）。
  - 起動前にOpusがsandboxの外で`dotnet restore ResoLoop.slnx`と、`tools/resoloop-jsx`の`npm ci`を行う。charterでは`--no-restore`を指示する。
  - Codexはworktreeでcommitできない。担当には未commitのまま報告させ、Opusが差分を確認してから代わりにcommitする。
  - CodexのsandboxからはResoniteへ接続できない。liveはOpusがユーザー承認を得て、読み取り専用で行う。
- Codexレビューはフェーズ単位で1回行う。S2の出口レビューはまだ行っていない。

## 2. S1の残りとして完了したこと（main `10fef92`まで）

- S1の残りのlive（読み取りのみ、ws://localhost:47610/「ぞかホーム」、Resonite 2026.9.18.82 / ResoniteLink 0.13.1.0）。記録は`.ccg/tasks/resoloop-slimming/s1-live2/summary.md`。
  - V04: enumは読める（member経由と直接の両方）。不在の型のsearchは空になる。
  - V05: 連続して取った2つのsnapshotのdiffは0変更で、偽の削除はない。10000 Slotで打ち切られ、`complete=false`になる。unobservedは5215件で、保守的な挙動。
  - 未測定: 短い型名で検索したときのget-allの回数（describeとsearchでは`--profile`が出ない）。
- S1-FIX-C（`10fef92`）: 型とComponentの不在は、実機の応答文言と完全一致したときだけNotFoundにする。
  - 型: `<type> is not a valid type`
  - Component: `Component with ID '<id>' not found.`
  - 型一覧に載っている型は、どんな応答でもNotFoundにしない（R1-C1の再発防止）。
  - Componentの読み取りで、不在以外の失敗は終了コードが5から7に変わった。

## 3. S2で完了したこと

### S2-1（main `893d951`）: ownershipとproject基準の分離
- 設計メモは`.ccg/tasks/resoloop-slimming/s2-1a-design.md`。ユーザー決定は次のとおり。
  - `authoring.projectRoot`は絶対パスで記録する。
  - `--require-state`を追加し、移行の手順では必須にする。
  - 受け入れは、現在のJSXで表せる範囲に限る。
- TSXでは`export const ownership = { key }`で指定する。未指定ならroot keyを使う。
- buildで、project基準を一度だけ解決する（`--project-root`、最寄りの`.resoloop.json`、TSXのディレクトリの順）。生成JSONには`authoring`として記録する。
- Coreは`authoring`から作ったcontextで、stateのパスを一度だけ計算する。`authoring`が不正なら`APPLY_PROJECT_CONTEXT_INVALID`で停止する。
- `diff`/`plan`/`apply`に`--require-state`を追加した。stateがなければ`APPLY_STATE_NOT_FOUND`で停止し、stateを作らない。
- 手書きJSONと明示した`--state`の挙動は変えていない。
- offlineで受け入れを確かめた。既存JSONからTSXに移しても差分0・writes 0。出力先を移しても、コピーしてもwrites 0。

### S2-2（main `5974b60`）: stable instance keyとlocal key
- TSXでは`<Scope instanceKey="left">…</Scope>`、JSONでは`"$scope"`で、明示的にscopeを作る。
- keyは`instance::local`の形になる（入れ子なら`outer::inner::local`）。各segmentには`:`を含められない。
- scope内での短い参照は、現在のscopeだけで解決する。外側から参照するときは、完全なkey（`ref.key(...)`）を使う。
- 既存のkey、stateの実効key、旧JSONの既定keyは変えない。衝突はエラーにし、自動で再採番しない。
- 新しいエラーコードは`APPLY_SCOPE_INVALID`と`APPLY_DRAFT_KEY_UNSTABLE`。
- **意図して変えた挙動**: TSXのdraftで自動生成したkey（兄弟のindexに依存するもの）は、確認用としてだけ使える。そのままvalidateやapplyをすると、`APPLY_DRAFT_KEY_UNSTABLE`で停止する。既存の制作物は、同じ文字列を明示keyとして書けば差分0で続けられる。
- offlineで受け入れを確かめた。
  - 同じ部品を2個置ける。
  - 無関係な兄弟を足しても、既存のkeyとIDが変わらない。
  - 旧来の実効keyから新しいTSXに移しても、差分0・writes 0で、全IDが保たれる。
- 検証: RLoop.Tests 643、Workbench.Tests 188、npm 51、contract 16。どれも失敗なし。詳細は`.ccg/tasks/resoloop-slimming/s2-2-notes.md`。

## 4. 次にやること（順番どおり）

1. **S2-3**: C#のApply型から、低水準の型形状を生成する。使っているComponentのcatalogから、型とIRの検証を導く。意味の検証は共通IRに集める（計画S2-3）。
   - 受け入れ: fieldを足すときの手での転記がなくなる。TSだけでは表せない条件（floatの範囲、参照型など）を、IRで拒否する。独立した正解fixtureを残す。
   - 比較元: S0-3の基準値（Apply型へfieldを1つ足すと、手で直す箇所が9〜11）。`.ccg/tasks/resoloop-slimming/s0-3-baseline.md`
2. **S2-4**: TSXのbuildが成功したときだけ、同じbuildのIR・map・使用型の識別をCLIへ渡す。
3. **S2-5**: `key + member + JSON path`から、TSX上の位置を導く。共通の診断に載せる。
4. **S2の出口**:
   - Lampを作る → 再適用で0変更 → configを1件変える → 誤ったmemberや参照の診断 → TSXを直す → 再適用。この流れを通す。
   - V06（Lampと小さなUIのlive取得）を一緒に行う。live書き込みは`ResoLoop_Test*`の下だけで行い、事前にユーザーの承認を得る。
   - S2の全差分（`0d4bd71`以降のS1分は除く。S2の開始は`42ef7ea`）に、重めのCodexレビューを1回行う。
5. 規模が大きいもの（S2-3〜S2-5）は、S2-1と同じ二段（設計メモ→ユーザー承認→実装）にするか、ユーザーに確認する。S2-2は、ユーザーの指示で一括にした。

## 5. 未検証・後回し

- S1-FIX-C、S2-1、S2-2の修正後の挙動は、liveで未検証（offlineのみ）。
- 実機の文言による分類（GetSlot、GetComponent、type）は、Resonite 2026.9.18.82とResoniteLink 0.13.1.0でだけ確かめた。他のバージョンで文言が変わると、不在が「不明」になり停止する（安全側に倒れる）。
- `authoring.projectRoot`は絶対パスなので、project全体を移したら再buildが要る。古いCLIは`authoring`入りのJSONを拒否する。
- S0-S1引き継ぎ§5の未検証項目（SDKの`Wait`が打ち切った後に届く応答、ConnectionGenerationの実機での値、深い階層とUserRootの扱いなど）は、まだ残っている。
- IntegrationTestsの19件は、opt-inが無効なので実機では実行されていない。

## 6. 主な記録

`.ccg/tasks/resoloop-slimming/`:
- `assignments.md`（追跡表）
- `plan.md`（charterとユーザー決定）
- `s1-live2/`
- `s1-fix-*`
- `s2-1a-design.md`
- `s2-1b-charter.md`
- `s2-2-charter.md`
- `s2-2-notes.md`
