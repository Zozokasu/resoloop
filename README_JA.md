[English](README.md)｜[日本語](README_JA.md)

# resoloop

## UIX制作の効率化

新規制作では `children` に `{"$recipe":"button","$with":{"key":"accept","rect":{}}}` と直接記述できます。include/export不要で、生成キーは `uix-button--accept` を接頭辞にします。既存prototypeのキーは変わりません。適用済み宣言の移行にはキー変更の確認が必要です。

適用後の値・参照確認は `resoloop observe '$member:state.Value' '$member:toggle.TargetValue' --state STATE --json` で最大64フィールドを一括取得できます。型・member ID・参照先IDを保持し、存在しない指定は失敗します。再接続時は既存のstable参照解決を使います。異なるComponentの値は順次取得であり、同一瞬間のsnapshotではありません。

`uix recipe list --json` で機能構造のレシピ、`uix recipe describe button --json` で引数と接続口を確認できます。button・boolean-state・scroll-contentに加え、text-input・toggle・choice・slider・value-stateを同梱しています。共通の選択状態からタブや開閉パネルも構成できます。色・形・文字・寸法・押下時の表現はレシピに固定せず、利用側で自由に構成します。

```powershell
resoloop uix recipe export button --output content/recipes/button.json --json
resoloop diff content/main.json --brief --report artifacts/plan-01.json --json
resoloop apply content/main.json --brief --json
```

エクスポートした通常のprototype JSONをincludeして使います。[レシピの接続方法](skills/codex/resonite-uix/references/recipes.md)を参照してください。`--brief`は差分・監査などの表示量を減らし、`--report`は省略前の詳細を新規ファイルに保存します。既存ファイルは上書きしません。diffには宣言・runtime型検証が含まれるため、通常作業でvalidateの2モードを重ねる必要はありません。apply自身の直前検証は維持します。

![resoloop_logo](./resource/resoloop_resonite_16_9.png)

resoloop は、Codex や Claude Code などの AI エージェントから Resonite のワールドを操作するための CLI です。

作りたいものを AI に伝えると、AI が現在のワールドを確認し、Slot や Component の構成をファイルへ記述して、ResoniteLink 経由で反映・検証します。作業内容がファイルとして残るため、同じ構成を繰り返し適用したり、Git で変更を管理したりできます。

resoloop が生成する作業ルートには `FrooxEngine.AI_GeneratedContent` を自動で付与し、`Source` に実行中のツール名とバージョン（例: `[resoloop 0.1.0-preview.9]`）を記録します。宣言ツリー内の持ち運び・装備用ルートにも同じタグを付与します。

> [!NOTE]
> 現在はプレビュー版です。ResoniteLink も Beta のため、更新によって動作が変わる可能性があります。

## apply が停止したら state と実機を確認する

apply は各送信の直前に、計画時の値・型・接続を再確認します。値の変化や対象 field を参照する writer の可能性を観測したら、`APPLY_PRECONDITION_FAILED`（終了コード6）でその書込みを止めます。観測の外に writer がいるかは「不明」です。不明でも書込みは進みますが、writer がいないことを証明したわけではありません。応答後は送った値を一度だけ読み直します。不一致なら保留を残し、`APPLY_WRITE_UNVERIFIED`（終了コード7）で停止します。末尾で参照を再送する処理はありません。

Component の `propertyModes` は member ごとに `config`・`initial`・`runtime`・`driver-owned` を指定します。省略時は従来どおり、`fields` は設定、`initialFields` は作成時だけの初期値です。通常 apply は runtime と driver-owned を作成時にも書きません。宣言だけで driver の所有を認めることもありません。

state v3 は確定済みの対応と保留を保存します。v1/v2 は読めますが、保存は v3 になり、古い CLI は v3 を拒否します。サーバが明示的に拒否した要求は保留を解消します。次の apply は、discovery の identity・受付・正確な ID・型・親・所有の証拠がそろった保留を照合し、一致した部分を確定します。確認できた不一致は保留を解消し、今の観測から再計画します。作成 ID や受付、identity が不明なら自動では解消しません。同名・型・順番による作成の回収も行いません。明示の `--url` で接続すると identity は不明になり、中断した保留を自動で確定できません。discovery 経由の接続なら一致を照合できます。

失敗後は `context.reason`、`stateFile`、`operationId`、確定部分と completeness を読み、`inspect EXACT_SLOT_ID --members` または `component inspect EXACT_COMPONENT_ID` で実機を確認してください。そのうえで、指定した保留だけを破棄できます。

```powershell
resoloop apply FILE --state STATE --discard-pending OPERATION_ID --yes
```

この操作は接続せず、世界へ書きません。確定済みの対応は残ります。作成の候補を採用することはなく、実機で作成済みなら次の apply で重複する可能性があります。更新・削除の保留を破棄しても、確定済みの対応は管理対象として残るため、実機確認と再計画が必要です。

直結の書込みは、正規化 URL を鍵に `<LocalApplicationData>/ResoLoop/write-locks/<hash>.lock` の排他的 handle を共有します。別 project でも host の大文字小文字、localhost/127.0.0.1、既定 port の表記ゆれは同じ鍵です。apply とその asset import、直接の Slot/Component 編集・削除、画像 capture の一時カメラ、`test --probe` が参加します。読取りと offline SVG capture は lock を取りません。Flux deploy は対象外です。`APPLY_SESSION_BUSY`（終了コード7）は同じ URL の書込み競合、`APPLY_STATE_BUSY` は一つの project state の競合です。保持中の lock を削除・奪取しないでください。次の holder は最後の書込み元 state の所在を確認し、保留あり・読取り不能なら別 project も `APPLY_WRITE_UNVERIFIED` で止めます。その project の宣言と state で保留を照合するか、実機を確かめてから指定保留を破棄します。state が消えた・壊れた場合は検証済み backup を復元して確認してください。cache 清掃で lock や保留は消えません。

prune は `--prune --yes` が必須です。prune と relocate の移動元削除は、直前に stable key・正確な ID・所有 Slot を再確認します。Slot の部分木を完全に観測できない、管理していない子孫がある場合は削除を送りません。engine が自動で足した Component も、出現しただけでは所有物にしません。削除後は正確な ID の不在を `SLOT_NOT_FOUND` / `COMPONENT_NOT_FOUND` だけで確認します。直接の `slot delete` / `component remove` は既存の対象確認・`--yes`・Root 拒否を維持します。古い driver が新しい参照を妨げる入替えは、先に古い driver を宣言から外し、diff を確認して `--prune --yes` で削除します。不在を確認してから新しい driver を足して apply してください。readback 不一致を越えて一回で入れ替えることはできません。

保証するのは観測した競合の検出と、同じ PC・同じ OS ユーザーの、この版以降の ResoLoop 間の書込み調整です。操作は `atomic:false` で rollback はありません。古い CLI、別 PC・別ユーザー、人、外部ツール、ProtoFlux への排他や、観測の外・確認後の競合、値の将来の保持は保証しません。[停止後の詳しい手順](README-DETAILS.md#apply-の停止後は保留と実機を確認する)を参照してください。

## インストール

必要なもの:

- Windows 10 / 11
- [`.NET 10 SDK`](https://dotnet.microsoft.com/download/dotnet/10.0)
- Resonite
- Codex、Claude Code などの AI コーディングエージェント

PowerShell で resoloop をインストールします。

~~~powershell
dotnet tool install --global ResoLoop --version 0.1.0-preview.14
resoloop --version
~~~

すでにインストール済みの場合は、次のコマンドで更新できます。

~~~powershell
dotnet tool update --global ResoLoop --version 0.1.0-preview.14
~~~

## 使い方

### 1. プロジェクトを作る

Resonite で作りたいものごとに、専用のプロジェクトを作成します。

~~~powershell
resoloop init MyResoniteProject
Set-Location MyResoniteProject
~~~

### 2. ResoniteLink を設定する

1. Resonite を起動し、編集したいワールドを開きます。
2. Dashboard の `Session` ページから `Settings` タブを開きます。
3. 画面左下の `Enable ResoniteLink` を選択します。
4. `ResoniteLink running on port: ...` と表示されればOK。

あとはresoloopが自動でポートを見つけて接続します。

環境変数を使って自分で設定することもできますが必須ではありません。

たとえば、表示されたポートが `12449` の場合:

~~~powershell
$env:RESONITE_LINK_URL="ws://localhost:12449"
resoloop doctor
~~~

最後に `ready` と表示されれば準備完了です。

### 3. AI に作業を依頼する

作成したプロジェクトを AI エージェントで開きます。プロジェクト作成後から同じ AI セッションを使い続けている場合は、生成された Skill を認識させるため、一度セッションを開き直してください。

あとは、Resonite で実現したい内容を普段の言葉で依頼します。たとえば:

~~~text
Resoniteでテレポーターガンを作って。装備できるアイテムで、銃みたいな形。銃を撃つと弾が山なりに飛んでいって、当たった地点に自分がワープするようにして。
~~~

## Blenderによるモデル制作

複雑なモデルの制作を依頼したときは、resoloopがBlenderを使うことがあります。

PCにBlenderがインストールされていれば自動で検出して利用します。

Blenderを立ち上げておく必要はありません。

## さらに詳しく

宣言形式の推測を減らすため、`resoloop manifest scaffold --key panel --output content/panel.json --json` で外観を含まない宣言を作成できます。必要な項目だけ `resoloop schema describe camera --json` で照会してください。

Reflectionは `type query --request FILE.json --json` で必要なmemberとenum候補を一括取得できます。要求形式は `schema describe reflection --json` で確認できます。保存した型定義はResonite・ResoniteLinkのバージョンとAdapter/Coreビルドが一致すれば、期限なしでcheck・diff・apply・値変換にも利用します。ローカル接続のポート変更では失効しません。`verified: true` はバージョン一致で信頼した結果も含み、`source: version-cache` と元の観測時刻で区別できます。`--refresh` は実機から更新、`--cache off` はディスクを読み書きせず実機照会、`--cache-dir DIR` は保存先の指定です。MOD/DLL構成を変えた場合は更新してください。ID・現在値・参照先は毎回実環境から確認します。`type check --manifest FILE.json --brief --json` はstrict検証を利用しますが、diff/apply直前の重複チェックは不要です。[設計・実測結果](docs/REFLECTION-EFFICIENCY.md)を参照してください。

新規素材は `manifest scaffold --kind provider --key front-material --type '[FrooxEngine]FrooxEngine.UI_UnlitMaterial' --output NEW_NODE.json` で名前付きSlotを生成し、childrenへ追加して見た目の設定を記入します。validate/diffは同一Slot内の識別リスクを警告します。

適用済みの平面Canvasは `resoloop capture content/panel.json --frame '$slot:canvas' --view front --output artifacts/front.jpg --json` で自動撮影できます。背面は `--view rear`。対象を動かさず実測矩形から距離を計算します。曲面・はみ出す子要素・遮蔽物等は明示カメラで確認してください。[詳細と検証](docs/AUTHORING-ASSISTANCE.md)。

- [詳細資料](README-DETAILS.md) — コマンド、設計、宣言形式、Flux-SDK、制約
- [クイックスタート](docs/QUICKSTART.md) — 適用、検証、ProtoFlux を含む詳しい手順
- [宣言形式](docs/DECLARATIVE.md) — `content/*.json` の仕様
- [ロードマップ](docs/ROADMAP.md)

## ライセンス

[AGPL-3.0-or-later](LICENSE)

既存制作物の再適用・移行ではcheckpointを保持し、`diff/plan/apply --state STATE --require-state` を使います。TSXのnamed exportでownershipをroot keyから独立指定でき、build metadataは生成JSONの出力先に依存せずproject基準を保ちます。project全体の移動後は再buildしてください。[制作・移行の詳細](README-DETAILS.md)を参照してください。

TSX の受け渡しでは build 前に毎回新しい要求 ID R を生成し、`resoloop-jsx build content/main.tsx --bundle --catalog catalog.json --build-id R -o build/R/bundle.json` を実行します。exit 0 のときだけ、同じ R を `resoloop validate|diff|plan|apply build/R/bundle.json --build-id R` に渡します。出力 directory の再利用は拒否し、失敗した build は今回の bundle を公開しません。CLI は内包 IR/map/catalog・使用型・記録した source/catalog の内容を接続前に検査し、最初の実機書込み直前にも入力を再確認します。接続ありのコマンドは合成 catalog を拒否し、接続先と client の version を照合します。offline validate の合成 fixture は実機証拠にはなりません。bundle 不正は `APPLY_BUILD_BUNDLE_INVALID`（exit 6、reason 付き）、要求 ID の引数不整合は既存 exit 2、Workbench は既存 unsupported で停止します。手書き JSON と従来生成 JSON の直接利用は Node 不要のまま、警告も追加しません。

入力の保証範囲は TypeScript の source/import graph と catalog です。動的 import/require、Node 組込み、`resoloop-jsx` runtime 以外の外部 package があれば公開を停止します。文や呼出し先は解析せず、環境変数・時刻等の暗黙入力は検出できないため保証対象外です。map は元 AST から確認できる位置を保持し、追跡できない位置は `unknown` です。R と確定印は producer との契約であり、偽造・ID 再利用や最終確認後の競合は保証しません。[bundle 形式と手順](tools/resoloop-jsx/README.md)を参照してください。

## catalog を使う offline 検証

`resoloop validate content/panel.json --catalog catalog.json --json` は接続・Node なしで展開済み IR を検証します。catalog は明示したファイルだけを読み、自動検索・live 取得はしません。取得 identity と mapper version、内容 hash を確認し、Single の変換失敗・非有限・表現範囲外を `VALUE_CONVERSION_FAILED`、確認済み型閉包から証明できた参照不適合を `APPLY_REFERENCE_TYPE_MISMATCH` で拒否します。通常の丸め・精度損失は許容し、member 固有の 0〜1 等の範囲は推測しません。欠落・未確認・識別不一致・閉包不足・外部 ID／asset／Slot member 等の型不明は `APPLY_CATALOG_UNAVAILABLE` で停止し、既存 issues に型・member・JSON path を返します。終了コードは既存の検証失敗と同じ 6 です。

成功しても `strict: false` のままです。`--catalog --strict` は catalog preflight 後に従来の live strict を実行し、接続先の version も照合します。live 実行は別途認可が必要です。catalog 未指定の validate・diff・plan・apply、schema/state、出力 JSON は変更しません。catalog の hash は改変検出用で、実機取得の証明ではありません。識別情報のない旧 reflection cache は catalog に昇格できません。V11 の固定原本・identity/hash・手書き診断表は合成 fixture で、実 Component の検証結果ではありません。[開発用 export/import tool](tools/RLoop.CatalogExport/README.md) を参照してください。Reflection、範囲を絞った現況観測、変更後 inspection は引き続き必要です。

TSX の修正位置は `validate|diff|plan|apply FILE --diagnostics NEW_FILE.json` で確認できます。別 JSON の `diagnosticVersion` は `"1"`。key/member/IR path、known/unknown の source・expected・observed、completeness を持ち、既知の null と未取得を区別します。bundle/map v1 は元 AST の UTF-16 offset・1 始まりの行/列・終端を含まない range と source hash を保持します。誤 member は property 名、誤参照は値を作った式に戻ります。関数 props の直接転送、Scope、fragment、map、選択された条件枝は元の位置を保持します。spread・動的 member 名・追跡できない転送は primary が unknown、実在する式や呼出しは related です。TSX を直して新しい R で再 build し、生成 JSON の行を修正入口にしません。手書き・従来生成 JSON でも利用でき、source は unknown です。既存 stdout/stderr JSON、context.issues、report、state は不変です。診断は既存の書込み可能ディレクトリの新規ファイルに出し、書出し失敗を stderr へ明示して検証/apply の判定・exit code を維持します。catalog の成功だけで runtime 検証を complete にしません。
