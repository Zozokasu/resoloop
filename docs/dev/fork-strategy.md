# fork開発方針（直結統一・Workbench凍結）

このリポジトリ（Zozokasu/resoloop）は orange3134/resoloop のforkである。

## 現在の方針（2026-09-30改定）

- **通常制作の主経路は直結に統一する。** TSX / 手書きJSON / ProtoGraph → ResoLoop（展開・型検証・参照解決・差分・局所観測・実行・診断）→ RLoop.ResoniteLink（直結、選択した単一セッション）/ RLoop.Flux → IFluxDeployer → F# deployer → Flux-SDK。
- **Workbench backend（`src/RLoop.Workbench`、`--backend workbench`、`wb`コマンド）は凍結して保持する。** 新機能・書込みadapter・64-op batch・RPC export・backend同等化へ投資せず、接続失敗時の自動backend切替もしない。S5で通常依存から除去する予定。旧W4（Workbench書込みRPC）は再開しない。旧W4〜W7はS1〜S5工程（`plan/resoloop-slimming-plan.md`）へ置換済み。
- 歴史: 2026-09-26に「Workbench対応（JSX + ProtoGraph → WorkbenchObject → Workbench RPC）を優先する」と決めたが、上記へ改定した。
- upstreamの継続的な取り込み、マージ手順、長期の開発方針は、内部形式が固まってから検討する。それまでupstreamの同期は行わない。
- 分岐点: upstream `7d73071`（Handle transient checkpoint disappearance after Windows sharing contention）。以後のforkの変更は、この点からの差分として後で評価する。

## 当面のルール

1. upstreamのファイルを変更してよい。直結主経路の整備に必要なら、Program.cs、Core、宣言形式の拡張も妨げない。
2. ただし、手間が同程度なら新しいファイル・新しいプロジェクトに置く方を選ぶ（例: `src/RLoop.Core`配下の新規ファイル、`tools/resoloop-jsx`）。後でマージを検討するときの材料になる。
3. upstreamのファイルに入れた変更は、commitメッセージで分かるようにする。マージ検討時に`git diff 7d73071 -- <upstreamのファイル>`で洗い出せるので、別の一覧は作らない。

## 調査で分かったこと（2026-09-26、再検討時の参考）

### upstreamの性質

- 作者は1人（orange3134）。外部PRのマージ実績はない（open 1件のみ）。CONTRIBUTING.mdはない。拡張点をupstreamに入れてもらう前提は置けない。
- 変更が多い: 2026-08-25〜09-25で約40コミット。変更の集中するファイルは`README.md`、`skills/codex/resonite-build/SKILL.md`、`src/RLoop.Cli/Program.cs`、`docs/DECLARATIVE.md`、`WorldService.cs`、`ApplyWorkflow.cs`。
- ライセンスはAGPL-3.0-or-later。fork改変版も同ライセンスで、改変ソースを公開し続ける。

### JSXとWorkbench対応の意味（2026-09-26時点の調査。Workbenchは現在凍結）

- JSX（TSX/JSX）はUI記述ではなく、WorkbenchObjectとResoniteオブジェクトの宣言的な定義言語。ProtoGraph（ProtoFluxのソース）と対で使う（workbench `plan.md:11,49`、`feedback/2026-09-25-direction-change.md:7`）。
- TSX/JSXの評価・型検査、共通IR、desired state、diff/plan/apply、静的診断はclient（ResoLoop）側の責務。Workbenchはこれらを解釈しない（workbench `plan.md:49,71,372`）。
- 連携の向きはResoLoop → Workbench公開RPCの一方向。
- Workbench RPC: Windows Named Pipe（既定名`ResoniteWorkbench.Rpc.v1`）、length-prefix JSON-RPC、`hello`/`welcome`でversion交渉。書き込みは個別primitiveではなくmutation plan単位（`mutation.validate` → `mutation.execute`（confirmationToken、readback付き）→ `mutation.outcome`）。観測は`world.slot`/`world.hierarchy`/`member.read`、Reflectionは`reflection.*`、Fluxは`flux.build`/`flux.deploy.*`（workbench `docs/rpc-protocol.md:120-148`）。

### ResoLoop側の構造

- `IResoniteClient`（`src/RLoop.Core/Abstractions.cs:3-34`）はResoniteLink固有の型を漏らしておらず、Coreは`IResoniteClient`経由でしか世界にアクセスしない。実装は`ResoniteLinkClientAdapter`だけで、`Program.cs`の5箇所（208, 228, 345, 870, 955行）で直接生成している。factoryやDIはない。
- `ValidateComponentMemberAsync` / `CallComponentMethodAsync` / `ImportAssetAsync`はデフォルト実装（未対応の例外）がある。`IReflectionMetadataClient`と`IResoniteClientDiagnostics`は任意のインターフェースで、`as`キャストで判定されている。
- 接続URLは`ws`/`wss`以外を拒否する（`src/RLoop.Core/Configuration.cs:103-114`）。
- CLIの振り分けは`Program.Main()`のif連鎖（接続不要コマンドは227行より前）と`RunResonite`（451行〜）のswitch。拡張機構はない。`CommandLine.cs`の真偽値オプションは固定のHashSet。
- 宣言形式はschema-v1 JSON（`docs/DECLARATIVE.md`）。入力はroot 1ファイル＋相対include、`.json`固定、stdin不可。上限は10,000 node・10 MiB・64ファイル（`limits.expandedNodes`で最大250,000）。テンプレートは`$prototype`/`$instance` + `$with` + `$repeat`とUIXの`$recipe`で、値の静的置換だけ。`schemaVersion`は`"1"`のまま構文が追加されている。
- 同梱Skillの列挙はハードコード（`src/RLoop.Core/BundledSkillManager.cs:17-25`と`RLoop.Core.csproj`の`EmbeddedResource`）。ディレクトリを置くだけでは配布されない。

### 設計の候補（歴史。2026-09-30改定前の調査）

- JSX: TSXをschema-v1 JSONへ変換する前段ツールにすれば.NET側を変えずに済む。条件分岐やprops合成は変換時に解決し、実行時の振る舞いはProtoGraphに置く。
- Workbench: 書き込みはmutation plan単位でprimitiveと1対1に対応しない、という調査結果が残る。Workbench書込み経路は今後投資しない（凍結）。

## 未確認事項

- Workbench書込みRPC（`mutation.execute`のplan形状、confirmationToken、readback等）に関する旧未確認事項は、W4を再開しないため追わない。
- `IReflectionMetadataClient` / `IResoniteClientDiagnostics`を実装しない場合に、どのコマンドが機能を失うか（直結側の整理で必要になれば確認する）。

## 保留中のfork独自ブランチ

- `feature/hierarchy-observation`（`a4a3f55`）: `hierarchy profile/query`と`snapshot create/diff`。upstreamの`observe`（`34a0f32`）と役割が近い。S1-3で直結観測基盤として統合する（`plan/resoloop-slimming-plan.md`）。
