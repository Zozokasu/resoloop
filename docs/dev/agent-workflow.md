# エージェント体制と実行プロトコル

`/ccg:go`を含む通常の開発で使う役割分担。
`/ccg:go`実行時は、プロジェクトhook（`scripts/agents/ccg-go-roles.mjs`）がこの文書の要点を注入する。
ccgの戦略・モデル解決・タスク作成（`.ccg/tasks/<task>/task.json`）はccg-workflowの手順をそのまま使い、この文書は「誰がどの判断と作業をするか」だけを上書きする。

ccgの戦略にある「実行モードの選択」（Agent Teams / Codex / Claude）は、このプロジェクトでは2026-10-01のユーザー決定により「Codex `gpt-6.1-sol`（high reasoning effort）の一担当、Codexレビューはフェーズ完了時」に固定する。Sonnet subagentはCodexが使えないときの予備、Devinはsandbox外でのみ使える任意手段とする。Opusは選択肢を尋ねずにこのモードを宣言する。
それ以外のHARD STOPゲートは、戦略の定義どおりに残す。

アーキテクチャ、Resonite安全規則、上流の扱いは`AGENTS.md`を正とし、この文書では繰り返さない。

## 方針の前提

通常制作の主経路は直結: TSX / 手書きJSON / ProtoGraph → ResoLoop（展開・型検証・参照解決・差分・局所観測・実行・診断）→ RLoop.ResoniteLink（選択した単一セッション）/ RLoop.Flux → IFluxDeployer → F# deployer → Flux-SDK。
Workbench backend（`src/RLoop.Workbench`、`--backend workbench`、`wb`）は凍結して保持し、新機能・書込みadapter・backend同等化へ投資しない。工程は`plan/resoloop-slimming-plan.md`のS1〜S5に従う。

## 役割と決定権

| 役割 | 実体 | 担当 | 決めてよいこと |
| --- | --- | --- | --- |
| プロデューサ・議長 | メインセッション（Opus） | 目標・優先順位・共有契約（Core公開型、`IResoniteClient`、CLI引数、エラーコード、state）・charter・受け入れ・merge判断・担当間の衝突解決・live検証の可否と対象 | 担当の設置・権限付与、契約の確定。ユーザーの目的や許容範囲を変える判断だけはユーザーに聞く |
| 担当（一担当） | Codex `gpt-6.1-sol`（high reasoning effort）。Codexが使えないときだけSonnet `feature-manager` subagent | 調査→実装→関連build/test→`agent/*`でcommit→短い報告をworktreeスロットで一貫して行う | 承認された範囲内の局所的・可逆的な判断 |
| 実装補助（任意） | Devin SWE-2 Max（`--model swe-2-max`） | sandbox外で大量の並列実装や長時間build/testを任せる。Codex sandbox内からは起動できない | 編集範囲内の実装判断 |
| フェーズ完了時のレビュー | Codex（`smart-friend`スキル、またはccgの`codeagent-wrapper --backend codex`） | フェーズ全差分の重めのレビュー、設計資料・引き継ぎ資料 | なし（指摘と資料を返す。修正ラウンドへ進むかはユーザーが決める） |

- Opusはファイル単位の実装をしない。charterは目的・範囲・契約・受け入れ条件だけにする。
- Opus自身の調べものは`scout`サブエージェントに委譲し、`path:line`と要点だけを受け取る。
- 実装や修正のたびにCodexレビューを回さない。実装単位は担当のbuild/testとOpusの差分確認で受け入れる。
- S1、S2などのフェーズがおおむね完了した時点で、そのフェーズの全差分（開始commit..現main）へ重めのCodexレビューを1回行う。重点は契約違反・state/所有・削除・公開JSONとエラーコードの互換性・並行性。レビュー時点の開始commitとmainのcommit hashを明示し、その後の変更は区別する。
- レビュー後に修正ラウンドへ進むかはユーザーが決める。修正後の再レビューは指摘が大きく設計が変わった場合だけ行い、軽微な修正にはCodexの再確認をしない。

## 通信とエスカレーション

- Opus → 担当: Codex CLIへcharterファイルを標準入力で渡す（下記）。charterには、担当の規則の正本である`.claude/agents/feature-manager.md`を読むよう明記する。Codexが使えないときだけ`Agent(subagent_type: "feature-manager")`、それも使えない場合は`general-purpose`に`model: "sonnet"`を付け、同じ規則を読ませる。
- 担当 → Opus: 最終報告、または途中の`ESCALATE`報告。担当は他の担当を直接起動できないので、担当間の調整はOpusが中継する。
- `ESCALATE`の条件: 他機能の契約変更、層の依存方向や責務境界の変更、要件解釈が大きく分かれる、担当同士で合意できない。報告には「論点・選択肢・推奨と理由・影響する担当」を書く。

### Codex担当の起動

codex-cli 0.159.2で確認済みの起動形は次のとおり。OpusがBashのbackground実行で起動する。

```bash
codex exec --model gpt-6.1-sol -c model_reasoning_effort=high --sandbox workspace-write -C "<worktree slot>" -o "<報告ファイル>" - < "<charter.md>" &
```

- worktreeのgit管理領域は本体repoの`.git`にあり、Codex sandboxはそこへ書けない（`--add-dir "<本体 repo>/.git"`を付けても`index.lock`が拒否される。2026-10-01確認）。Codex担当は変更を未commitのまま残し、報告にcommitの分け方と件名を書く。Opusは差分を確認してから、そのslotの`agent/*`ブランチで代わりにcommitする。
- `--effort`という引数は存在しない。reasoning effortは`-c model_reasoning_effort=high`で指定する。
- charterはファイルに書いて標準入力で渡す。Windowsでは改行を含む引数を渡すと1行目しか届かない。
- `--sandbox workspace-write`内はネットワークが遮断される。NuGet restoreやnpm ciはOpusがsandbox外で先に済ませ、charterでは`dotnet build ResoLoop.slnx --no-restore`を指示する。同じ理由でCodex sandbox内からDevinは起動できない。
- charterの項目、ブランチとcommit、ログ置き場、報告形式は`.claude/agents/feature-manager.md`に従う。Opusは`-o`の報告ファイルとslotのcommitを読み、差分とbuild/testの証拠で受け入れる。終了コードや完了宣言だけでは受け入れない。

## 並列作業のルール

- 1ファイルの編集者は同時に1人。共有型（`RLoop.Core`の公開モデル）、`IResoniteClient`、CLIの引数定義、`skills/codex/*`、`README*`には所有者を1人決める。
- 機能間の契約が要るタスクは、契約をOpusが確定してから並列化する。契約を変えたら影響する担当に通知する。
- 作業途中のファイルを前提にして、別の担当が完了と判断しない。
- 1つの作業ツリーでbuildするのは1人まで。本体の作業ツリーを誰に使わせるかはOpusがcharterで指定する。それ以外の並列作業は、`scripts/agents/new-worktree.ps1 -Name <task> -Slot <1-3>`で固定のworktreeスロット（`..\resoloop-worktrees\slotN`）を使い、担当が`agent/*`ブランチでcommitする。
- commitは`agent/*`ブランチまで。mainへのmergeはユーザー承認後に`--no-ff`で行う。push・リリースはしない。
- 並列数の目安: 同時に最大3。ビルドは1つあたりCPUを数コア使うので、4以上に増やすときは実測してから。

## 担当の文脈を小さく保つ

- 担当はcharter 1件で終える。修正roundや担当交代では、追跡表・差分・既存証拠・未決事項だけを引き継ぐ（新しい担当に渡してよいが必須ではない）。
- charterは、1担当が調査から検証まで通せる大きさにする。越える場合は、Opusが担当を分ける。
- ビルド・テストの全文ログは`.claude/agents/feature-manager.md`の規則に従い`.ccg/tasks/<task>/`（charterで別の場所が指定された場合はその場所）に保存し、Opusへは10行程度の要約（件数、失敗名と一行理由、skip数、所要時間）だけを返す。Opusは要約とcommitの差分を確認する。

## 追跡

タスクごとに`.ccg/tasks/<task>/assignments.md`の1表で管理する（ccgのタスクディレクトリを使い、新しい管理資料は作らない）。

| ID | 担当 | 編集範囲 | 依存 | 状態 | worktree / branch | Devinセッション（任意） |
| --- | --- | --- | --- | --- | --- | --- |

状態は`planned` / `running` / `review` / `integrated` / `blocked` / `failed`のいずれか。
担当が自分の行を更新し、Opusは担当の行と契約を管理する。

## 検証の階層

| 階層 | 誰が | 何を | コマンド |
| --- | --- | --- | --- |
| 局所 | 担当（sandbox外では長時間ならDevinも可） | 変更に直接関係するビルドとテスト | `dotnet build ResoLoop.slnx --no-restore` / `dotnet test tests/RLoop.Tests/RLoop.Tests.csproj --no-build` |
| 統合 | 担当 | 担当機能を統合した後のオフライン全体 | `dotnet build ResoLoop.slnx --no-restore` → `dotnet test ResoLoop.slnx --no-build` |
| live | Opusが可否と対象を決め、担当が実行 | 統合後に1回 | `AGENTS.md`のopt-in手順（`RESOLOOP_RUN_INTEGRATION=1`、`Category=Integration`） |

- 同じcommit・環境・test集合の検証は再実行しない。
- 共通基盤（`RLoop.Core`、`IResoniteClient`、CLI引数解析）の変更や影響範囲が不明な変更は、検証範囲を広げる。
- liveは共有のResoniteセッションを変えるので、同時に1本だけにする。実験は`ResoLoop_Test*` Slotの下だけで行い、正確なIDで片付ける。スキップされたliveテストを成功扱いせず、未検証として報告する。
- 未整備: live実行のラッパー（環境変数設定・排他ロック・実行記録`.test-runs/runs.jsonl`）はまだない。整備されるまで、liveの実行方法と対象はOpusがcharterで個別に指示する。

## Devinを任意で使う場合

大量の並列実装や長時間build/testのとき、sandbox外ではDevin SWE-2 Maxを任意手段として使ってよい。Codexの`--sandbox workspace-write`内はネットワークが遮断されるため、そこからは起動できない。必要ならOpusへ返し、sandbox外での起動と編集範囲を調整する。使う場合は次を守る。

- タスクごとにUTF-8の指示ファイルを一時領域へ作り、担当ツリーで起動する。Claudeとの会話はDevinへ自動共有されないため、指示ファイルだけで完結させる。指示ファイルはcommitしない。
- Devinは本体ツリー（未信頼）とworktree外を読めない。commitは担当が行う。同じタスクを重複起動しない。
- 許可外のコマンド・Web・リポジトリ外の読み取りは、1回でも拒否されるとrun全体が止まる。確認なしで実行できるのは`.devin/config.local.json`で許可した完全一致のコマンドだけ。
- スロットはユーザーが一度だけ対話的に信頼する（`cd ..\resoloop-worktrees\slotN`、`devin`、信頼を承認して終了）。Devinの信頼設定ファイルは編集しない。未信頼ならDevinを使わず担当が自分で実施する。
- 大きいファイルは複数回に分けて書かせる（出力上限で打ち切られるため）。

```text
devin --model swe-2-max --permission-mode accept-edits -p --prompt-file "<指示ファイルの絶対パス>"
devin list --format json
devin --resume "<session-id>" --model swe-2-max --permission-mode accept-edits -p --prompt-file "<追加指示ファイルの絶対パス>"
```

指示ファイルには、役割、目的、編集範囲、既存の型・API契約、受け入れ条件、変更してはいけない事項、許可済みの検証コマンド、既存ユーザー変更の保持、commit/push/releaseの可否、`AGENTS.md`の規則（依存方向、Resonite安全規則、CLI挙動変更時のREADMEと`skills/codex/*`の同期）を書く。報告の先頭は`DONE` / `NEEDS_INPUT` / `BLOCKED`とし、変更内容、10行以内の検証要約、未解決事項を返させる。`NEEDS_INPUT`には既存の契約から答えて同じセッションを再開する。完了報告や終了コードだけで受け入れず、差分と検証結果を確認する。

## 決定論的な検証を自動化する（2026-10-05）

build、テスト実行、件数集計、差分・hash照合、merge前のGit状態確認だけを行うために、追加のエージェントを起動しない。マネージャが `pwsh -File scripts/agents/verify.ps1 -Profile All` を直接実行する。影響が限られる場合は `Dotnet`、`Jsx`、`JsxContract`、状態確認だけなら `Inspect` を選ぶ。使い方と対象範囲は [定型検証の手順](deterministic-verification.md) にまとめる。

全文ログはファイルへ保存し、通常は `summary.json` と10行以内の実行結果だけを読む。失敗時は該当stepのログを調べ、修正が必要になった時点で実装担当へ渡す。成功したstepを理由なく繰り返さず、全体検証は最終候補にまとめる。TSのコンパイルを含む実行では、別のtypecheckを重ねない。

製品コードの修正、原因の判断、設計、必要な独立レビュー、受け入れは既存の担当方針を維持する。このrunnerはoffline専用で、実機実行・merge・push・ブランチ削除は行わない。liveの許可と安全規則、検証再利用の一致条件も変えない。
