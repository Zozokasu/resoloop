# ResoLoop

ResoLoopは、AIコーディングエージェントがResoniteLink経由でResoniteのSlot・Component・ProtoFluxを観測・編集・検証するための.NET CLIである。
利用者向けの説明は`README.md` / `README_JA.md`、詳細は`README-DETAILS.md`にある。

アーキテクチャ、コマンド、Resonite安全規則、上流の扱い、テストとSkillの同期規則は[`AGENTS.md`](AGENTS.md)を正とする。
この文書はClaude向けの入口で、AGENTS.mdと重複する規則は書かない。

このリポジトリはorange3134/resoloopのforkである。当面はupstreamを取り込まない。通常制作は直結（ResoniteLink）主経路へ統一し、Workbench backendは凍結・S5で除去予定である（計画: `plan/resoloop-slimming-plan.md`）。方針と調査結果は[`docs/dev/fork-strategy.md`](docs/dev/fork-strategy.md)にある。

## 製品ファイルと開発用ファイルの区別

- `skills/codex/*/SKILL.md`は、`resoloop init`で利用者のプロジェクトへ配布される**製品の一部**である。開発エージェントへの指示ではない。CLIの挙動を変えたときだけ、AGENTS.mdの規則に従って同期する。
- 開発エージェント向けの指示は、この文書、`AGENTS.md`、`docs/dev/agent-workflow.md`、`.claude/agents/`に置く。

## 調査と出力

大量のコード調査は実装担当や調査役に委譲し、概要と特徴的な実装のファイルパス・1-based行番号を受け取る。
これは読み取りコストを下げるための協働方法であり、メイン担当がファイルを読めないという意味ではない。

## 計画とフィードバック

- 正式な計画は`plan/`（git管理外）に置く。ユーザーが明示的に頼まない限り、エージェントは`plan/`を変更しない。
- 方針変更の提案は`feedback/`（git管理外）に書く。提案は正式計画を自動で置き換えない。反映するかはユーザーが決める。
- ccg-workflowの手順、起動方法、モデル解決、managed領域（`.ccg/`）はccg-workflowに委ねる。独自の起動コマンドを作らず、ccg管理ファイルを自動変更しない。
- フェーズやマイルストーンの区切りでは引き継ぎ文書を書く。仕組み、確認済みの事実、後回しにした項目、未検証の事項を分け、未達の完了条件を完了扱いにしない。

## エージェント体制

2026-10-02のユーザー決定により、三層の役割と通常二層の実行へ切り替える。正本は[`docs/dev/agent-workflow.md`](docs/dev/agent-workflow.md)。旧「6.1-sol一担当が実装まで通す」方針を置き換える。

- Opus 5.5 highは目標・共有契約・付与権限・節目の受け入れを持つ。通常の情報補充、局所修正、モデル交代はマネージャへ任せる。
- Codex `gpt-6.1-sol` highは技術管理に専任し、製品コードの実装・修正・競合解消を下部エージェントへ委任する。管理規則は[`.claude/agents/feature-manager.md`](.claude/agents/feature-manager.md)。Sonnet版は管理役の予備である。
- 実装担当は[`.claude/agents/coding-worker.md`](.claude/agents/coding-worker.md)に従う。2026-10-08のユーザー決定により、Codexの実装と修正は`gpt-6-luna` high、ログの取得・build／testの実行・結果の要約は`gpt-6-luna`へ渡す。lunaへの契約は短く明確に書き、実装にはtestを含め、testのファイルを大きくしすぎない。実効モデルとeffortを記録する。
- 目的・契約・権限・所有・検証・受け入れを固定し、その内側で分割、参照情報、モデル、並列数を調整する。指示、worktree、sandboxだけで限定閲覧を保証したと報告しない。
- 新体制の最初の試験はS3の実機テストとフィードバック修正。ユーザーの「S3実装完了」という報告を起点に、テスト対象の正確な差分と既存証拠を確認する。実機テストの実行許可・対象は別に確認し、この指示変更を実行許可と解釈しない。

起動方法、実効権限、委任能力の確認、局所・統合・live検証、重点レビュー、ログ、commit/mergeの規則は正本へ集約する。ccg本体のlifecycleとHARD STOPゲートは維持する。

## 完了の報告

- 成功・失敗・skip・未検証を分けて報告する。skipしたテストやlive未実行を成功扱いしない。
- live検証は、どのセッション・どの`ResoLoop_Test*` Slotに対して行ったかを証拠と一緒に報告する。
- 依頼されていないcommit、push、リリースはしない。
