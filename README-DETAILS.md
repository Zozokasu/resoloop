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

結果は必ず`complete`を確認してください。`false`のとき`truncation.reason`は`max-slots` / `depth-boundary` / `result-limit`のいずれかです。`depth-boundary`は深さ上限にSlotが接しており、子の不在を証明できない状態を意味します。`result-limit`の続きは返却された`--cursor`で取得します。cursorは接続とqueryに束縛されるため、条件を変えたり再接続した場合は誤った続きを返さず拒否されます。

更新前後の比較は巨大なJSONを読み比べるのではなく`resoloop snapshot create --under SLOT --output before.json`、変更、`after.json`作成、`resoloop snapshot diff before.json after.json`の順で行います。snapshot diffは接続不要です。snapshotは安定pathをkeyにした正規化IRを保存するため、property順や浮動小数の表記揺れは差分になりません。差分は新しい側が親を実際に観測した範囲でのみ削除を報告し、証明できない場合は`SNAPSHOT_PARTIAL`などのwarningを返します。

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

`.pg`に対するbuild/check/watchは既存Flux-SDK CLIをラップします。`flux node search/describe`はFlux-SDKの通常metadata出力を、同名nodeをfull identity別に保持したversioned catalogへcacheします（1.9.0の`froox-docs --json`は同名keyで失敗するため使用しません）。`flux validate-manifest`は接続せずにJSON shape、source、dependency、module portとbindingの一対一対応を検査します。JSON module manifestに対するwatchは依存順に成功buildだけを再deployします。`$slot:key` parentはworld stateから現在session向けに検証・再解決されます。moduleの `bindings` ではFlux input名を `mode: "source"`、output名を `mode: "drive"` として `$slot:key` / `$component:key` / `$member:key.MemberName` へ接続できます。driveはmember targetだけを受け付けます。明示したCLI `--state` はcurrent directory基準、manifest内の`worldState`、`source`、`deployState`はmanifest基準です。Fluxの`int`/`int32`/`System.Int32`、`bool`/`System.Boolean`などのscalar aliasは同じ型として照合します。IDはworld stateのcomponent index、管理member、`identityFields`を使って再解決されます。source signatureの未結線port、方向・型不一致、Flux-SDK 1.9.xのinterface global inputはbuild/deploy前に、build成功後の0 nodeはdeploy前に構造化エラーになります。deployはFlux-SDK 1.9のLoader.replaceを利用し、deploy先の`parentSlotId`と、再観測した実module childの`moduleSlotIdBefore` / `moduleSlotIdAfter`、解決済みbinding、checkpoint recoveryを返します。`doctor`は最小check/build probeでmanaged-dataの明示pathまたは自動発見が実際に成功するか確認します。

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

- CONNECTION_FAILED: ResoniteLinkがworldで有効か、画面上のportとURLが同じか確認
- COMPONENT_TYPE_NOT_FOUND: type searchの完全な結果を使う
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
- applyはschema v1のJSONのみです。operationは非atomicでrollbackはできませんが、操作単位のcheckpointと再実行手順を返します。
- List更新は公開API上whole-member replacementです。`diff`は要素added/removedを表示してから一括更新します。SyncObject要素は子memberを含む構造値へ正規化して比較します。
- `capture --output capture.jpg` / `.png` は専用のInteractiveCameraでゲーム内画像を撮影し、ローカルの写真書き出しを読み取ります。Resoniteのレンダラーと写真保存先へのアクセスが必要です。`.svg` は引き続きオフライン投影で、`screenshotAvailable: false` です。
- logsはLink protocolからのstreamではなく、明示されたローカルlog fileのtailです。
- runtime probeは `safe: true` と `--probe --yes` の二重許可が必要です。public Reflectionに公開されたSyncMethodを呼ぶ `method` probeに加え、fieldを一時変更してafter assertionをpollし、`finally`で元の値へ戻して復元確認する `set-member` probeを利用できます。公開されないinteractionはstructural-onlyで、test結果の`verification`は`partial`になります。`partial`を実機interaction確認済みとして扱わないでください。
- ResoniteLink 0.13.1はUIXの`SyncDelegate` memberをComponent definition/update modelへ公開せず、Dynamic Impulse helperと`CallInput.Trigger`も呼び出し可能なSyncMethodとして公開しません。resoloopはraw messageやmember名を推測せず、これらのinteractionをstructural-onlyとして報告します。
- Flux-SDK 1.9.xのinterface型global input（実機で確認した `IButton global` など）はdeploy前に`FLUX_INTERFACE_GLOBAL_UNSUPPORTED`で拒否します。concrete Componentの`element` inputからmodule内でglobal化するか、Dynamic Impulse bridgeを使用してください。`Slot element` inputの配線は正常動作を確認しています。

## License and upstream notes

ResoniteLinkはMIT、Flux-SDK programmatic integrationはAGPL-3.0-or-laterです。この構成はFlux-SDKへリンクするため、resoloopの配布条件もAGPL-3.0-or-laterとしています。公開インターフェースを中心に実装し、非公開Resoniteソースやデコンパイル結果を同梱していません。
