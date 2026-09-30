# Workbench対応 W2 引き継ぎ（読み取り経路）

作成: 2026-09-30
対象: main `e85a015`（未push）
- W2-B の merge は `6449678`。
- W1 round 4 の merge は `a9d44c7`。
- `scripts/dev/workbench.ps1` の merge は `30043b8`。
- live 修正 R2/R3 の merge は `e85a015`。

設計: `plan/workbench-design.md`。計画: `plan/workbench-roadmap.md`。前段: [`workbench-w1-w3-handoff.md`](workbench-w1-w3-handoff.md)。読み取りの対応表: [`workbench-read-mapping.md`](workbench-read-mapping.md)。

## 1. 仕組み

### 読み取り経路（`src/RLoop.Workbench`）
- **Protocol**
  - resonite-workbench `7d40c92` から取り込み直した。R1〜R3 の反映として、`reflection.search`・`reflection.enum`・Slot の `transform` / `persistent` / `isReferenceOnly` が入った。
  - 取り込み元は `Protocol/README.md` に記録してある。
- **`WorkbenchResoniteClient.Read.cs`**
  - `IResoniteClient` の読み取り5メソッドを実装した。
    - `GetSlotAsync`: `world.observe`
    - `GetComponentAsync`: `reflection.component` + `member.read`
    - `SearchComponentTypesAsync`: `reflection.search`
    - `DescribeComponentTypeAsync` / `DescribeTypeAsync`: `reflection.component` / `reflection.type`、enum は `reflection.enum`
  - wire から Core モデルへの変換は mapper に集めた。
    - `WorkbenchSlotMapper`: `world.observe` を `SlotInfo` に変換する。transform の JSON 文字列をパースする。深さと Slot 数の上限も判定する。
    - `WorkbenchReflectionMapper`
    - `WorkbenchMemberMapper`
  - 1回の読み取りの中で接続 ID が変わったら失敗させる（`ReadOperation`）。
    - `reflection.*` の「不明」の結果（`value` が存在して null、`unknownReason` あり、meta の接続 ID が null）は、接続の変化とはみなさない。
    - 代わりに、型名と Workbench の理由を含む `WORKBENCH_UNAVAILABLE` で失敗させる。
    - `value` が欠けた応答は、不正な応答として扱う。
  - 型キャッシュ（`_componentTypes`、`_memberNames` の中の DeclaredMembers）は、最後に確認した非 null の接続 ID が変わると消す。接続 ID が null の間は、キャッシュを参照しない。
  - member の `type` は、`reflection.component` の field 定義の `valueType` から埋める。ジェネリック、nullable、`isGenericParameter`、入れ子の member は null のままにする。
- **上限**（`WorkbenchErrors.cs` の `WorkbenchLimits`）
  - 深さは 0〜32、Slot は 8,192 まで。超えた要求は切り詰めずに、`WORKBENCH_OBSERVE_LIMIT_EXCEEDED`（終了コード7）で拒否する。
  - 深さ33以上と `--depth -1` は、RPC を呼ぶ前に拒否する。
  - 利用者が指定できるコマンドの既定の深さは、どれも32以下（hierarchy 2 / find 8 / inspect 1 / tool audit 16）。このため、charter 案にあった「Workbench のときだけ既定を32にする切り替え」は入れていない。
- **要求した深さの範囲内で、Workbench が読めなかった子 Slot（`ReadFailed`）がある場合**
  - 不完全な結果を返さず、`WORKBENCH_UNAVAILABLE`（終了コード4）で失敗させる。
  - 範囲の外にある読めなかった子は、スタブとして返す。
- **`find --direct-children`**: `WorldService.FindAsync` で1段だけを評価する。直結経路でも、孫のスタブが混ざらなくなった。

### CLI（`src/RLoop.Cli`）
- `BackendSupport.cs` の `WorkbenchSupport`（正本）で、Workbench 経由の各コマンドの扱いを決めている。
  - 対応: status, ping, hierarchy, find, observe, inspect, type, validate
    - `validate --strict` と `type check --manifest` は、`BACKEND_UNSUPPORTED` で明示的に失敗する。
  - W2 のうちに対応予定のまま（planned-w2）: doctor, uix, item, tool, capture。理由は `DeferralReason` と README-DETAILS にある。
  - W4 で対応予定（planned-w4）: diff, plan, slot, component, apply, test, flux
  - 直結経路専用（link-only）: scene, blender, logs
- `ResoniteClientFactory.cs`（W1 round 4）: 接続に失敗した後の解放でも例外が出たときは、元の例外を投げ直す。解放時の例外は、元の例外の `Data["disposeException"]` に残す。

### 開発用スクリプト `scripts/dev/workbench.ps1`
- `start` / `connect` / `status` / `stop` / `help` がある。Windows PowerShell 5.1 用。
- `start`
  - App を最小化した状態で起動し、PID を `%LOCALAPPDATA%\resoloop-dev\workbench-app.json` に記録する。
  - App のビルドが repo の HEAD より古いときは警告する。`-RequireFreshBuild` を付けると、警告の代わりに失敗させる。判定はファイルの更新時刻で見る目安で、どの commit からビルドしたかは確認できない。
- `connect`
  - 候補が0件のときは、`-DiscoverTimeoutSec`（既定45）まで探索を再試行する。
  - 候補が2件以上のとき、または別の接続がすでにあるときは、何もせずに失敗する。
  - 接続後に、sessionId が一致するかを照合する。
- `stop`: 記録した PID・プロセス名・起動時刻がすべて一致するプロセスだけを終了させる。
- 常時承認: 起動・接続・停止と、読み取りのみの確認は、そのつど確認しなくてよい（ユーザー決定 2026-09-30）。Resonite への書き込みは、これまでどおり作業ごとに許可を取る。

## 2. 確認済みの事実

- **オフライン**（main と同じ tree の `e1aedf7`、slot）
  - build 0 warning / 0 error
  - RLoop.Tests 358/358、RLoop.Workbench.Tests 188/188
  - `30043b8` の時点で、resoloop-jsx は test 40/40、contract 9/9
  - `workbench.ps1` の構文エラー 0
- **live 比較**（読み取りのみ、2026-09-30。詳細は `.ccg/tasks/workbench-w2-read/live-w2b.md`）
  - 環境: Resonite 2026.9.18.82 / ResoniteLink 0.13.1.0。session「ResoniteLink テスト中」、`ws://localhost:35057`。resonite-workbench は `7d40c92` をビルドし直して使った。
  - 両方の経路で一致したもの:
    - `find`（名前・Component・直接の子）
    - `type search`、`type describe`（enum の値を含む）
    - RefID の形式と値
    - `inspect --members` の member の値
    - 深さ内の transform / isPersistent / tag
  - `connectionId` は接続ごとに違う（Workbench 経由 141 / 直結 142）。同じワールドかどうかの判定には使えない（仕様どおり）。
  - 直結経路の `type search` は空にならなかった（`Light` で11件、Workbench 経由と一致）。「`GetAllComponentTypes()` は常に空を返す」という Workbench 側の報告は、この環境では当てはまらなかった。
  - member の値の読み取りは、1往復約10ms。`hierarchy --depth 2 --include-components`（Root）は、推定約7,000往復・約70秒になる（直結経路は1.2秒）。
- **merge 後の再確認**（`e85a015`）
  - `connect` の再試行は動いた（起動直後から13秒で接続）。
  - B1 のメッセージも期待どおりだった（`inspect Root --members` が、GradientStripTexture の理由を含む `WORKBENCH_UNAVAILABLE` で失敗）。

## 3. 後回しにした項目

課題の本文と直し方の案は `feedback/2026-09-30-workbench-read-gaps-from-w2b-live.md`（git 管理外）にある。どれもユーザーが「記録のみ」と判断した。

- **R-1（回帰）**: `type describe [FrooxEngine]FrooxEngine.GradientStripTexture --backend workbench` が、`WORKBENCH_UNAVAILABLE` で失敗する。
  - `30043b8` では、Program.cs が `DescribeTypeAsync` にフォールバックして成功していた。
  - R3 の「`reflection.type` が既知で `isComponent=true` なら UNAVAILABLE」という変更が、このフォールバックを塞いだ。直結経路では成功する。
- **R-2（表記ずれ）**: B2 で埋めた member の `type` が、Workbench の表記（`bool` / `int`）のまま出る。直結経路は `System.Boolean` / `System.Int32`。
- **R3 レビューの残り4件**（`.ccg/tasks/workbench-w2-read/review-w2b-r3.md`）
  - 型キャッシュが、`session.status` を挟まない接続の切り替えと、並行読み取りに弱い（P1 が2件）。CLI は1コマンド1プロセスなので起きにくいが、Workbench の機能を取り込んで長く動くプロセスで使うと、現実の問題になる。
  - `workbench.ps1` の探索の期限が、わずかに超えうる。
  - `-RequireFreshBuild` のとき、DLL が無いと検査を飛ばす。
- **Workbench 本体の課題**（取り込み時に設計する）
  - 「不明」の結果に接続 ID が付かない。
  - `GradientStripTexture` の member 定義を ResoniteLink が読めない（NullReferenceException）。
  - ユーザーの Slot が常に除外される（`world.observe` に、除外をやめる引数が無い）。
  - member の値を一括で読む手段が無い（旧 R4）。
  - 深さの外のスタブと、Slot ごとの `members` が欠ける。
  - App を起動してから session が見つかるまで、30〜40秒かかる。
- **`feature/hierarchy-observation`（`a4a3f55`、ローカルの1 commit）**: ユーザー判断は「W2 の後に別タスクで移植」。
  - 追加されるのは `hierarchy profile/query` と `snapshot create/diff`。
  - `snapshot` は直結経路専用（LinkOnly）として登録し、`hierarchy profile/query` は Workbench では深さ32までにする方向。
  - `skills/codex/resonite-inspect/SKILL.md` の同期が必要。
  - main とのテキストの衝突は無い。merge した状態でのビルドは未確認。
- **Workbench 経由で planned-w2 のまま残したコマンド**
  - doctor, uix, item, tool, capture。capture を対応させるときは、#8 も直す。
  - Workbench の機能を ResoLoop へ取り込む方針（2026-09-30）が決まったので、これらを Workbench RPC で対応させるか、取り込み後の設計に回すかは、W4 の前に決め直す。
- **Protocol のキャンセル（#7）**: `feedback/2026-09-27-rpc-client-cancellation.md`（resonite-workbench 側）。未対応。

## 4. 未検証の事項

- RLoop.IntegrationTests の19件は、環境変数が未設定で何もせずに終わっている。成功ではなく未実行。この件は `ResoLoop_Test_*` の Slot を作るので、書き込みの許可を取ってから実行する。
- 次の変更は live で確かめていない。
  - W2-B 修正 R1〜R3 のうち、B1 の型の定義を読めない場合以外の変更。
    - 切断を挟むキャッシュの無効化
    - `value` 欠落の扱い
    - `isGenericParameter`
    - `type describe` で「不明」と「存在しない」を区別した部分
  - `workbench.ps1` の `stop` の照合の失敗経路、`-RequireFreshBuild` で失敗する経路、`connect` の状態の取り直し。
- `hierarchy --include-components` を Workbench 経由で最後まで実行した場合の時間。推定値だけで、実測していない（途中で B1 により失敗するため）。
- `observe`（world state を使う）は、live 比較をしていない。

## 5. 作業体制についての知見

- **Codex**: 改行を含む依頼文を引数で渡すと、Windows では1行目しか届かない。依頼文はファイルに書き、`codex exec ... -o <out> - < prompt.md` で渡す。最初の W2-B レビューはこのせいで対象が欠け、W1/W3 と ps1 は後で見直した。
- **修正のたびに Codex で見直すと、毎回実際の不具合が見つかった**（R1: P1 1件、R2: P1 1件、R3: P1 2件）。ただし、R-1・R-2 は Codex では見つからず、live で見つかった。fake の応答の形が、実際の Workbench とずれていたため。Workbench の wire は、live で記録した応答を fixture にするのがよい。
- **live 比較の前に、Workbench App のビルドが新しいことを確かめる。** 古い App（R1 より前）を起動して、比較をやり直した。`start -RequireFreshBuild` を使う。
- feature-manager は、利用上限などで最終報告を返す前に止まることがある。追跡表（`assignments.md`）と git から状態を復元できた。manager には「最後に必ず報告を返す」と書き、追跡表は他の行を消さずに更新させる。
- Devin は、許可されていないコマンド（ビルドの別の書き方、grep、PowerShell）で止まることがある。指示ファイルに、許可されたコマンドをそのまま書く。`workbench.ps1` の構文解析は manager か Opus が行う。
