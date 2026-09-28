# Workbench対応 W1・W3 引き継ぎ

作成: 2026-09-28
対象: main `623873c`（W1のmerge `3a86bc2`、W3のmerge `623873c`。未push）
設計: `plan/workbench-design.md`（承認済み。git管理外）。計画: `plan/workbench-roadmap.md`

## 1. 仕組み

### W1 Workbench接続基盤

- **`src/RLoop.Workbench`**（RLoop.Coreを参照し、RLoop.ResoniteLinkは参照しない）
  - `Protocol/`: resonite-workbenchの `src/ResoniteWorkbench.Protocol` の10ファイルを、commit `d549280` から変更せずに取り込んだもの。名前空間も `ResoniteWorkbench.Protocol` のまま。取り込み元の記録は `Protocol/README.md` にある。取り込みはユーザーの許諾済み。
  - `WorkbenchResoniteClient`: `IResoniteClient` の実装。W1で実装したのは、接続と `GetSessionInfoAsync`（`session.status` を読む）だけで、それ以外のメソッドは `BACKEND_UNSUPPORTED` を投げる。
    - `session.status` は厳密に読む。`meta.stale`、必須フィールドの欠落、`Connected` の場合の connectionId の不一致、`Disconnected` なのに `connection` がある場合は、どれも `WORKBENCH_UNAVAILABLE` にする。`WORKBENCH_NOT_CONNECTED` にするのは、正規の `Disconnected` の場合だけ。
  - `WorkbenchConnectionMeta`: 最新の `meta`（connectionId、worldRevision、scope）を保持する。W2 / W4で使う。
- **`src/RLoop.Cli/ResoniteClientFactory.cs`**: **接続済みの** `IResoniteClient` を返す、唯一の生成関数。
  - backendの判定を、URLの解決より前に行う。
  - linkのときだけ、URLを解決して `ConnectAsync(uri)` を呼ぶ。
  - 接続に失敗したら、生成したclientを解放してから例外を投げ直す。
  - テスト用に、client生成を差し替えられる `internal` のオーバーロードがある（`InternalsVisibleTo(RLoop.Tests)`）。
- **`src/RLoop.Cli/BackendSupport.cs`**: `--backend workbench` のときのコマンドの対応表（`WorkbenchSupport` が正本）。未対応のコマンドは、接続する前に `BACKEND_UNSUPPORTED` で失敗させる。
- **CLIと設定**
  - `--backend link|workbench`（既定は `link`）、`RESOLOOP_BACKEND`。
  - `--workbench-pipe`（既定は `ResoniteWorkbench.Rpc.v1`）、`RESOLOOP_WORKBENCH_PIPE`。
  - `src/RLoop.Core/Configuration.cs` に設定を追加した。ws/wssの検証は変えていない。
  - `resoloop wb status [--json]` は、`--backend` の値に関係なく、常にWorkbenchに接続する。
- **エラーコード**: `WORKBENCH_UNAVAILABLE`、`WORKBENCH_PROTOCOL_INCOMPATIBLE`、`WORKBENCH_NOT_CONNECTED`、`BACKEND_UNSUPPORTED`。
- **利用者向けの説明**: `README-DETAILS.md` の「Workbench backend」節。

### W3 JSX最小（`tools/resoloop-jsx`）

- **構成**
  - Node 22以上。依存は `typescript` 5.9.3（固定）だけ。`@types/node` は使わず、`src/node-ambient.d.ts` で代用している。
  - 独自のjsx runtime（`jsxImportSource: "resoloop-jsx"`）。`Slot` / `Component` / `Fragment` は、参照が一致するかで判定するマーカー関数。
- **出力**: `"schemaVersion": "1"` の、展開済みのapply文書1ファイル。
- **入口**
  - 通常の入口 `resoloop-jsx`: `key` が型の上で必須。
  - draft用の入口 `resoloop-jsx/draft`: keyを省ける。`--draft` のときだけ、`親のkey/名前#同じ種類の兄弟の中での順位` からkeyを作り、警告を出す。
- **エラー**: 空白だけのkey、keyの重複、同名の兄弟、`NaN` / `Infinity`（`NON_FINITE_NUMBER`、どのpropかのパス付き）は、どれも終了コード1。型エラーは終了コード2。
- **参照の補助関数**: `ref.slot` / `ref.component` / `ref.member` / `ref.slotMember`。`ref.asset` は、assetsの宣言が未対応なので公開していない。
- **契約テスト**: `npm --prefix tools/resoloop-jsx run contract` は、fixtureの出力を、ビルド済みの `resoloop validate`（非strict）に通す。CLIが未ビルドなら、終了コード3で「未実行」になる。

## 2. 確認済みの事実

- main `623873c` でのオフライン検証（2026-09-28、slot3をdetached HEADにして実行）:
  - ビルドは警告0・エラー0。
  - テスト: RLoop.Tests 334/334、RLoop.Workbench.Tests 26/26。
  - resoloop-jsx: 型検査は通過、テストは40/40、契約テストは9/9。
- W1のブランチ上で、`dotnet test` を3回連続で実行し、3回とも失敗0だった。以前不安定だったテストの原因は、コンソール出力の差し替えと、xUnitの並列実行との競合だった。3つのテストクラスとも、`--report` を読んで検証する形に変えている。
- CodexのレビューはRound 1とRound 2の2回（記録は `.ccg/tasks/workbench-w1-w3/`。git管理外）。P0は無かった。指摘のうち #7 と #8 以外は、Round 2と3で直した。
- 取り込んだProtocolの10ファイルは、`d549280` と完全に一致する。`Abstractions.cs` と `WorldService.cs` は変更していない。

## 3. 後回しにした項目

- **#7 Protocolのキャンセル**: `WorkbenchRpcClient.SendAsync` が、呼び出し側のtokenを受け取らない。resonite-workbenchの `feedback/2026-09-27-rpc-client-cancellation.md` に提案として書いた。Workbench側で直ったら、取り込み直す。
- **#8 captureとFluxの経路**: この2つの経路は、まだ生成関数の外でURLを解決している（`Program.cs` の capture / Flux の箇所）。今は `BACKEND_UNSUPPORTED` で止めているので、そこまで到達しない。W2 / W4でこれらのコマンドを対応させるときに、生成関数の中へ移す。
- **対応表**: `validate` / `diff` / `plan` / `uix` などが `planned-w2` に入っている。これらのうち、接続を必要としないものがあるかは、W2で見直す。
- **W3**: entryは1つの `.tsx` ファイルだけに対応している（複数のファイルに分けたentryは未対応）。npmでの配布方法、Reflectionから型を生成する機能は、W7で扱う。
- **Codex**: Round 3の修正の差分は、まだCodexにレビューしてもらっていない。

## 4. 未検証の事項

- **live**: `resoloop wb status` を、実際に動いているWorkbenchに対して試していない（W1の完了条件の残り）。RLoop.IntegrationTestsの19件は、`RESOLOOP_RUN_INTEGRATION` が未設定で何も実行していない。
- **読み取り経路（W2の前提）**: 設計記録の5.3 (0) のとおり、Workbenchの `SlotRecord` にはtransformとpersistentが無い。W2で、読み取りの対応表を作り、Workbench側の `feedback/` にRPCの追加を提案する（ユーザー承認済み）。
- **ID**: RefIDの形式、同じsessionかの判定、IDが有効な範囲が、直結経路とWorkbench経路とで一致するか（W2のlive比較で確かめる）。

## 5. 作業体制についての知見

- Devin（SWE-2）について
  - 自分のworktreeの外のファイルを読めない。
  - `.devin/config.local.json` で許可されたコマンドしか実行できない。`git add` / `commit` も実行できないので、commitはmanagerが行う。
  - 本体のツリーは、Devinに未信頼である。main上の検証は、slotを `git checkout --detach <commit>` で切り替えて行う。
- feature-managerは、Devinの完了を待つ途中で、最終報告を返さずに止まることがある。そのときは、slotのcommitを確かめて、新しいmanagerに検証を取り直させる。
- slot1〜3は、mainの `623873c` にdetached HEADで置いてある。`new-worktree.ps1` は、detached HEADのslotにも、そのまま新しいブランチを作れる。
