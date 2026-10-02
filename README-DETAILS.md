# resoloop 詳細資料

公開向けの概要と導入手順は [README.md](README.md) を参照してください。この文書には、コマンド仕様、宣言的 apply、Flux-SDK、設計、制約などの詳細をまとめています。

resoloopは、CodexやClaude CodeなどのAIエージェントがResoniteを

観測 → 実装 → 適用 → 検証 → 解析 → 修正

のループで操作するための非対話CLIです。ResoniteLinkを単純に露出せず、Slot、Component、member、runtime typeという安定した概念へ写像します。v0.1はproject初期化・診断、接続、Hierarchy/検索/inspection、Slot・Component CRUD、Reflection、再利用可能なJSON apply、scene/test artifact、安全な差分収束、Flux-SDK build/hot deploy、ログtailを実装しています。

## Architecture

~~~text
Agent / Codex Skills
        │
        ▼
   RLoop.Cli ───────► RLoop.Flux ─────► flux-sdk / FluxSDK.Core
        │
        ▼
   RLoop.Core (models, workflows, IResoniteClient)
        │
        ▼
   RLoop.ResoniteLink (0.13.1 adapter)
        │
        ▼
   ResoniteLink WebSocket ─────────────► Resonite
~~~

ResoniteLink依存はAdapterに隔離されています。上位層は公式ライブラリの生JSONや型へ依存しません。Flux-SDKのコンパイラは再実装せず、CLIと公式programmatic APIを利用します。

## Requirements

- Windows 10/11
- .NET SDK 10
- Resoniteの対象worldでResoniteLinkを有効化
- ProtoFlux利用時はFlux-SDK 1.9推奨
- FluxのReflection用にResonite managed DLL directory

Resonite本体のデコンパイルはビルド・実行の必須依存ではありません。

## Installation

~~~powershell
dotnet tool install --global ResoLoop --version 0.1.0-preview.14
resoloop --version
~~~

Preview版の更新:

~~~powershell
dotnet tool update --global ResoLoop --version 0.1.0-preview.14
~~~

release自動化とnuget.org Trusted Publishingの設定は[docs/RELEASING.md](docs/RELEASING.md)を参照してください。

ソースから検証・package作成する場合:

~~~powershell
dotnet build ResoLoop.slnx
dotnet test ResoLoop.slnx --no-build
dotnet pack src/RLoop.Cli/RLoop.Cli.csproj -c Release -o artifacts
dotnet tool update --global --add-source .\artifacts ResoLoop --version 0.1.0-preview.14
~~~

開発中は次でも実行できます。

~~~powershell
dotnet run --project src/RLoop.Cli -- help
~~~

## ResoniteLink configuration

preview.13には[ResoniteLinkセッション探索](docs/DISCOVERY.md)があります。`resoloop discover --json`で候補を取得し、`resoloop status --url auto --json`で一意な候補へ接続できます。複数ある場合は`--session '正確な名前またはID'`で選択します。既定12秒間のUDP通知受信で、ポート総当たりは行いません。通常URLの設定優先順位は維持します。

手動指定する場合はResoniteに表示された現在のportを設定します。portは起動ごとに変わるためハードコードされていません。

~~~powershell
$env:RESONITE_LINK_URL="ws://localhost:12449"
resoloop status --json
~~~

設定優先順位:

1. CLI: --url, --timeout, --command-timeout, --library-path
2. 環境変数: RESONITE_LINK_URL, RESOLOOP_TIMEOUT_SECONDS, RESOLOOP_COMMAND_TIMEOUT_SECONDS, RESOLOOP_FLUX_EXECUTABLE, RESONITE_MANAGED_DATA_PATH
3. カレントディレクトリから親方向で最初の .resoloop.json
4. %USERPROFILE%\.resoloop\config.json

URLがなければRESONITE_LINK_URL_MISSINGを返します。設定例は[examples/resoloop.example.json](examples/resoloop.example.json)です。

## Workbench backend

`--backend` で接続先のtransportを選択します。既定の `link` は従来どおりResoniteLink WebSocketへ接続します。`workbench` はResonite Workbench Appが開くnamed pipe経由のRPCで、URL探索は行いません。

| 設定 | CLI | 環境変数 | 既定値 |
|---|---|---|---|
| backend | `--backend link\|workbench` | `RESOLOOP_BACKEND` | `link` |
| workbench pipe | `--workbench-pipe NAME` | `RESOLOOP_WORKBENCH_PIPE` | `ResoniteWorkbench.Rpc.v1` |

`resoloop wb status [--json]` は `--backend` の値に関係なく常にWorkbenchへ接続し、handshakeとsession状態を返します。

~~~powershell
resoloop wb status --json
~~~

~~~json
{"ok":true,"data":{"pipe":"ResoniteWorkbench.Rpc.v1","handshake":{"serverVersion":"1.0.0","selectedProtocol":1,"availableCapabilities":["session.read"],"grantedCapabilities":["session.read"]},"session":{"connected":true,"resoniteVersion":"2025.9.2.1234","resoniteLinkVersion":"0.13.1","uniqueSessionId":"..."}}}
~~~

`session.connected` が `false` の場合、Workbench自体は応答していますがResonite sessionへ未接続です。

`--backend workbench` 時のコマンド対応（正本は `src/RLoop.Cli/BackendSupport.cs` の `WorkbenchSupport`）。Workbench backendは凍結中で、未対応コマンドを追加する予定はありません。通常は直結（`--backend link`、既定）を使ってください。将来のリリースで除去予定です:

| 対応 | コマンド |
|---|---|
| supported | status, ping, hierarchy, find, observe, inspect, type, validate |
| planned-w2 | doctor, uix, item, tool, capture |
| planned-w4 | diff, plan, slot, component, apply, test, flux |
| link-only | scene, blender, logs |
| unsupported | hierarchy profile, hierarchy query, snapshot create（`snapshot diff` は接続不要のため対象外） |

supported以外の表内コマンドを `--backend workbench` で実行すると `BACKEND_UNSUPPORTED` になります。`validate`（非strict）は接続を要しません。`validate --strict` と `type check --manifest` はmember値の変換検証をWorkbench経路では実行できないため、`BACKEND_UNSUPPORTED` で明示的に失敗します。表にないコマンド（help, init, schema, manifest, uix recipe, skills, discover など）は接続を開かないためbackendの影響を受けません。`wb` は表に含めず、常にWorkbenchへ接続します。

planned-w2の延期理由: `doctor` はlink URLの探索に依存します。`tool audit` はtransformが未報告のとき単位値へ黙ってフォールバックし、`uix audit` はmember値がnullのときを「有効」と扱い、`item audit` は深さ64固定、`capture` はComponent/Slotの作成を伴うため、直結経路と同じ結果を保証できません。`diff`/`plan` は書き込み経路の前提ゲートとしてW4で扱います。

### 観測の上限

`world.observe` は深さ0〜32、8,192 Slotまでです。上限は切り詰めずに拒否します。深さ33以上または `--depth -1`（無制限）はWorkbenchを呼ばず、観測が8,192 Slotを超えたときも、どちらも `WORKBENCH_OBSERVE_LIMIT_EXCEEDED`（終了コード7）で失敗します。`--under` で範囲を絞るか `--backend link` を使ってください。

`--depth` の範囲（最大64）と既定値（hierarchy 2、find 8、inspect 1、tool audit 16）は直結経路と同じままです。既定値が32を超えるコマンドは無いため、Workbench経由のときだけ既定を32にする切り替えは入れていません。深さ64固定の内部処理（runtimeRelocatable Slotの再解決、item audit、diff/planの観測）は、Workbench経由ではこの上限で失敗します。

### 直結経路との違い

Workbenchが報告しなかった値は0や単位値で埋めず、null（JSON出力ではキーが省略される）で返します。

- Slotの `position`/`rotation`/`scale`/`isActive`/`isPersistent`/`tag` は未報告のときnullです。slotレベルの `members`（`inspect --members` のslot部分。Parent・PositionなどSlot自身のsync member）はWorkbench経路では常にnull（省略）で得られません。必要なときは `--backend link` を使ってください。
- 要求した深さより下の子（`--depth 0` ではルート以外すべて）と、除外された子は、`isReferenceOnly: true` のスタブ（id・name・parentIdのみ）として返ります。要求した深さより下でWorkbenchが読めなかった子も、子のないスタブとして扱われ、失敗しません。
- 接続中ユーザーのSlot（`User ...` という名前で、Workbenchは除外理由を `UserRoot` と報告）は `world.observe` で常に除外され、要求した深さの範囲内でも `isReferenceOnly: true` のスタブとしてだけ返ります。直結経路では展開されます（live測定: `hierarchy --depth 2` で直結269 Slot、Workbenchは183 Slot展開）が、Workbench経路で含める選択肢はありません。
- 要求した深さの範囲内で、Workbenchが読み取れなかった子Slot（`unexpanded` の reason が `ReadFailed`）があると、hierarchy / find / observe / inspect などSlotを観測するコマンドは、不完全な結果を返さず `WORKBENCH_UNAVAILABLE`（終了コード4、接続失敗と同じ）で失敗します。原因は接続断ではなく一部Slotの読み取り失敗です。コマンドを再試行するか `--backend link` を使ってください。
- WorkbenchがComponent型の定義を読めないことがあります（`reflection.component` が `Unknown` を返す。測定例: `[FrooxEngine]FrooxEngine.GradientStripTexture`、unknownReasonは "ResoniteLink failed to read the definition of component type ...: Object reference not set to an instance of an object."）。member値は型定義のmember名一覧を起点に読むため、このようなComponentに当たると、member値を読むコマンド（`inspect --members`、`hierarchy --include-components` などすべて）は部分的な結果を返さず全体が `WORKBENCH_UNAVAILABLE`（終了コード4）で失敗します。messageにはComponent型名とWorkbenchのunknownReasonが入ります。接続の問題ではなく再試行しても改善しないため、そのsubtreeには `--backend link` を使うか、対象Componentを含むSlotでは `--members` / `--include-components` を避けてください。同じ型への `type describe` も `WORKBENCH_UNAVAILABLE`（終了コード4）で失敗し、messageに型名とunknownReasonが入ります。`type describe`（`--member` なし）は `reflection.component` の「不明」に対して `reflection.type` で1回裏取りし、型は存在するがComponentではない（enumなど）と分かったときだけ `COMPONENT_TYPE_NOT_FOUND` を返して `DescribeTypeAsync` にフォールバックして成功します（非Component型も従来どおり describe できます）。裏取りの `reflection.type` も「不明」、またはComponentなのに定義を読めないときは `WORKBENCH_UNAVAILABLE` のままです（Workbenchのwireには「型が存在しない」を明示する形がなく、Unknownは不在の証明ではありません）。完全名の打ち間違え（存在しない型）も同じ応答になるため `WORKBENCH_UNAVAILABLE` になり得ます。その場合は `type search` で正しい名前を確認してください。`Unknown` 応答のmetadataにはconnection idが無いため、「読み取り中の接続切り替わり」ではなく型定義の読み取り失敗として報告されます。
- Componentのmember値: `value` はwireのJSONです。通常のfieldの `type` はComponent型のmember定義（`reflection.component` のvalueType）から埋め、assembly接頭辞を除くため `[mscorlib]System.Single`→`System.Single` のように直結経路のCLR完全名と同じ表記です。enumのfieldはenum型名のままです。`type` がnull（省略）のままなのは、generic・nullable値型、定義と一致しないmember、sync object内のnested memberとlist要素、field以外のmemberです（referenceは `targetType`、opaqueは変更なし）。配列・辞書のmemberは `kind: "opaque"`（値なし）、playbackも値なしです。
- `type describe` の非Component型: `isWorldElement` と `genericParameters` はWorkbenchが報告しません（R5、保留中）。`isComponent`/`isSyncObject` はtrue、`isEnum`または値型はfalseと決められる型だけが答えられ、それ以外（普通のclass、閉じたgenericなど）は `BACKEND_UNSUPPORTED` です。open genericのパラメータ名は取れます。
- `type search` が返す型名は `[Assembly]Namespace.Name` の形です。`Slider` などの短い名前は、`reflection.search`（500件の窓）で一意に解決できるときだけ受け付けます。`COMPONENT_TYPE_NOT_FOUND` が返るのは2条件だけです: 短い名前が `reflection.search` で一意に解決できない（該当なし・曖昧・切り詰め。非Component型の describe では `TYPE_NOT_FOUND`）とき、または型は存在するがComponentではないと `reflection.type` で判明したとき（`type describe` は `DescribeTypeAsync` にフォールバックして成功します）。`reflection.component` / `reflection.type` の「不明」（`value` がnullの応答）自体は `WORKBENCH_UNAVAILABLE` です。

### 往復回数

1回の論理読み取りが複数のRPC往復になります。`hierarchy --include-components`、`inspect --members`、`inspect --component/--member` は掛け算になるため、memberの多いComponentや大きなsubtreeでは遅くなります。

- `GetSlot` = `world.observe` 1回。`includeComponentData` のときは、さらにComponentの型ごとに `reflection.component` 1回（client内でcache）＋ memberごとに `member.read` 1回。
- `GetComponent` = 型が不明なとき `member.read` 1回（型特定のprobe）＋ `reflection.component` 1回（型ごとにcache）＋ member数分の `member.read`。直前の `GetSlot` で型が既知ならmember数分だけです。`member.read` は1回ごとにComponent全体をResoniteLinkから読みます。
- `type search` = 1回、`type describe`（Component型）= 1〜2回、（非Component型）= 1〜4回。`reflection.component` が「不明」のときは裏取りの `reflection.type` がさらに1往復増えます。

Workbench関連のエラーコード:

- `WORKBENCH_UNAVAILABLE`: pipeへ接続できない、またはhandshakeを完了できない（Workbench未起動など）。読み取り中の接続切り替わり、`stale` 応答、memberの読み取り失敗、型定義の読み取り失敗（`reflection.component` / `reflection.type` の `Unknown`。`type describe` では `reflection.component` の「不明」を `reflection.type` で1回裏取りし、Componentまたは不明ならこのコード。messageに型名とunknownReasonを含む）、要求した深さの範囲内のSlot読み取り失敗（`ReadFailed`）、不正な応答形式も同じコードです
- `WORKBENCH_PROTOCOL_INCOMPATIBLE`: Workbenchがprotocol versionを拒否。ResoLoopまたはWorkbench Appの更新が必要
- `WORKBENCH_NOT_CONNECTED`: Workbenchは応答したがResonite sessionへ未接続。`wb status` では `connected: false` として正常出力されます
- `WORKBENCH_OBSERVE_LIMIT_EXCEEDED`: 観測上限（深さ32・8,192 Slot）を超えた。`--under` で絞るか `--backend link` を使用
- `BACKEND_UNSUPPORTED`: `--backend workbench` で未対応の接続コマンドを実行した
- `SLOT_NOT_FOUND` / `COMPONENT_NOT_FOUND`: Workbenchの「Unknown」（不在の証明ではない）のときにも同じコードで返ります。`COMPONENT_TYPE_NOT_FOUND` / `TYPE_NOT_FOUND` は「Unknown」のときには返らず（その場合は `WORKBENCH_UNAVAILABLE`）、`COMPONENT_TYPE_NOT_FOUND` は短い型名が `reflection.search` で一意に解決できなかったとき（該当なし・曖昧・500件の窓で切り詰め）と、型は存在するがComponentではないとき（`type describe` は `DescribeTypeAsync` にフォールバック）に返ります。`TYPE_NOT_FOUND` は短い名前の解決失敗時だけです

## Quick start

自分のプロジェクトをゼロから開始する手順は[docs/QUICKSTART.md](docs/QUICKSTART.md)にまとめています。今後の実装順は[docs/ROADMAP.md](docs/ROADMAP.md)を参照してください。

~~~powershell
resoloop init MyResoniteProject
Set-Location MyResoniteProject
resoloop skills sync --check
$env:RESONITE_LINK_URL="ws://localhost:<current-port>"
resoloop doctor
resoloop validate content/main.json --json
resoloop plan content/main.json --json
resoloop apply content/main.json --json
~~~

AIエージェントからは --json を標準にしてください。

~~~powershell
resoloop status --json
resoloop hierarchy profile --json
resoloop hierarchy query --component UIX.Image --select slot.path,component.type --limit 50 --json
resoloop hierarchy --depth 2 --json
resoloop find --name Cube --json
resoloop inspect Root/MyObject --members --json
resoloop find --name Status --under Root/ResoLoop_Test_Game --direct-children --json
resoloop inspect Root/ResoLoop_Test_Game --component Slider --member SnapPositions --depth 4 --json
resoloop type search Grabbable --json
resoloop type describe FrooxEngine.Grabbable --json
resoloop type describe FrooxEngine.StaticTexture2D --member PreferredProfile --json

$slot = (resoloop slot create --parent Root --name ResoLoop_Test --position 0,1.5,2 --json | ConvertFrom-Json).data.id
$component = (resoloop component add $slot FrooxEngine.Grabbable --set Scalable=true --json | ConvertFrom-Json).data.id
resoloop component set $component Scalable false --json
resoloop inspect $slot --members --json
resoloop slot delete $slot --yes --json
~~~

最初の観測には`resoloop hierarchy profile --json`を使うと、Slot一覧を受け取らずに総数・深さ分布・最大sibling数・Component型の内訳が分かります。対象を絞る段階では`resoloop hierarchy query`が`--name` / `--name-regex` / `--component` / `--member` / `--reference-to` / `--direct-children`でCore側でfilterし、`--select`で指定したfieldだけを返します。`--limit`は返す件数、`--max-slots`は走査量で、別々のbudgetです。

結果は必ず`complete`を確認してください。`false`のとき`truncation.reason`は`max-slots` / `depth-boundary` / `user-root-excluded` / `reference-only-boundary` / `result-limit`のいずれかです。`reference-only-boundary`は、transportがcomponent一覧も子も返さなかったreference-only Slotがあり、その子の有無（と、そこにUserRootがあるか）が不明な状態で、`truncation.referenceOnlySlots`と`truncation.referenceOnlyPaths`に列挙されます。`--exclude-user-roots`のときもreference-only Slotの子はUserRootかどうか判定できないため、このフィールドに残ります。`depth-boundary`は深さ上限にSlotが接しており、子の不在を証明できない状態を意味します。`result-limit`の続きは返却された`--cursor`で取得します。cursorはqueryに束縛され、cursor位置より前の行の内容（Slot id・path・投影した値の要約）も再検証します。`UniqueSessionId`は接続ごとの連番で接続の同一性を証明できず、`ConnectionGeneration`もプロセス内でしか通用しないため、接続の一致はcursorの根拠にしません。条件を変えると`CURSOR_QUERY_MISMATCH`、cursor位置より前の行が変わると`CURSOR_STALE`で拒否されます。ただし保証の範囲は前半の変化の検出だけです。cursor位置より後ろの行は検証できないため、前半が一致する別の世界へ接続してもエラーにならず、その世界の続きが返ります（別の世界の続きを検出する保証はありません）。内容が同一なら再接続後も続けられます。cursorの形式を変えたため、旧形式（v1・v2）のcursorは`CURSOR_INVALID`になります（再発行するには`--cursor`なしで再実行してください）。以前の`CURSOR_CONNECTION_MISMATCH`は廃止されており、接続の違いだけで拒否されることはありません。観測は既定でUserRoot配下を含みます。`--exclude-user-roots`（profile / query / snapshot create）を明示したときだけ`UserRoot` Componentを持つSlotを除外し、`truncation.reason: user-root-excluded`、`truncation.excludedUserRoots`、`truncation.excludedPaths`で除外範囲を返し`complete`は`false`になります。除外は不在ではなく未観測として扱われ、snapshot diffでも削除と判定されません。

更新前後の比較は巨大なJSONを読み比べるのではなく`resoloop snapshot create --under SLOT --output before.json`、変更、`after.json`作成、`resoloop snapshot diff before.json after.json`の順で行います。snapshot diffは接続不要です。snapshotは安定pathをkeyにした正規化IRを保存するため、property順や浮動小数の表記揺れは差分になりません。差分は新しい側が親を実際に観測した範囲でのみ削除を報告し、証明できない場合は`SNAPSHOT_PARTIAL`などのwarningを返します。Component一覧やmemberについても同様で、reference-onlyのSlot（子の有無も不明なので、その子は削除と判定されません）、member dataが返らなかったSlot/Component、読み取れなかったmember、member scopeが異なる（`selected`で`--member`の一覧が違う場合を含む）ため比較できない範囲、scopeの違いで片側に記録されていないmemberは値の変化・追加・削除として報告せず、`unobserved`（`path` / `componentType` / `member` / `reason`）と`unobservedCount`に列挙し、`complete`は`false`、`SNAPSHOT_UNOBSERVED`のwarningが付きます。snapshotに記録する`connectionId`は情報用で、同一接続の証明には使いません。2つのsnapshotが同じworld sessionのものだと証明する手段は無いため、Slot・Component・参照先のraw IDの一致・不一致も同一性の根拠にしません。Slotはpath、Componentは型と順序、参照はsnapshot内で参照先が置かれた位置（path）で比較します（このためcomponent idの違いによる`component.replaced`は出力しません）。snapshot外を指す参照は比較できず、`unobserved`に`reference-identity-unproven`として入り`complete`は`false`です。常に情報として`SNAPSHOT_IDENTITY_UNPROVEN`（severity `info`）を返します。参照がnullになった場合は`member.removed`ではなく`reference.changed`で、nullも値として記録します。互換性の変更として、旧`SNAPSHOT_CONNECTION_CHANGED`は廃止し、`connectionId`の違いでは警告を出しません。Component・memberの観測済みフラグ（`componentsObserved` / `membersObserved`）を持たない旧snapshotは、旧版の`childrenObserved: true`も子の観測を保証しないため全観測フラグを未観測へ正規化し、`SNAPSHOT_LEGACY_OBSERVATION_FLAGS`の警告を出して不完全（`complete: false`）として扱い、その不在から削除などを判定しません。旧snapshotと新snapshotのdiffでも同様で、作り直すと解消します。

member payloadを省いた小さな観測には`resoloop hierarchy --under Root --depth 1 --include-components --summary --json`も使えます。必要なsubtreeが分かれば`--under`をそのSlotへ絞ります。宣言したSlotのfieldをnative driverへ接続する場合は`$slot-member:crystal.Rotation`、Componentのfieldには`$member:componentKey.MemberName`を使い、接続前にdriverの型を確認します。[宣言形式](docs/DECLARATIVE.md)と[鍛冶屋テストの改善](docs/BLACKSMITH-FEEDBACK.md)を参照してください。

Reflectionでmemberがlistだと確認できた場合、対応済みのfield/reference要素はJSON arrayで設定できます。たとえばMeshRendererへmaterial providerを割り当てる場合:

~~~powershell
resoloop component set $renderer Materials ('["' + $materialProviderId + '"]') --json
~~~

成功出力:

~~~json
{"ok":true,"data":{"id":"Reso_...","updated":true}}
~~~

自己修正可能な失敗出力（stderr、non-zero exit code）:

~~~json
{"ok":false,"error":{"code":"COMPONENT_TYPE_NOT_FOUND","message":"Component type 'Grabbabl' was not found.","context":{"query":"Grabbabl"},"suggestions":["[FrooxEngine]FrooxEngine.Grabbable"]}}
~~~

Exit codeは、2=引数、3=設定、4=接続、5=not found、6=validation、7=操作、8=timeout、9=外部ツール失敗です。

## Command reference

| Area | Commands |
|---|---|
| Project | init, doctor |
| Connection | status, ping |
| Observe | hierarchy profile, hierarchy query, hierarchy, find, inspect |
| Compare | snapshot create, snapshot diff |
| Slot | slot create, slot set, slot delete --yes |
| Component | component list/inspect/add/set/remove |
| Reflection | type search, type describe |
| Declarative | validate, plan/diff, apply [--prune --yes], scene summary, capture, test |
| Flux | flux status/check/build/watch/deploy/deploy-manifest |
| Diagnostics | doctor, logs |

全オプションは `resoloop help`、事故に関係する詳細は `resoloop help apply` / `resoloop help diff` で確認できます。hierarchyのdefault depthは2、findは8です。大規模worldでは `find --under` / `--direct-children` と `inspect --component` / `--member` を優先し、depth -1は避けてください。削除は --yes 必須で、Root削除は常に拒否されます。statusの `connectionId` はResoniteLink接続ごとのIDで、安定したworld identityではありません。保存済みIDは接続変更後にpath/keyから再解決されます。

## Declarative apply

[examples/agent-test.json](examples/agent-test.json)を参照してください。

~~~powershell
resoloop diff examples/agent-test.json --brief --report artifacts/plan-01.json --json
resoloop apply examples/agent-test.json --json
~~~

Material、家具、照明、ReflectionMaterial、TouchButtonを含むnestedな実例は[examples/house-world.json](examples/house-world.json)です。

~~~powershell
resoloop apply examples/house-world.json --json
~~~

schema v1では、top-levelに `schemaVersion: "1"`、`ownership.key`、root `slot.key`が必要です。ownershipごとのstateは既定でproject内の `.resoloop/state/<ownership>.json` に保存され、途中経過もcheckpointされます。このdirectoryは `resoloop init` が生成するignore設定によりversion controlから除外されます。

TSX builder は入口の named export `export const ownership = { key: "house-world" }` を使い、未指定ならroot Slotのkeyをownershipにします。空文字・不正形状はbuildエラーです。既存制作物ではownershipと全Slot/Component keyを保ち、draftの自動keyで置き換えないでください。

新しい再利用部分木は TSX の `<Scope instanceKey="left">` で明示的に囲み、内部の明示 local key `body` を `left::body` に展開します。JSON source では node の `"$scope":"left"` が同じ規則です。nested scope は `outer::inner::local`、instance/local segment は空白のみと `:` を禁止します。兄弟順序は使わず、同名兄弟の禁止は従来どおりです。scope 内の fields/initialFields（配列・object 内も含む）の短い `$slot:` / `$component:` / `$ref:` / `$member:` / `$slot-member:` は現在 scope だけで解決し、親 scope/global に fallback しません。外からは `$member:left::state.Value` や `ref.component(ref.key("left","state"))` の完全 key を使います。完全 key は内部から別 scope を参照する場合にも使えます。`migrateFrom` も同じ scope 規則です。

既存の unscoped key と state の実効 key はそのままです。Scope の追加による自動移行・再採番はせず、展開 key が旧 key と衝突すれば TSX build の `DUPLICATE_KEY` または C# の既存 key 衝突エラーで停止します。新しい segment の不正は `APPLY_SCOPE_INVALID`（終了コード6）。`--draft` の旧 index 由来 key は従来の文字列を出力しますが、source guard `"$draftKeys":true` を付け、validate/diff/plan/apply は `APPLY_DRAFT_KEY_UNSTABLE`（終了コード6）で停止します。意図した実効 key を明示 props に写して再buildしてください。scope 内は draft でも明示 key が必須です。展開後 document schema `"1"`・state schema v3・CLI selector の文字列完全一致規則は変わりません。

生成JSONの任意top-level `authoring` は `{"projectRoot":"C:/projects/house","source":"content/main.tsx","ownershipSource":"entry-export"}` の形です。build時に一度だけ、明示 `--project-root` > 作者TSXの最寄り `.resoloop.json` のdirectory > TSXのdirectory、の順でprojectRootを決めます。projectRootは絶対パス、sourceはproject相対、ownershipSourceは診断情報（未指定exportでは `root-key`）です。生成JSONの出力先・コピー先はstate基準を変えません。project全体を移動したら再buildしてください。新しいmetadataを読むには対応CLIが必要です。

TSX build の受渡しは、呼出し側が毎回 build **前**に作る新規 ID R と単一 bundle を使えます。`resoloop-jsx build content/main.tsx --bundle --catalog CATALOG.json --build-id R -o build/R/bundle.json` の exit 0 を確認してから、`resoloop validate|diff|plan|apply build/R/bundle.json --build-id R` へ渡してください。旧 bundle から ID を読み戻さず、既存 directory/output を再利用しません。通常 build の JSON・exit 0/1/2・BuildError は維持します。

bundle は `kind`、`bundleVersion`、`buildId`、`completion`、`buildStages`、`inputs`、`ir`、`map`、`usedTypes`、`catalog` を持ちます。IR/map/catalog の `text` の UTF-8 bytes hash と、source/catalog の絶対 path・raw bytes hash を記録します。同じ directory の一時ファイルを close・入力再確認後に rename して公開します。TypeScript Program の全 source（宣言・標準 library を含む）と静的 local import が対象で、project 外 source、動的 import/require、Node 組込み、authoring runtime 以外の外部 package は確定できません。文・呼出し先は解析せず、環境変数・時刻等は検出不能で保証対象外です。map は version/buildId/IR hash/sources/entries を持ち、追跡できる元 AST の位置は known range、追跡できない位置は明示 `unknown` です。位置が unknown だけの旧 map も受け付けます。

CLI は拡張子ではなく内容で識別し、bundle 経路から通常 JSON へ fallback しません。要求 R、確定 stage、全 payload、map/source 整合、共有 IR/catalog 検証、使用型完全名の集合を接続前に検査します。保持した入力 snapshot を準備後と最初の実機 mutation 直前に再確認し、変更・削除・読取り不能なら書込み 0 で停止します。内包 catalog 原本だけを使い、外部 `--catalog` の併用は拒否します。接続ありの diff/plan/apply と validate --strict は synthetic を拒否し、既存 strict と同じ session/client version 照合をします。未確認 catalog は既存 `APPLY_CATALOG_UNAVAILABLE`、bundle 不正は `APPLY_BUILD_BUNDLE_INVALID`（exit 6、`context.reason`: requestMismatch/uncommitted/mixed/inputChanged/inputUnknown）。bundle に ID がない／通常 JSON に ID がある場合は既存引数エラー（exit 2）、Workbench は `BACKEND_UNSUPPORTED`。手書き／従来生成 JSON の直接経路・validate JSON・schema `"1"`・state のパス解決は維持し、警告は増やしません。内包 authoring の project/state 基準と `--state`/`--require-state` も従来どおりです。

保証は bundle 制作手順に限ります。R と確定印は署名ではなく producer との契約で、手動の ID 再利用や偽造を検出できません。最終確認後の競合・書込み開始後の変更、precondition/lock/応答喪失は S3 です。`--diagnostics NEW_FILE.json` は元 TSX の位置と詳細な証拠を別 JSON に出します。位置を追跡できない場合は unknown、catalog の成功だけでは runtime は未検証です。実 catalog と live は別途検証が必要です。[完全な手順・形式](tools/resoloop-jsx/README.md)。

Coreはcontextを検証して保持し、stateパスをwriter lock・Prepare・結果・captureのselectorで共有します。authoring不正時は生成JSONの場所へfallbackしません。authoringのない手書きJSONはJSONのdirectoryから最寄りconfigを探し、無ければそのdirectoryを使う従来経路（Node不要）です。明示 `--state` は常に最優先でcwd相対です。authoring付きdocumentのlocal asset検証・取り込み、mesh bounds、camera bookmarkの相対出力は作者sourceのdirectory基準です。明示出力はcwd基準、手書きJSONの相対資源は従来基準、JSON includeは各JSON基準、Flux各パスはmanifest基準、接続設定の探索はcwd起点を維持します。

既存制作物の `diff/plan/apply --require-state` は、選んだstateが無ければ `APPLY_STATE_NOT_FOUND`（終了コード5）で停止し、stateを作りません。指定なしの初回作成は従来どおりです。state v1/v2読込・v3保存、flat key、filenameのSanitize（禁止文字と空白を `_` に置換し端の `_` を除去）、ownershipKeyの完全一致検証は変わりません。Sanitizeで同じfilenameになってもownership不一致は拒否します。ownershipやsessionIdの一致だけでlive IDを再利用せず、現在worldの所有証拠を引き続き検証します。

JSON→TSX移行（現在JSXで表せるSlot/Component subsetのみ）: ①旧diffのstateパス・ownership・全keyを記録しstateをbackup、②同じ値でTSXを作成し、旧JSONのkey省略箇所はstateの実効keyを明示（Component既定keyは `slotKey/component:normalizedType:ordinal`）、③build後に `resoloop diff OUTPUT.json --state OLD_STATE.json --require-state --json` で差分0を確認、④ `resoloop apply OUTPUT.json --state OLD_STATE.json --require-state --json` 後にIDと再diffを確認します。既定パスが異なる場合はbuilderの `--project-root` で旧基準を固定するか、検証済みcheckpointを明示的にコピーします。stateの自動探索・移動やownership書換えは行いません。

`children` でSlot階層を宣言できます。SlotとComponentの明示的 `key` はrename、親変更、セッション変更後の再解決に使われます。stable Slotの親変更はIDを維持する`relocate`、stable Componentの親Slot変更は作成・参照再解決・旧Component削除としてplan/applyされます。同じSlotに同型Componentを複数宣言する場合は、それぞれにkeyが必要です。`identityFields`へ不変な管理memberを指定でき、managed reference topologyもstateへ保存されるため、同型Componentの挿入後も再接続時に誤接続せず再解決できます。`fields`は毎回収束させる値、`initialFields`はComponent新規作成時だけ設定してruntime dataを上書きしない値です。`managedFields`はresoloopが収束させるposition/rotation/scaleを限定し、`preserveWorldTransform`は既存Slotの現在のlocal transform値を保持します。親変更時は`relocationTransform: "local"`が既定で、`"world"`なら旧world transformから新しいlocal transformを計算して以後保持します。装備などでruntime親が変わるitem rootには管理Component証拠と`runtimeRelocatable: true`を宣言できます。保存path消失時は一意な証拠でのみ再解決し、移動中のplan/applyはmutation前に停止します。planのrelocate理由にも選択したpolicyが表示されます。key変更時は`migrateFrom`でworld objectを作り直さずstateを移行できます。fieldから `$slot:key`、`$component:key`、`$member:key.MemberName`、`$asset:key` を参照でき、forward referenceも利用できます。旧 `$ref:key` も互換です。vector、quaternion、colorはJSON array/objectがcanonicalで、従来のcomma stringも互換入力として受理されます。

`inspect`、`slot`、`component`、`item audit`でもstable selectorを使用できます。例: `resoloop component inspect '$component:controller' --state .resoloop/state/item.json --json`。raw IDは接続単位、stable selectorはstateのpath、型、identity、reference topologyから現在のIDへ再解決されます。

preview.10後のsourceではcheckpoint schema 2にSlot名の配列を保存し、`/`・`\`・前後空白を含む名前を保持します。厳密指定の例はPowerShell `resoloop slot inspect 'path:["Root","A/B"," Label "]' --json`です。同名兄弟は初回validationで拒否します。旧stateは次回保存時に移行しますが、旧版CLIはschema 2を読み込めません。宣言schemaは`"1"`のままです。UIXの参照切り替え、checkpoint競合、画像の市松模様の調査は[test14改善記録](docs/UIX-TEST14-FEEDBACK.md)を参照してください。

include、parameter/variable、prototype/instance、repeat、asset、camera、assertionの仕様は[docs/DECLARATIVE.md](docs/DECLARATIVE.md)にまとめています。house fixtureは3ファイルへ分割し、22個のboxと4本のtable legをprototype化しました。展開結果69 Slot・148 Componentを維持したまま、宣言量は46,178 byteから42,430 byteへ8.1%減っています。

`validate` は接続なしのschema・値形状・key・参照検査、`validate --strict` は接続先のruntime Reflectionを使ったComponent/member検査です。`plan` はworldを変更せずcreate/update/no-opを列挙します。既存rootを初めて管理対象へ取り込む場合、inspectとplanで完全一致対象を確認してから一度だけ `--adopt` を付けます。stateがある通常の再適用では不要です。

~~~powershell
resoloop plan content/main.json --json
resoloop diff content/main.json --changes-only --json
resoloop diff content/main.json --deletes-only --json
resoloop apply content/main.json --profile --json
resoloop diff content/main.json --json
resoloop scene summary content/main.json --output artifacts/scene.json --json
resoloop capture content/main.json --camera main --output artifacts/main.svg --json
resoloop capture content/main.json --camera main --output artifacts/main.jpg --json
resoloop test content/main.json --json
~~~

applyの最終JSONはstdout、進捗はstderrへ分離されます。plan/diffのJSONは常に全変更を `changes` 配列へ分離し、`--changes-only` / `--creates-only` / `--deletes-only` / `--summary` は `operations` の表示量だけを絞ります。`--prune --yes`はstaleな親Slot配下を1回の親削除へ集約します。機械処理できる進捗が必要なら `--ndjson-progress`、表示を抑えるなら `--quiet` を使います。`--timeout` は各ResoniteLink request、`--command-timeout` はcommand全体のdeadlineです。Ctrl+Cやdeadlineで中断した場合はstate fileと完了件数が報告され、同じapplyで再開できます。

## Blender modeling

`resoloop blender find`、`blender run SCRIPT.py`、`blender export FILE.blend`で、背景Pythonによるモデル制作からResoniteへのimportまで進められます。BlenderはPATH登録なしでも検出でき、事前に起動しておく必要はありません。同梱の`resonite-blender`スキルは造形、VR向け資源設計、静的meshのUV・法線・texture・materialのimportを扱います。未インストールの場合はユーザーの許可を確認し、CLIが勝手にインストールすることはありません。

exportはレビュー可能なapply bundleを新しい出力directoryへ生成します。この時点ではrenderやworldの変更は行いません。

~~~powershell
resoloop blender find --json
resoloop blender run modeling/model.py --arg=artifacts/prop.blend --json
resoloop blender export artifacts/prop.blend --output content/prop-v1 --name Prop --parent VERIFIED_PARENT --json
# 独立した部品・pivotと、対応する直接接続PBR data画像を保持する場合
resoloop blender export artifacts/prop.blend --output content/prop-v2 --name Prop --parent VERIFIED_PARENT --preserve-hierarchy --pack-pbr --json
resoloop validate content/prop-v1/model.apply.json --strict --json
resoloop diff content/prop-v1/model.apply.json --state .resoloop/state/prop.json --json
resoloop apply content/prop-v1/model.apply.json --state .resoloop/state/prop.json --json
~~~

`modeling/model.py`は自分のprojectの制作script、`VERIFIED_PARENT`は現在のworldで確認した親Slotに置き換えます。`--preserve-hierarchy`は部品の階層とpivotを保持し、`--pack-pbr`は対応する直接接続のPBR data画像をResonite向けにまとめます。検出pathの上書き、texture packing、対応shader、テスト手順と制約は[Blender workflow](docs/BLENDER.md)を参照してください。

UVを持つn-gonと、生成・編集された画像bufferの現在のpixelに対応しています。textureのcolor profileは`resoloop type describe FrooxEngine.StaticTexture2D --member PreferredProfile --json`で実行環境のenumを確認します。item auditが返す外部参照候補と未使用allow指定も確認してください。[制作テストの改善と検証](docs/BLENDER-FEEDBACK.md)に記録があります。

新しいexportはproviderを名前付きSlotへ分け、中断したapplyからの復旧に備えます。既存のroot直下provider配置を保つには`--legacy-root-providers`を使用できます。strict validation、nested SyncObjectの差分、Slot fieldの観測、geometry／partial／pivotのbounds区別は後述の観測・検証手順に従います。[時計塔・戦車テストの改善](docs/CLOCKTOWER-FEEDBACK.md)も参照してください。

制作では要求する見た目を先に満たし、不要な重複などを省いて資源量を調整します。モデル単体の合否にセッション全体のFPSを使わず、モデルに帰属するgeometry、material、画像などを評価します。制作のためにBlenderで最終renderを実行することは必須ではありません。

## UIX authoring

同梱の`resonite-uix`スキルは共有Assets、Layout metrics、fitting、scrolling、入力状態、階層移行を扱います。既存UIX Slotは移動先のComponentを準備してからrelocateし、新しいComponent keyはprune予定のIDを再利用しません。スキルのreference配布と利用者編集の保護は後述のCodex Skillsを参照してください。

~~~powershell
resoloop uix audit '$slot:panel' --state .resoloop/state/panel.json --depth 6 --max-slots 128 --json
resoloop validate examples/uix-responsive.json --strict --json
~~~

UIX auditはread-onlyの部分的な構造観測です。計算後のsizeはunknownのままで、texture/material設定やButton color driverが確認できても画像の読込・描画成功は保証しません。`--strict`はwarningも失敗として扱います。[responsive example](examples/uix-responsive.json)は共有font、等幅card、複数fieldを一時変更して復元するprobeを示します。`set-members`と明示的な展開上限は[宣言形式](docs/DECLARATIVE.md)、検証済みの挙動と残る課題は[UIX feedback](docs/UIX-FEEDBACK.md)を参照してください。

schema 2のexact Slot名保持、同名兄弟・不正assetの事前検証、ライフサイクル変更後の参照再検証、checkpointの単一writerとreaderの共存、市松模様を圧縮設定だけに帰因させない調査結果は[test14改善記録](docs/UIX-TEST14-FEEDBACK.md)にまとめています。

### 構造レシピと要約出力

`uix recipe list`、`uix recipe describe NAME`、`uix recipe export NAME --output NEW_FILE.json`は接続不要です。button・boolean-state・scroll-content・value-state・text-input・toggle・choice・sliderを通常のprototypeとして配布します。色、形、文字、寸法、押下表現を固定せず、引数と接続口で利用側の任意の構成と接続します。choiceのselected出力からタブ、bool状態から開閉パネルも構成できます。[接続例と制約](skills/codex/resonite-uix/references/recipes.md)、[検証雛形](skills/codex/resonite-uix/references/control-verification.md)を参照してください。dropdownのフォーカス・外側クリック・キーボード操作は追加の実装が必要です。

`--brief`を付けたdiff/planは変更対象・理由を一度だけ出力し、`--summary --brief`なら件数のみになります。UIX auditはSlot観測の全件列挙を省き、問題・打切り・partial状態を保持します。testは失敗または構造確認のみのケースを表示します。applyの進捗は抑制しますが、`--ndjson-progress`の明示指定は維持します。未対応の結果型は従来のdataを返します。

`--report NEW_FILE.json`は省略前の成功/エラーJSONを保存します。既存ファイルを拒否し、接続/変更前に出力先を確保します。brief/report利用時は`--json`なしでもJSON出力になります。通常のJSON形式は変更しません。既存の`--summary`単独では従来どおりchanges配列が残ります。

diff/planはofflineとruntime Reflectionの検証を内部で実行します。通常の変更ループでvalidateの両モードを先に重ねる必要はありません。単独validateはオフライン作業や診断用に維持し、apply直前の再検証も維持します。要約表示によって検証・観測範囲や削除の`--yes`要件は変わりません。

## Flux-SDK

~~~powershell
dotnet tool install --global Papaltine.FluxSDK --version 1.9.0
$env:RESONITE_MANAGED_DATA_PATH="D:\Users\star_\AppData\Local\RESO Launcher\profiles\profile1\Game"

resoloop flux check examples/flux/ResoLoopHello.pg --project examples/flux --json
resoloop flux node search DynamicImpulse --json
resoloop flux node describe DynamicImpulseTrigger --json
resoloop flux build examples/flux/ResoLoopHello.pg --project examples/flux --json
resoloop flux deploy --project examples/flux --module ResoLoopHello --parent ResoLoop_Test --json
resoloop flux validate-manifest examples/flux/resoloop.flux.json --json
resoloop flux deploy-manifest examples/flux/resoloop.flux.json --json
resoloop flux watch examples/flux/resoloop.flux.json --json
~~~

`.pg`に対するbuild/check/watchは既存Flux-SDK CLIをラップします。`flux node search/describe`はFlux-SDKの通常metadata出力を、同名nodeをfull identity別に保持したversioned catalogへcacheします（1.9.0の`froox-docs --json`は同名keyで失敗するため使用しません）。`flux validate-manifest`は接続せずにJSON shape、source、dependency、module portとbindingの一対一対応を検査します。JSON module manifestに対するwatchは依存順に成功buildだけを再deployします。`$slot:key` parentはworld stateから現在session向けに検証・再解決されます。moduleの `bindings` ではFlux input名を `mode: "source"`、output名を `mode: "drive"` として `$slot:key` / `$component:key` / `$member:key.MemberName` へ接続できます。driveはmember targetだけを受け付けます。明示したCLI `--state` はcurrent directory基準、manifest内の`worldState`、`source`、`deployState`はmanifest基準です。Fluxの`int`/`int32`/`System.Int32`、`bool`/`System.Boolean`などのscalar aliasは同じ型として照合します。Component IDは所有Slotを検証してから、そのSlot上の型・管理member名・記録済み`identityFields`値・管理参照を証拠に再解決します。`componentIndex`は使いません。同じ型・同じmember名などの候補が複数残ると`STABLE_COMPONENT_AMBIGUOUS`で停止します。保存ID集合が完全に一致しても同一性の証拠にはなりません。同型を同じSlotに置く場合は、作成時に不変の`identityFields`値か一意に絞れる管理参照を記録するか、名前付きprovider Slotに分けてください。source signatureの未結線port、方向・型不一致、Flux-SDK 1.9.xのinterface global inputはbuild/deploy前に、build成功後の0 nodeはdeploy前に構造化エラーになります。deployはFlux-SDK 1.9のLoader.replaceを利用し、deploy先の`parentSlotId`と、再観測した実module childの`moduleSlotIdBefore` / `moduleSlotIdAfter`、解決済みbinding、checkpoint recoveryを返します。`doctor`は最小check/build probeでmanaged-dataの明示pathまたは自動発見が実際に成功するか確認します。

弾、標的、エフェクトなど多数の同型オブジェクトは、各templateへ完全なFlux graphを複製せず、template側を不可避なDriverやadapterだけに留めます。instance stateは`Projectile/Active`のような型付き・名前空間付きDynamicVariableへ置き、上限付きpoolを1つのcontroller moduleから走査、確保、更新、完全reset、再利用します。これによりpool数を増やしてもProtoFlux module数とdeploy costが比例増加しません。[PooledProjectiles.pg](examples/flux/PooledProjectiles.pg)と[manifest](examples/flux/pooled-projectiles.flux.json)が最小例です。

保存・配布するGrabbableは、保存前に参照閉包を監査できます。

`AudioClipPlayer.playback`などのSyncPlayback memberは型情報を保持するため、`in Rain: SyncPlayback element`へ`$member:player.playback`で接続できます。PlaybackノードがIPlayableを要求する場合は、Reflectionで実装を確認したうえで`ObjectCast<SyncPlayback,IPlayable>`を使います。

~~~powershell
resoloop item audit Root/ResoLoop_Test_Teleporter --strict --json
resoloop tool audit '$slot:tool-root' --state .resoloop/state/tool.json --depth 16 --json
~~~

`item audit`はroot以下のSlot、Component、member IDを閉包として収集し、外部参照をrequired world element、Flux external、runtime context、明示許可へ分類します。保存後に切れる参照は`ITEM_NOT_PORTABLE`、runtime contextはwarningです。意図した依存だけを`--allow-external`で許可し、Flux moduleとbinding targetもGrabbable root内へ置いてください。

engine既定shader/font/materialのようにsession IDが変わる意図的な外部依存は、監査結果を確認したうえで`--allow-external-role TextRenderer:Font`のような正確な`Component:MemberPath`で許可できます。これはengine既定を型だけで推測せず、レビュー済みの参照roleをstableに許可する仕組みです。

roleは単純型名、`Namespace.Type`、`[Assembly]Namespace.Type`、正確なComponent IDに対応します。型指定は一致する全Component、ID指定はそのsessionの1個だけが対象です。`externalRoleCandidates`から対象確認済みの完全修飾roleを選び、`unmatchedExternalRoles`／`ITEM_ALLOW_ROLE_UNUSED`で無効な指定を確認してください。未使用指定はwarningとなりstrict監査に失敗します。外部shaderの自動許可は行いません。

enumなどの非Component型はassembly名が必要な場合があります。`type describe COMPONENT --member FIELD --json`はReflectionのvalueTypeを使用し、Nullableを解除して型定義・enum値を返します。例は`FrooxEngine.StaticTexture2D --member PreferredProfile`です。assemblyや数値を推測する必要はありません。

`tool audit`はRawDataTool.TipReference、左右GripPoseReference.HandSide、および各poseのlocal Z+とtip方向の内積を検査します。geometryが合格してもPrimary actionのruntime smoke checkは別途必要です。

## Codex Skills

skills/codexには次のworkflow Skillがあります。

- resonite-build: 観測から編集・Reflection・検証・修正までの統合ループ
- resonite-uix: 共有Assets、Layout、入力・状態保持、UIX階層移行と見た目の検証
- resonite-debug: read-first診断
- resonite-inspect: コンテキストを浪費しない観測
- resonite-blender: Blenderの検出・背景Python制作・VR資源設計・mesh/UV/texture/materialのimport。[制作手順とCLI例](docs/BLENDER.md)
- resonite-flux: ProtoGraph check/build/deploy

`resoloop init` はResoniteコンテンツproject限定のskillsとして、対象projectの `.agents/skills/` へこれらを自動的にインストールします。

~~~powershell
resoloop init .
resoloop skills sync --check
# 更新が必要で、利用者編集との競合がないことを確認後
resoloop skills sync --update
~~~

`resoloop init`はskill内容と配布hashを`.agents/skills/.resoloop-bundled.json`へ記録します。`skills sync --check`はread-onlyで差分を検査し、`--update`は現在内容が前回配布hashと一致する未編集skillだけを更新します。利用者編集またはlockのない未知内容は`SKILL_SYNC_CONFLICT`で全更新前に停止します。Codexはcurrent directoryからrepository rootまでの `.agents/skills/` を読み込むため、このskillsは対象project内でだけ利用されます。CLI commandはprimitive、Skillはworkflowです。

同梱スキルはreferencesもfile単位でhash追跡します。lock schema 2の`files`へ移行し、schema 1の`skills`も読み込めます。referenceを利用者が編集した場合も全件preflightで保護し、欠損fileは復旧します。

## Tests

~~~powershell
dotnet test tests/RLoop.Tests/RLoop.Tests.csproj

$env:RESOLOOP_RUN_INTEGRATION="1"
$env:RESONITE_LINK_URL="ws://localhost:12449"
dotnet test tests/RLoop.IntegrationTests/RLoop.IntegrationTests.csproj --filter Category=Integration
~~~

Integration testは一意なResoLoop_Test_Integration_* Slotだけを作り、finallyでcleanupします。既存ユーザーコンテンツは操作しません。

## Logs and troubleshooting

resoloop logsはResonite内部の非公開APIへ依存せず、設定されたファイルまたはdirectoryの最新.logをtailします。

~~~powershell
$env:RESONITE_LOG_PATH="C:\path\to\Resonite\Logs"
resoloop logs --tail 200 --json
~~~

- APPLY_PROJECT_CONTEXT_INVALID（終了コード6）: authoringの形状・絶対projectRoot・project相対sourceが不正です。生成JSONの場所へのfallbackは行いません。作者projectを確認して再buildしてください
- APPLY_STATE_NOT_FOUND（終了コード5）: `--require-state` またはstable selectorに必要なcheckpointがありません。選んだstateパスとbackupを確認し、既存制作物で空stateを作って再試行しないでください
- APPLY_TARGET_AMBIGUOUS（終了コード6）: 同名候補が複数ある、またはstateに記録された元Slotの移動先を別の同名Slotが占有しています。移動先は採用せず変更前に停止します。所有を確定して移動先の衝突を解消してください
- CONNECTION_FAILED: ResoniteLinkがworldで有効か、画面上のportとURLが同じか確認
- 直結経路のGetSlotで `Success=false` のとき、`SLOT_NOT_FOUND`（終了コード5）は要求IDへのResoniteLinkの明確な不在応答 `Slot with ID '<要求ID>' not found.` と完全一致する場合だけで、それ以外の失敗応答は `RESONITE_OPERATION_FAILED`（終了コード7）になります。
- 型定義とGetComponentの不在も、実際に送った型名への `<型名> is not a valid type`、要求IDへの `Component with ID '<要求ID>' not found.` という明確な不在応答との完全一致（Ordinal、前後空白のみ除去）で判定します。ただし型一覧に載っている型は、完全一致の不在応答でも一覧の存在証拠と矛盾するため NotFound にせず unreadable / unknown にします。未掲載の型定義は完全一致時に `TYPE_NOT_FOUND`、Component型の解決は完全な一覧に一致が無いときに `COMPONENT_TYPE_NOT_FOUND`（終了コード5）となり、その他の定義失敗は unreadable / unknown、GetComponentのその他の失敗は `RESONITE_OPERATION_FAILED`（終了コード7、contextに `componentId` と `errorInfo`）です。
- APPLY_STORED_ID_UNVERIFIED（終了コード6）: 保存Slot IDの不在・所有を検証できない、または検証済み所有Slot上の保存Componentの証拠が不一致・未読のため、変更前に停止しました。Slot IDの読取失敗は`SLOT_NOT_FOUND`だけを不在とし、一般失敗・例外は`reason: storedIdReadFailed`、旧pathの読取失敗は`recordedPathReadFailed`で停止します。Componentの証拠不一致は`componentEvidenceMismatch`、member未読は`componentEvidenceUnread`です。手動改名・移動と別worldでのID衝突を区別しません。`context`の`storedId`、`recordedPath`、`observedName`、`observedPath`、`reason`を確認し、所有Slotだと確認できた場合は記録された名前と親に戻してください。それ以外はcheckpointを保持してstateを明示的に修復・置換します。未検証のIDを採用したり、stateを無条件に捨てて再applyしたりしないでください
- STABLE_COMPONENT_AMBIGUOUS（終了コード6）: 所有Slot上に型・member名・identity値・管理参照で区別できないComponent候補が複数あります。保存ID集合が完全でも、候補の個別の同一性を証明できなければ停止します。`candidateIds`を観測してstateを保持し、所有と各候補を確認してから明示的に復旧してください。既存checkpointへmanifestの`identityFields`を追加するだけでは保存済み証拠は補われません。新規制作では名前付きprovider Slotへ分けるか、作成時に不変の`identityFields`値か一意に絞れる管理参照を記録します
- COMPONENT_TYPE_NOT_FOUND: type searchの完全な結果を使う
- COMPONENT_DEFINITION_UNREADABLE（終了コード7）: 型は型一覧に存在するが、ResoniteLinkがそのComponent型のmember定義を読めない（例: `GradientStripTexture`）。「不在」ではなく「不明」です。`type describe COMPONENT`（`--member` なし）は型情報（TypeInfo）に `membersAvailable: false`、`membersUnavailableCode`、`membersUnavailableReason` を加えて成功し、Componentのmemberを必要とする操作（`component add`/`set`、`apply`、`type describe --member`）はこのコードで拒否されます。`type query`/`type check` は拒否せず、当該型を `status: "unknown"`（`differences` に `TYPE_DEFINITION_UNAVAILABLE`）として報告します。実Componentのmember値は `inspect --members` で読めることがあります
- TYPE_SEARCH_INCOMPLETE（終了コード7）: 型一覧が空または不完全で、型が存在するか判定できない（「不明」）。`COMPONENT_TYPE_NOT_FOUND` にはなりません。world読み込み完了後に再試行してください。`type describe` は型情報を返し `membersAvailable: false` を付けます
- TYPE_SEARCH_INCOMPLETE の補足: 型一覧が空のとき ResoLoop はカテゴリ木を走査して型一覧を作ります。カテゴリ数が5000超、深さが32超、子カテゴリ名が空または `/` `\` を含む、応答に `ComponentTypes`/`SubCategories` が無い、途中の要求が失敗した（SDKが例外を投げた場合を含む。取消、REQUEST_TIMEOUT、接続断はこのコードに包まずそのまま伝播）、のいずれでも部分結果は返さず、このコードで失敗します。空または不完全な一覧は再利用しません。`type search` の一致なしは、一覧が完全なときだけ空の結果です
- COMPONENT_TYPE_AMBIGUOUS（終了コード2）: 短い型名が型一覧の複数の型に一致した。「不在」ではありません。`context.candidates` の候補から完全名 `[Assembly]Namespace.Type` を選んで再実行してください（従来は `COMPONENT_TYPE_NOT_FOUND` として報告されていました）
- TYPE_DEFINITION_UNREADABLE（終了コード7）: ResoniteLinkがその型の定義（TypeDefinition）を読めない。enumなど非Component型がComponent型一覧に無いことも不在の証明にはならず、一般的な読取失敗は`TYPE_NOT_FOUND`ではなくこのコードで失敗します
- CONNECTION_GENERATION_CHANGED / ALREADY_CONNECTED: 接続レベルの失敗です。`type query` / `type check` は型単位の `unknown` にせず、コマンド全体を中断します。古い接続の応答は世代確認とキャッシュ更新を同じロックで行うため、新しい接続のメモリ・diskキャッシュには書かれません
- ALREADY_CONNECTED（終了コード7）: 接続済みのクライアントへ再接続しようとした。1つのクライアントは1つの接続だけを担当します（通常のCLIコマンドでは発生しません）
- CONNECTION_GENERATION_CHANGED（終了コード7）: 型情報の読取り中に接続が入れ替わったため、その応答を破棄しました（キャッシュへ書きません）。現在の接続で再試行してください。`session` の出力には接続ごとの `connectionGeneration` が付くことがあります（保存はしません）
- `type query` / `type check`: 型が読めない場合は全体を止めず、その型を `types[].status: "unknown"`（`differences` に `TYPE_DEFINITION_UNAVAILABLE`）として報告し、他の型は通常どおり返します。型一覧が完全なのに存在しない型だけが `status: "notFound"`（`COMPONENT_TYPE_NOT_FOUND`）です。`verified` は `status: "verified"` のときだけtrueです。member値型（enumなど）の定義だけが読めない場合は、Componentの解決結果は保持したまま、その型を `status: "unknown"`、該当memberを `differences` の `ENUM_VALUE_TYPE_UNAVAILABLE` として報告し、そのmemberのenum期待値は比較しません（`notFound` にはなりません）。接続断・timeout・接続世代の変更はコマンド全体を失敗させます
- open generic: `resoloop type specialize '[FrooxEngine]FrooxEngine.DynamicValueVariable<>' string` でclosed genericを生成する
- COMPONENT_MEMBER_NOT_FOUND: type describeでflattened memberを確認
- VALUE_CONVERSION_FAILED: vector/quaternion/colorはJSON array/objectを優先し、エラーのtarget typeと受理例を確認
- FLUX_SDK_NOT_FOUND: flux-sdkをglobal toolとして導入、またはRESOLOOP_FLUX_EXECUTABLEを設定
- Flux type error: RESONITE_MANAGED_DATA_PATHまたは --library-path を確認

## In-game screenshots

`capture FILE.json --camera BOOKMARK --output capture.jpg` はブックマークのworld座標・注視点・縦画角と解像度を使って撮影します。manifestを自動applyする操作ではないため、必要なコンテンツを先にapplyしてください。Reflectionで `InteractiveCamera.Capture()` を確認し、専用の `ResoLoop_Test_Capture_*` Slotにカメラを生成します。撮影後は失敗時もそのSlotだけを削除します。既存のカメラは変更しません。

~~~powershell
resoloop capture content/main.json --camera main --output artifacts/main.jpg --url ws://localhost:<current-port> --json
resoloop capture content/main.json --camera main --output artifacts/main.png --screenshots-dir 'C:\Users\YOUR_NAME\Pictures\Resonite' --width 1280 --height 720 --capture-timeout 60 --json
~~~

標準の読み取り先は、Resonite本体と同じWindowsのPictures既知フォルダ配下の `Resonite` です。OneDriveリダイレクトがプロセス間で食い違う場合は、OneDrive直下のローカライズされたPictures候補も調べ、既存写真が最も新しい `Resonite` フォルダを自動選択します。保存先が別PCや共有先にある場合だけ、その写真フォルダをローカルから読み取れる `--screenshots-dir` で指定してください。新規フォルダはResoniteの初回書き出しを待ちます。元の写真は残し、完成した画像だけを `--output` へコピーします。PNGがJPEGに変換される設定では `.jpg` を使うか、ResoniteのKeep Original Screenshot Formatを有効にしてください。形式・解像度が違う場合はエラーになります。

OneDriveなどで保存先が異なる環境は、環境変数 `RESOLOOP_SCREENSHOTS_DIR` または `.resoloop.json` / `%USERPROFILE%\.resoloop\config.json` の `screenshotsDirectory` に保存先を設定できます。優先順位はCLI → 環境変数 → project設定 → user設定です。[examples/capture.json](examples/capture.json)は原点付近を撮る最小例です。

同じ写真フォルダを使うresoloopの同時撮影は拒否します。撮影中は他のカメラや手動の写真撮影を避けてください。公開APIには撮影要求と保存ファイルを対応付けるIDがないため、撮影前後の新規ファイル差分で検出し、複数候補があれば `CAPTURE_AMBIGUOUS` を返します。画像が届かない場合は `CAPTURE_EXPORT_TIMEOUT`（exit 8）となります。対象ワールドが表示中か、レンダラーが動作しているか、保存先が正しいか確認してください。`--capture-timeout` は書き出し待ち時間、`--command-timeout` は接続・準備を含めた全体の上限です。

成功時は `screenshotAvailable: true`、`format: jpeg` または `png` を返します。併記する `.scene.json` はmanifest上の要約です。オフラインの比較には従来の `--output capture.svg` を使えます。

`ownership`には一時camera Slotの`parentId`／`slotId`／`slotName`と`cleanupCompleted`を返します。現在はRootへ作成し、exact ID・名前を確認してcleanupします。`scene summary`の`bounds.kind`は`geometry`／`partial`／`pivots`で根拠を区別し、ローカルmesh assetの頂点から外形を取得します。親transform適用後のrest AABBであり、動作中の掃引範囲や衝突形状ではありません。

Slotの`--position`／`--scale`／`--rotation`はJSON配列・objectと従来comma形式に対応し、有限値を要求します。例: `resoloop slot update VERIFIED_SLOT --position '[1,2,3]'`。inspectにはSlot自身の`members`とIDも含まれ、item auditはその所有root内のPosition／Rotation fieldなどを内部参照として扱います。

strict validationはadapterの書込み変換を実行し、Nullable enumを含む不正値をasset import前に検出します。nested SyncObjectは指定された子だけを再帰比較し、省略された既定値を差分にせず、宣言値のdriftは検出します。古いcheckpointにmanifestのidentityFieldsを足すだけでは識別根拠は追加されません。候補と所有範囲を確認して復旧してください。新規Blender exportはproviderを名前付きSlotへ分け、旧root配置には`--legacy-root-providers`を指定できます。

撮影の実機テストは `RESOLOOP_RUN_INTEGRATION=1` と `RESOLOOP_RUN_CAPTURE_INTEGRATION=1` の両方を設定して実行します。`RESONITE_LINK_URL` と必要に応じて `RESOLOOP_SCREENSHOTS_DIR` を指定し、`dotnet test tests/RLoop.IntegrationTests --filter FullyQualifiedName~CaptureIntegrationTests` を実行してください。専用Slotは削除されますが、ゲームが保存した写真は写真フォルダに残ります。

## Known limitations

- ResoniteLink 0.13.1自体がBetaで、breaking changeの可能性があります。
- applyはschema v1のJSONのみです。operationは非atomicでrollbackはできませんが、操作単位の保留・readback証拠と、停止後に確認すべき対象を返します。
- List更新は公開API上whole-member replacementです。`diff`は要素added/removedを表示してから一括更新します。SyncObject要素は子memberを含む構造値へ正規化して比較します。
- `capture --output capture.jpg` / `.png` は専用のInteractiveCameraでゲーム内画像を撮影し、ローカルの写真書き出しを読み取ります。Resoniteのレンダラーと写真保存先へのアクセスが必要です。`.svg` は引き続きオフライン投影で、`screenshotAvailable: false` です。
- logsはLink protocolからのstreamではなく、明示されたローカルlog fileのtailです。
- runtime probeは `safe: true` と `--probe --yes` の二重許可が必要です。public Reflectionに公開されたSyncMethodを呼ぶ `method` probeに加え、fieldを一時変更してafter assertionをpollし、`finally`で元の値へ戻して復元確認する `set-member` probeを利用できます。公開されないinteractionはstructural-onlyで、test結果の`verification`は`partial`になります。`partial`を実機interaction確認済みとして扱わないでください。
- ResoniteLink 0.13.1はUIXの`SyncDelegate` memberをComponent definition/update modelへ公開せず、Dynamic Impulse helperと`CallInput.Trigger`も呼び出し可能なSyncMethodとして公開しません。resoloopはraw messageやmember名を推測せず、これらのinteractionをstructural-onlyとして報告します。
- Flux-SDK 1.9.xのinterface型global input（実機で確認した `IButton global` など）はdeploy前に`FLUX_INTERFACE_GLOBAL_UNSUPPORTED`で拒否します。concrete Componentの`element` inputからmodule内でglobal化するか、Dynamic Impulse bridgeを使用してください。`Slot element` inputの配線は正常動作を確認しています。

## License and upstream notes

ResoniteLinkはMIT、Flux-SDK programmatic integrationはAGPL-3.0-or-laterです。この構成はFlux-SDKへリンクするため、resoloopの配布条件もAGPL-3.0-or-laterとしています。公開インターフェースを中心に実装し、非公開Resoniteソースやデコンパイル結果を同梱していません。

## 識別情報付き catalog と共通 IR 検証

`resoloop validate FILE.json --catalog CATALOG.json --json` は Core の `ApplyDocumentValidator.ValidateAsync(..., catalog: ...)`／`ApplyCatalogValidator.ValidateFileAsync` に接続します。SDK 型は adapter の取得 snapshot／mapper 内に留まり、Apply JSON と `schema describe` には metadata を追加しません。新規 catalog の format/mapper は `2`。format 2 は hash 対象の `content.acquisitionFailures`（要求型・取得種別・理由）を追加します。mapper 1 は既存の `APPLY_CATALOG_UNAVAILABLE` で拒否し、失敗記録のない format 1 は現 mapper identity でのみ読めます。指定 Component/SyncObject 自身も独立に型取得し、その継承情報に flattened 定義の member だけを足します。不一致・独立取得の欠落/失敗・基底あり印に対する基底欠落は閉包未確認です。各型の失敗と 512 型/2 分の上限到達を snapshot に保存し、失敗記録を catalog に引き継ぎます。最後の session 再確認が期限に収まらなかった snapshot は unverified として保存し export を拒否します。identity は Resonite version、server ResoniteLink version、client package version、mapper version、取得日時で、取得時の `evidenceIdentity` と一致が必要です。内容は canonical JSON の SHA-256 `contentHash` で照合します。出典は `live`、照合済み `version-cache`、失敗になる `unverified`。合成資料には `synthetic: true` を明示します。hash は取得の証明ではなく改変検出です。offline は保存済み identity の整合だけを確認し、現在の runtime version は確認しません。

Component/member の取得範囲、完全名と確認済み alias、base/interface 閉包、generic の確認状態、nullable／tuple の要素、list／array／dictionary と SyncObject の入れ子を保持します。取得範囲外 member は不存在にしません。型名が違うだけでは参照不適合と判定せず、未取得の基底型・interface、未確認 generic、外部参照・asset・Slot member の型不明は `APPLY_CATALOG_UNAVAILABLE`。確認済み閉包で証明できた不適合だけが `APPLY_REFERENCE_TYPE_MISMATCH` です。Single は nullable の非 null 値と tuple 各要素を含め `VALUE_CONVERSION_FAILED` で変換失敗・NaN／Infinity・表現範囲外を拒否します。丸めや精度損失は許容し、member 固有範囲は推測しません。既存 issues／`ApplyValidationResult`／終了コード 6 を使います。

catalog 成功だけでは `Strict=true` にしません。`--catalog --strict` は先に offline catalog preflight、次に既存 live strict と session/client version 照合を行います。Workbench backend の対応は追加しません。通常 CLI に生成コマンドは追加せず、[開発用 tool](tools/RLoop.CatalogExport/README.md) で snapshot の export と catalog import を行います。旧 cache は取得 identity がなく受け入れません。catalog 未指定の既存経路は維持します。V11 の fixture は合成で、実 catalog・live capture の検証は別途必要です。

For source diagnosis, add `--diagnostics NEW_FILE.json` to `validate|diff|plan|apply`. This separate JSON has `diagnosticVersion: "1"` and structured key/member/IR path, known/unknown source, expected/observed evidence and completeness; a known null is distinct from unknown. Bundle map v1 stores original TS/TSX UTF-16 AST offsets and one-based, end-exclusive line/column ranges bound to source hashes. Fix TSX using that location, then create a fresh bundle/request; generated JSON lines are not an authoring location. Unchanged function props forwarded directly preserve caller expressions; rewritten scalar or nested object props have unknown primary locations; Scope, fragments, map callbacks and selected conditional children retain original ranges. Spread/computed members and untracked value transfers have unknown primary locations with real expressions/calls as related evidence. Handwritten and legacy generated JSON also accept diagnostics, with unknown source. Legacy stdout/stderr JSON, context.issues and reports are unchanged. Diagnostics require a new file in an existing writable directory, distinct from the selected input/bundle, catalog and state paths (including missing files); writing failure is reported on stderr without changing validation/apply judgement or exit code. Catalog evidence never completes runtime verification.
## apply の停止後は保留と実機を確認する

apply は送信直前に、計画時の型・member 種類・値・接続を比較します。観測した変化や writer の可能性は `APPLY_PRECONDITION_FAILED`（exit 6、`context.reason`）で拒否します。writer の可能性は、対象 field ID を `IField<…>` として参照する観測済み Component から検出します。対象を持つ Component 自身と、今回 apply が管理して書く参照は除きます。読むだけの参照と駆動する参照は区別できないため、可能性として停止します。観測外は completeness に unknown と記録して書込みを進めます。unknown は writer がいないという証明ではありません。

Component の任意の `propertyModes` は member 名を `config` / `initial` / `runtime` / `driver-owned` に対応させます。
未指定なら従来どおり `fields` は config、`initialFields` は作成時だけの initial です。
通常 apply は runtime と driver-owned の member を作成時にも書きません。driver-owned の宣言は writer の所有証明にはなりません。

config は通常の設定更新、initial は作成時だけの初期値です。`fields` と initial、`initialFields` と config の矛盾は既存の `APPLY_COMPONENT_FIELD_POLICY_CONFLICT` で拒否します。

apply の state は `schemaVersion: 3` です。v1/v2 は読めますが、安全確認後の最初の保存で v3 になり、保留が空でも v2 に戻りません。v3 に未対応の CLI は `APPLY_STATE_VERSION_UNSUPPORTED` で拒否します。state 本体の `pending` に操作 ID、session の観測、正確な対象 ID（不明なら null）、送信内容、応答と readback の証拠、確認できた部分を保存します。型 cache や生成 bundle とは別の記録です。

送信前に保留を保存し、応答後に送った属性・member を一度だけ readback します。確認できた結果の保存後に対応を確定し、不一致・欠落は保留のまま `APPLY_WRITE_UNVERIFIED`（exit 7）で停止します。同じ apply の中では再送しません。サーバの明示的な否定応答は、保留を外して元のエラーで停止します。更新の否定応答では、部分的な適用を調べるため一度 readback し、観測値を診断に載せます。確定済みの対応は変えません。保留を外す保存に失敗した場合は保留が残ります。cancel・timeout・state 保存失敗は従来のコードを保ち、後続の送信を止めます。

次の apply は、双方の URL と照合済み S-ID が一致し、受付の証拠があり、正確な ID・型・親・所有 Slot の鎖を確認できる保留を照合します。値が合う部分は確定し、合わない部分は「書けていないと分かった」として保留を解消します。確認した対象の ID は採用しますが、送った値を確定済みの値として記録しません。解消した部分は warning の診断 `APPLY_PENDING_RESOLVED_NOT_APPLIED` に操作 ID・key・member・期待値・観測値を残します。この診断に終了コードはありません。その後は今の観測と宣言から通常どおり計画します。同じ宣言なら新しい確認つきの書込みを一度試み、宣言を直していれば直した内容を使います。削除対象がまだ在ると確認できた保留も解消しますが、prune の送信には引き続き `--prune --yes` が必要です。

identity を証明できない、作成 ID が不明、受付の証拠が無い、対象が不在・型違い・親違いの場合は自動では解消しません。名前・型・順番による作成 ID の回収もしません。失敗 context と `--diagnostics` の `operationId`、`sendStatus`、`pending`、`confirmed`、`confirmedBindings`、`evidencePersistence` を使い、実機で調べる正確な ID と未確定部分を確認してください。

実機を `inspect EXACT_SLOT_ID --members` または `component inspect EXACT_COMPONENT_ID` で確かめた後、`resoloop apply FILE --state STATE_FILE --discard-pending OPERATION_ID --yes` で指定した保留だけを破棄できます。接続せずに project state lock を取り、保存して終了します。その実行では通常の apply を続けず、保留内の候補の対応を採用しません。確定済みの対応は維持します。`--prune`・`--adopt` など書込みを伴う引数との併用は `INVALID_OPTION`、`--yes` が無ければ `CONFIRMATION_REQUIRED`、存在しない操作 ID は `INVALID_OPTION` です。

v3 の操作 ID は state の `pending[].operationId` です。v1/v2 の空 ID は、停止時に表示する `legacy:slot:KEY` または `legacy:component:KEY` を使います。KEY は実効 key の URI エスケープ表記です（例: `child/a` → `legacy:slot:child%2Fa`）。旧形式では、その空 ID の未証明の対応だけを外します。破棄の成功 JSON は `stateFile`・`operationId`・`kind`・`key`・`id`（不明なら null）・`warning` を返します。人間向けの表示にも対象と注意事項を載せます。

作成の保留を破棄しても候補の対応は採用せず、未確定の作成物は管理の外になります。実機で作成済みなら次の apply で重複する可能性があります。更新・削除の保留を破棄した場合は、確定済みの対応が残ります。更新は実機の現在値、削除は正確な ID の存在・不在を確認してから再計画してください。先に実機を inspect で確かめ、不要なものは正確な ID と `--yes` で削除してください。ID 不明の作成は、実機で対象を見つけて確認する必要があります。

### 同じ URL の書込みと保留を project 間で調整する

直結の書込みは、session を観測してから session lock、project state lock の順に取得し、再観測して計画します。競合は待ち続けずに失敗し、handle は finally/Dispose で解放します。session lock の鍵は `ApplySessionObservation.NormalizeUrl` による正規化 URL です。host の大小文字、末尾ドット、loopback の localhost/127.0.0.1/::1、既定 port は正規化し、path/query は保持します。project と作業 directory に関係なく `<LocalApplicationData>/ResoLoop/write-locks/<URL の SHA-256>.lock` の排他的 file handle を一つ使います。cache 清掃の対象ではありません。

参加する入口は apply（asset import を含む）、直接の Slot create/set/delete、Component add/set/remove、画像 capture の一時カメラ作成から後片付けまで、`test --probe` の実行と復元です。読取り、offline SVG capture、`--discard-pending` は session lock を取りません。破棄は project state lock だけを使います。Flux deploy/watch の書込みはこの lock の対象外で、Workbench の操作も変更していません。

`APPLY_SESSION_BUSY`（exit 7）は同じ URL の別 writer が handle を保持している状態です。`APPLY_STATE_BUSY` は従来どおり、一つの state の writer 競合です。ファイルの存在や PID の古さで stale 判定しません。動いている writer が保持する lock を削除・奪取せず、holder の終了を待ってください。プロセス終了で OS が handle を解放すれば次の取得が可能です。

lock ファイルには最後に書込みをした state の絶対パスを保存します。保留の複製ではありません。所在の保存を保留作成より先に完了し、保存できなければ送信しません。次の holder はその state 本体を確認します。ファイルまたは directory が無いと正確に分かれば、別 project の書込みも続行します。アクセス拒否、壊れた JSON、未対応 version、所有 key の欠落は読取り不能として扱います。別 project の保留・旧形式の空 ID・読取り不能、または lock の所在情報の破損があれば `APPLY_WRITE_UNVERIFIED`（exit 7）で止まります。`context.reason` は `previousStatePending` / `previousStateUnreadable`、`stateFile` は確認すべき state、`lockFile` は session lock のパスです。壊れた所在情報の場合は state のパスも不明になり得ます。

その state を直すか、元の project の宣言と state で保留を解決してください。指定保留を破棄する場合は、正確な対象を実機で確かめてから `--discard-pending OPERATION_ID --yes` を使います。別 project が自動でその保留を直すことはありません。state を恒久的に失った場合は、ResoLoop の書込みが動いていないことと実機の状態を確かめたうえで、報告された `lockFile` を削除してください。動いている writer が保持する lock は削除・奪取しないでください。

lock の URL は session identity の証明ではありません。保留の identity は、選択した URL と discovery の `S-` ID を照合できたときだけ matched と記録します。明示の `--url` 接続は追加 discovery を行わず、identity unknown になります。新規の書込みは許可しますが、その接続で中断した保留は自動で確定しません。discovery 経由で接続し、保留と今の照合済み identity が一致する場合にだけ自動照合します。接続連番 `UniqueSessionId` は identity に使いません。

### apply 内の削除は正確な所有と完全な観測を要する

prune は引き続き `--prune --yes` が必要です。relocate の移動元 Component 削除には新しい引数を要求しません。どちらも直前に、計画時の stable key・正確な ID・型・所有 Slot とその鎖を再確認します。Root、所有 claim の衝突、曖昧な ID、所有の変化は拒否します。直接の `slot delete` と `component remove` には apply の所有証明を追加せず、既存の `--yes`・対象確認・Root 拒否に session lock だけを加えています。

Slot の削除前は各 Slot の直下 children/components を読み、部分木を全てたどります。深度の打切り・reference-only・欠落・不明な一覧・10,000 Slot の観測上限を越える場合は `APPLY_PRECONDITION_FAILED` で止めます。子の一覧が null でも、要求した depth 1 の応答で正確な対象の完全 Slot が返った場合は、実機で確認した leaf の表現として子なしと扱います。Component 一覧が欠落している場合は空と扱いません。管理していない子孫や Component は巻き込みません。engine が自動追加した Component も、出現しただけでは所有物にしません。

削除は保留を保存してから送り、正確な ID の不在を一度 readback します。不在と認めるのは `SLOT_NOT_FOUND` / `COMPONENT_NOT_FOUND` だけです。ほかのエラーでは対応を外さず保留を残します。応答を失った削除は、不在だけを根拠に成功と断定せず、新しい apply も重複作成・二重削除を送りません。

古い driver が新しい driver の参照を妨げる入替えは、最初の readback 不一致で停止し、一回の apply では完了しません。先に古い driver を宣言から外し、`diff --deletes-only` で正確な対象を確認して `apply --prune --yes` で削除します。不在を inspect で確かめてから新しい driver を宣言に追加して apply してください。この二段階の手順は offline test で確認済みです。すでに失敗した入替えでは、保留の確認と候補の曖昧さの解消が先に必要です。同型候補を ordinal で選んだり、state を消して回収したりしないでください。

### 保証の範囲

観測した値・型・writer の可能性の競合を検出し、同じ PC・同じ OS ユーザーの、この版以降の ResoLoop 間で書込みを調整します。これにより別 project の未確定書込みを飛び越しません。

操作は `atomic:false` です。全体の原子性や rollback、古い CLI・別 PC/ユーザー・人・外部ツール・ProtoFlux への排他、観測外や確認と送信の間の競合、readback 後の値の保持は保証しません。state の flush と atomic replacement は行いますが、電源断や filesystem 固有の耐久性も無条件には保証しません。同じコマンドの再実行だけで安全に収束すると判断せず、保留と実機の証拠から続行を決めてください。
