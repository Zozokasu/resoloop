# エージェント体制と実行プロトコル

`/ccg:go`を含む通常の開発で使う役割分担。
`/ccg:go`実行時は、プロジェクトhook（`scripts/agents/ccg-go-roles.mjs`）がこの文書の要点を注入する。
ccgの戦略・モデル解決・タスク作成（`.ccg/tasks/<task>/task.json`）はccg-workflowの手順をそのまま使い、この文書は「誰がどの判断と作業をするか」だけを上書きする。

ccgの戦略にある「実行モードの選択」（Agent Teams / Codex / Claude）は、このプロジェクトではユーザーの常設承認により「Sonnet一担当（Devinは任意）、Codexは高影響変更のレビュー」に固定する。Opusは選択肢を尋ねずにこのモードを宣言する。
それ以外のHARD STOPゲートは、戦略の定義どおりに残す。

アーキテクチャ、Resonite安全規則、上流の扱いは`AGENTS.md`を正とし、この文書では繰り返さない。

## 方針の前提

通常制作の主経路は直結: TSX / 手書きJSON / ProtoGraph → ResoLoop（展開・型検証・参照解決・差分・局所観測・実行・診断）→ RLoop.ResoniteLink（選択した単一セッション）/ RLoop.Flux → IFluxDeployer → F# deployer → Flux-SDK。
Workbench backend（`src/RLoop.Workbench`、`--backend workbench`、`wb`）は凍結して保持し、新機能・書込みadapter・backend同等化へ投資しない。工程は`plan/resoloop-slimming-plan.md`のS1〜S5に従う。

## 役割と決定権

| 役割 | 実体 | 担当 | 決めてよいこと |
| --- | --- | --- | --- |
| プロデューサ・議長 | メインセッション（Opus） | 目標・優先順位・共有契約（Core公開型、`IResoniteClient`、CLI引数、エラーコード、state）・charter・受け入れ・担当間の衝突解決・live検証の可否と対象 | 担当の設置・権限付与、契約の確定。ユーザーの目的や許容範囲を変える判断だけはユーザーに聞く |
| 担当（一担当） | `feature-manager`エージェント（Sonnet） | 調査→実装→関連build/test→短い報告を一貫して行う。worktreeスロットの`agent/*`ブランチで作業し、commitまで行う | 承認された範囲内の局所的・可逆的な判断 |
| 実装補助（任意） | Devin SWE-2 Max（`--model swe-2-max`） | 大量の並列実装や長時間build/testを担当が選んで任せる | 編集範囲内の実装判断 |
| 高影響変更のレビュー | Codex（`smart-friend`スキル、またはccgの`codeagent-wrapper --backend codex`） | 別担当レビュー、設計資料・引き継ぎ資料 | なし（指摘と資料を返す） |

- Opusはファイル単位の実装をしない。charterは目的・範囲・契約・受け入れ条件だけにする。
- Opus自身の調べものは`scout`サブエージェントに委譲し、`path:line`と要点だけを受け取る。
- 別担当レビューは高影響変更に限る（削除、所有境界、state migration、応答喪失、公開API、複数projectの設計変更）。小変更ごとに議長→担当→実装者→別reviewerの全段を必須にしない。具体的な指摘を直した差分と検証を受け取り、理由のない追加roundを要求しない。レビューは対象のcommitか差分を明示する。

## 通信とエスカレーション

- Opus → 担当: `Agent`（`subagent_type: "feature-manager"`。使えない場合は`general-purpose`に`model: "sonnet"`を付け、`.claude/agents/feature-manager.md`を読ませる）。
- 担当 → Opus: 最終報告、または途中の`ESCALATE`報告。担当は他の担当を直接起動できないので、担当間の調整はOpusが中継する。
- `ESCALATE`の条件: 他機能の契約変更、層の依存方向や責務境界の変更、要件解釈が大きく分かれる、担当同士で合意できない。報告には「論点・選択肢・推奨と理由・影響する担当」を書く。

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
- ビルド・テストの全文ログは`.ccg/tasks/<task>/`に保存し、Opusへは10行程度の要約（件数、失敗名と一行理由、skip数、所要時間）だけを返す。Opusは要約と`git diff --stat`を読む。

## 追跡

タスクごとに`.ccg/tasks/<task>/assignments.md`の1表で管理する（ccgのタスクディレクトリを使い、新しい管理資料は作らない）。

| ID | 担当 | 編集範囲 | 依存 | 状態 | worktree / branch | Devinセッション（任意） |
| --- | --- | --- | --- | --- | --- | --- |

状態は`planned` / `running` / `review` / `integrated` / `blocked` / `failed`のいずれか。
担当が自分の行を更新し、Opusは担当の行と契約を管理する。

## 検証の階層

| 階層 | 誰が | 何を | コマンド |
| --- | --- | --- | --- |
| 局所 | 担当（長時間ならDevin） | 変更に直接関係するビルドとテスト | `dotnet build ResoLoop.slnx` / `dotnet test tests/RLoop.Tests/RLoop.Tests.csproj --no-build` |
| 統合 | 担当 | 担当機能を統合した後のオフライン全体 | `dotnet build ResoLoop.slnx` → `dotnet test ResoLoop.slnx --no-build` |
| live | Opusが可否と対象を決め、担当が実行 | 統合後に1回 | `AGENTS.md`のopt-in手順（`RESOLOOP_RUN_INTEGRATION=1`、`Category=Integration`） |

- 同じcommit・環境・test集合の検証は再実行しない。
- 共通基盤（`RLoop.Core`、`IResoniteClient`、CLI引数解析）の変更や影響範囲が不明な変更は、検証範囲を広げる。
- liveは共有のResoniteセッションを変えるので、同時に1本だけにする。実験は`ResoLoop_Test*` Slotの下だけで行い、正確なIDで片付ける。スキップされたliveテストを成功扱いせず、未検証として報告する。
- 未整備: live実行のラッパー（環境変数設定・排他ロック・実行記録`.test-runs/runs.jsonl`）はまだない。整備されるまで、liveの実行方法と対象はOpusがcharterで個別に指示する。

## Devinを任意で使う場合

大量の並列実装や長時間build/testのとき、担当はDevin SWE-2 Maxを選んで使ってよい。使う場合は次を守る。

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
