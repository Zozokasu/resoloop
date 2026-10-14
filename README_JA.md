[English](README.md)｜[日本語](README_JA.md)

# resoloop

## Component カタログと TypeScript 補助

通常のCLI作業では、Reflectionで正確なComponentの完全型名を確認し、JSON配列 `component-types.json` に記録します。既存のResoniteLink URL設定を利用して、読み取り専用のカタログを取得し、TypeScript用宣言を生成できます。

```powershell
resoloop catalog capture --types component-types.json --output catalog.json --url ws://localhost:<現在のport>
resoloop catalog types catalog.json
resoloop-jsx build content/main.tsx -o build/main.json
resoloop validate build/main.json --catalog catalog.json --json
```

`--url`を省略すると、CLIの既存のURL・設定選択を使います。取得はLink backendで行い、sessionのwrite lockを取らず、実機へ書き込みません。取得前後のversionを照合し、入力名は512件まで、再帰依存を含む取得試行名は失敗分も含めalias正規化前で一意512件まで、取得時間は2分までです。

要求したComponent/typeが欠けるか、取得が件数・時間上限で打ち切られた場合は、部分catalogを保存して `CATALOG_CAPTURE_INCOMPLETE`（exit 6）を返します。依存型だけの取得失敗は診断情報として残りますが、それだけではこのエラーになりません。JSONエラーでは元の取得結果を `error.context.result` に保持し、text出力でも複数行の結果と件数を残します。

`requestedTypes` は重複を含む入力名数、`acquisitionBudgetUsed` は失敗も含む取得試行済みの一意な名前数（alias正規化前）、`acquisitionBudgetLimit` は512です。`outputTypes` はexportされた型数で、既知scalarの合成により512を超えることがあります。budget情報のない旧snapshotは `acquisitionBudgetUsed: "unknown"` と報告します。

`catalog types`はoffline処理です。synthetic catalogと、identity・出典・hashなどの検査で `UnavailableReason` が返るcatalogを拒否します。取得が不完全でも、この検査を通る部分catalogからは型宣言を生成できます。確認済みで対応しているfield値には型を付け、未確認・未対応の値は `JsonValue` に残します。部分catalogを使う場合も、対象文書に必要なmetadataが足りるかC#のcatalog検証で確認してください。catalog formatは2、mapperは3のままです。生成器はversion 3で、宣言のheader formatは1です。mapper 2のcatalogは `APPLY_CATALOG_UNAVAILABLE` で拒否されます。明示認可のあるlive取得で作り直し、型宣言を再生成してください。

既定出力は最寄りの `.resoloop.json` があるprojectの `.resoloop/catalog-types.d.ts` と `.resoloop/catalog-fields.ts` です。projectがなければ現在のdirectoryを基準にします。既定出力先は現在のdirectory基準です。`--output X.d.ts` は `X.fields.ts` も生成し、別のsuffixでは宣言だけを書いてwarningを出します。宣言headerはformat 1、generator 3、catalog content hash、runtime file名とSHA-256を持ちます。runtime SHAはBOMなしUTF-8で計算し、CRLFはLFへ正規化します。`resoloop-jsx build` が自動で読むのは `.resoloop.json` のあるproject（または `--project-root`）の `.resoloop/` だけです。そのproject内で実行するか `--output` を指定してください。生成宣言は `CatalogComponentRegistry` を拡張します。通常の `tsc` では `include` に指定するか、TypeScriptファイルから `/// <reference path="../.resoloop/catalog-types.d.ts" />` で参照してください。buildで使うruntime tableは `content/main.tsx` から `../.resoloop/catalog-fields.js` のように明示importします。generator 3の宣言だけの旧形式は型補助に使えますが、`componentFields` にはruntime tableが必要です。

生成型は `<Component type="…">` の `fields` と `initialFields` に付与されます。従来の `<Field>` は `JsonValue` のままで、member名と値をTypeScriptでは検査しません。catalog-bound APIでは `componentFields(type, catalog)` が返す `Lamp.Field` を使うと、確認済みmember名と値を検査できます。確認済みmember値の型をownerのgeneric closure未完了だけを理由に捨てることはありませんが、未知の型やmemberは推測しません。

TypeScriptはboolean/number/stringを厳密に扱います。たとえばbooleanの `true` と文字列 `"true"`、int32の `42` と文字列 `"42"` はTypeScript上で別の型です。C# runtime変換は両者を受け入れる場合があります。C#のstring変換はraw値の文字列表現を使うため、TypeScriptのstringより広く受理されます。整数の範囲・有限性などC#側の変換検査も残ります。最終的なcatalog判定はC#の `ApplyCatalogValidator` が行います。

カタログ取得や型宣言だけではruntime動作を検証したことになりません。

## 実験的なObject合成

`defineObject<P>()({ exports, body })` は再利用するbodyと公開referenceを定義します。`Def.instance(id)` は配置ごとのhandleを返し、JSXでは `<Def handle={handle} ...props />` と使います。`handle` は予約済みpropなので、利用者prop `P` に含められません。`ref.export(handle, name)` は定義した公開名だけを参照し、戻り値にも型が付きます。`componentFields(type, catalog)` は生成runtime tableからmemberと値を結び付けます。厳密なreference型は `boolean`、`int32`、`string`、`single` に限られ、数値値はTypeScriptの `number` です。reference型では `int32` と `single` を区別します。

次の例は架空のoffline `Fixture.Objects.Lamp` と `Fixture.Objects.Toggle` を使います。これらの型やmemberがResoniteに存在する証拠ではありません。実際に使うcatalogはReflectionで確認し、CLI生成のruntime tableを読み込んでください。生成tableを製品コードで手書き代用しないでください。

<!-- object-example -->
~~~tsx
import { Slot, Component, defineObject, componentFields, ref } from "resoloop-jsx";
import { catalog } from "../.resoloop/catalog-fields.js";

const Lamp = componentFields("Fixture.Objects.Lamp", catalog);
export const ownership = { key: "object-fixture" };

const Light = defineObject<{ name: string; intensity: number }>()({
  exports: { enabled: Lamp.ref("light", "Enabled") },
  body: ({ name, intensity }) => (
    <Slot key="root" name={name}>
      <Component
        key="light"
        type="Fixture.Objects.Lamp"
        fields={{ Intensity: intensity }}
        initialFields={{ Enabled: true }}
      />
    </Slot>
  ),
});

const desk = Light.instance("desk-lamp");
const ceiling = Light.instance("ceiling-lamp");

export default (
  <Slot key="scene" name="Objects">
    <Light handle={desk} name="Desk" intensity={1.5} />
    <Light handle={ceiling} name="Ceiling" intensity={4} />
    <Component
      key="toggle"
      type="Fixture.Objects.Toggle"
      fields={{ Target: ref.export(desk, "enabled") }}
    />
  </Slot>
);
~~~

Object instance IDは `^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$` に一致し、文書全体で一意でなければなりません。body内のSlotとComponentには明示keyが必要です。展開後のkeyは `id::localKey` となり、`Scope` はさらに `::` segmentを加えます。`id::` namespaceは展開文書で予約されます。body内のtargetはprivateです。公開targetであっても生のreferenceでは使えず、定義済み公開名を `ref.export` で指定してください。Objectの入れ子は未対応です。生成IRはschema version 1、ownershipは単一key、apply stateはversion 3のままです。

TSX buildのObject/Field検査はexit 1で、`APPLY_OBJECT_INVALID`、`APPLY_OBJECT_DUPLICATE`、`APPLY_OBJECT_EXPORT_INVALID`、`APPLY_OBJECT_EXPORT_UNKNOWN`、`APPLY_OBJECT_TYPE_MISMATCH`、`APPLY_OBJECT_PRIVATE_REFERENCE`、`APPLY_FIELD_TYPE_MISMATCH` を返します。TypeScript診断はexit 2です。C# `validate` は値変換・参照適合性・catalog evidenceの最終判定を行い、`VALUE_CONVERSION_FAILED`、`APPLY_REFERENCE_TYPE_MISMATCH`、`APPLY_CATALOG_UNAVAILABLE` は既存のexit 6を使います。

利用者が参照している間はinstance IDとexport名を維持してください。export名を保てばconsumer側のsourceは維持できます。Componentのowner移動、type/key変更、target差替えでruntime reference IDは変わり得るため、export先をObject定義内で確認し、必要なら対象contentを作り直してください。`initialFields` はComponent作成時だけの値です。Object APIは実機の所有関係を証明せず、再作成後もruntime値が保たれるとは保証しません。offlineではtoken/contextの反復・並行buildを確認していますが、ESM cache回収やwatch modeは保証対象外です。APIはexperimentalです。runtime tableやcatalog evidenceが不足すると `APPLY_CATALOG_UNAVAILABLE` でTSX buildがexit 1になります。Object APIは実機のResoniteでは未検証で、実物のToggleのComponentとその参照先のmemberもReflectionでは確かめていません。



明示import先のruntime fileを解決できない場合は `TS2307`（build exit 2）です。declaration header、hash、runtime companionの不一致や古さは `APPLY_CATALOG_TYPES_HASH_MISMATCH`（exit 1）になります。`tsc`単独ではheader/hashの整合、元JSONに対する鮮度、definition identity、配置、Object privacyを検証しません。通常buildも元catalog JSONの鮮度や実機との一致は保証せず、入力JSONとのbindingは既存bundle modeの `--catalog` だけが行います。computed literalの `__proto__` だけは狭い既存例外で、他のprototype-key形式へ範囲を広げません。

確認済みで非nullableのscalar field targetだけが、`boolean`、`int32`、`string`、`single` の `FieldRef<T, R>` として厳密に型付きされます。nullableやその他のreferenceは既存の `string | null` または `UncheckedReference` fallbackを保ちます。通常のstring referenceと従来の `ref.*` helperは互換ですが、raw internal referenceはObject boundaryを越えられません。Object境界外からは宣言した公開名を `ref.export` で参照します。

project-rootの解決ではentryが明示 `projectRoot` 内なら、そのrootをauthoring pathとproject catalog読込に使います。entryがroot外なら従来どおりentry directoryをrootにし、relative `authoring.source`（例: `../project/content/main.tsx`）を保ちます。外部projectのcatalog importはTypeScript `TS6059` になる場合があります。

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

schema v1はComponentの任意`fieldAliases`とSlotの`tag`にも対応します。TSXでは`<Field>`子要素と`<Slot tag>`を使えます。alias参照はcanonicalな`$member`へ解決され、tagを省略すると既存値を保ちます。記法・診断・適用条件は[宣言の詳細](docs/DECLARATIVE.md)を参照してください。

Slotを直接変更する場合は、正確なSlotをinspectしてから `resoloop slot set VERIFIED_SLOT --active false` または `--active true` を実行し、もう一度inspectして `data.isActive` を確認します。作成時は `resoloop slot create --parent VERIFIED_PARENT --name NAME --active false` を指定できます。値は小文字の `true` / `false` のみです。省略すると既定値または現在値を保ちます。宣言型applyにactive指定はありません。

state v3 は確定済みの対応と保留を保存します。v1/v2 は読めますが、保存は v3 になり、古い CLI は v3 を拒否します。サーバが明示的に拒否した要求は保留を解消します。次の apply は、discovery の identity・受付・正確な ID・型・親・所有の証拠がそろった保留を照合し、一致した部分を確定します。確認できた不一致は保留を解消し、今の観測から再計画します。作成 ID や受付、identity が不明なら自動では解消しません。同名・型・順番による作成の回収も行いません。明示の `--url` で接続すると identity は不明になり、中断した保留を自動で確定できません。discovery 経由の接続なら一致を照合できます。

失敗後は `context.reason`、`stateFile`、`operationId`、確定部分と completeness を読み、`inspect EXACT_SLOT_ID --members` または `component inspect EXACT_COMPONENT_ID` で実機を確認してください。そのうえで、指定した保留だけを破棄できます。

```powershell
resoloop apply FILE --state STATE --discard-pending OPERATION_ID --yes
```

この操作は接続せず、世界へ書きません。確定済みの対応は残ります。作成の候補を採用することはなく、実機で作成済みなら次の apply で重複する可能性があります。更新・削除の保留を破棄しても、確定済みの対応は管理対象として残るため、実機確認と再計画が必要です。

直結の書込みは、正規化 URL を鍵に `<LocalApplicationData>/ResoLoop/write-locks/<hash>.lock` の排他的 handle を共有します。別 project でも host の大文字小文字、loopback アドレス（localhost、127.0.0.0/8 の 127.0.0.2 など、::1）、既定 port の表記ゆれは同じ鍵です。scheme・port・path・query が違えば別の鍵で、別の鍵が同じ world へ届くかは検証していません。apply とその asset import、`flux deploy`・`deploy-manifest`・manifest watch、直接の Slot/Component 編集・削除、画像 capture の一時カメラ、`test --probe` が参加します。Flux は配置ごとにlockを取り、配置後の読み直しとstate確定まで保持します。読取りと offline SVG capture は lock を取りません。`APPLY_SESSION_BUSY`（終了コード7）は同じ URL の書込み競合、`APPLY_STATE_BUSY` は一つの project state の競合です。動いている writer が保持する lock を削除・奪取しないでください。

次の holder は最後の書込み元 state を確認します。ファイルまたは directory が無いと正確に分かれば、別 project の書込みも続行します。アクセス拒否は不在と扱いません。保留あり・読取り不能、または lock の所在情報が壊れている場合は `APPLY_WRITE_UNVERIFIED` で止め、context に `stateFile` と `lockFile` を返します。その state を直すか、元の project の宣言と state で保留を解決してください。指定保留を破棄する場合は、先に正確な対象を実機で確かめます。state を恒久的に失った場合は、ResoLoop の書込みが動いていないことと実機の状態を確かめたうえで、報告された `lockFile` を削除してください。cache 清掃で lock や保留は消えません。

prune は `--prune --yes` が必須です。prune と relocate の移動元削除は、直前に stable key・正確な ID・所有 Slot を再確認します。Slot の部分木を完全に観測できない、管理していない子孫がある場合は削除を送りません。engine が自動で足した Component も、出現しただけでは所有物にしません。削除後は正確な ID の不在を `SLOT_NOT_FOUND` / `COMPONENT_NOT_FOUND` だけで確認します。直接の `slot delete` / `component remove` は既存の対象確認・`--yes`・Root 拒否を維持します。古い driver が新しい参照を妨げる入替えは、先に古い driver を宣言から外し、diff を確認して `--prune --yes` で削除します。不在を確認してから新しい driver を足して apply してください。readback 不一致を越えて一回で入れ替えることはできません。

保証するのは観測した競合の検出と、同じ PC・同じ OS ユーザーの、この版以降の ResoLoop 間の書込み調整です。操作は `atomic:false` で rollback はありません。古い CLI、別 PC・別ユーザー、人、外部ツール、ProtoFlux への排他や、観測の外・確認後の競合、値の将来の保持は保証しません。[停止後の詳しい手順](README-DETAILS.md#apply-の停止後は保留と実機を確認する)を参照してください。

## 実験的な creation regenerate

creation regenerate は所有root全体を作り直す実験機能です。planとapplyには `--url auto --session S-ID` が必要で、applyにはplan確認後の明示的な `--yes` も必要です。置換はdelete-first方式です。先に旧rootを削除するため、その間は制作物がない状態になり、新rootの作成に失敗しても旧rootは戻りません。root配下は手動で追加した内容も含めてすべて削除されます。実行前に対象rootと配下を正確に確認してください。

TSXからは `resoloop-jsx build content/room.tsx --target creation -o creation.json --project-root .` でenvelopeを作れます。default exportはroot Slot 1つにします。creation targetではkeyを省略でき、兄弟Slotに同じnameを使えます。明示keyは重複できません。`creationId` はentryのnamed `ownership` exportにあるkeyを優先し、なければroot Slotの明示keyを使います。どちらも無い場合や、1〜128文字で制御文字を含まない値でない場合は `CREATION_ID_INVALID` でbuildが失敗します。自動生成keyは `creationId` になりません。通常のschema-v1 buildでは、従来どおり明示keyと異なる兄弟Slot名が必要です。creation buildでは `--draft`、`--bundle`、`--catalog`、`--build-id` を併用できず、この版にcreation bundleはありません。Asset・Flux・Scriptなど未対応のcreation宣言は、plan/applyのregenerate preflightが接続前に停止させます。creation state v4は通常のstate v3とは別形式で、移行機能はありません。

`validate` はenvelopeを自動認識し、`--mode` なしでoffline検証します。`plan` と `apply` には `--mode regenerate` が必要です。

```powershell
resoloop validate creation.json --json
resoloop plan creation.json --mode regenerate --state creation-state.json --url auto --session S-ID --json
resoloop apply creation.json --mode regenerate --state creation-state.json --replace-order delete-first --url auto --session S-ID --yes --json
```

TSX creation経路と、同名兄弟Slotを含むregenerateは実機で未検証です。

保留が残ったら、再applyせずstate、operation ID、rootの正確なIDを確認してから復旧方法を選びます。

```powershell
resoloop creation recover --state creation-state.json --action rollback --url auto --session S-ID --yes --json
resoloop creation recover --state creation-state.json --action finish --url auto --session S-ID --yes --json
resoloop creation recover --state creation-state.json --discard-pending OP --yes --json
```

Resoniteは、Componentを付けると別のComponent（例: UIXの `RectTransform`）を自動で足すことがあります。作り直しはこれを失敗にせず、足された数を結果の `engineAddedComponents`（`creation recover --action finish` も同じ）で返します。自動で足されたComponentは定義に無いため、定義から参照も値の指定もできません。値を決めたいComponentは定義に明示して書いてください。Slotの数は定義と一致しないと失敗します（`APPLY_WRITE_UNVERIFIED`）。

定義したComponentを、Resoniteが自分で足したものの重複として消した場合（例: UIXのComponentの後に書いた `RectTransform`）、作り直しは、足されていた方へ定義の値を書いて使います。定義の順には依りません（警告 `CREATION_COMPONENT_MERGED_WITH_ENGINE_ADDED`）。採用するのは、持ち主のSlotを完全に観測でき、同じ型の候補がちょうど1つの場合だけです。それ以外は `COMPONENT_NOT_FOUND` で失敗します。`plan --mode regenerate` も、Componentの型と値を `apply` と同じに検査します（書込みはしません）。コマンドの期限切れでpendingが残ったときは、`suggestions` に `creation recover` の案内が出ます。

記録したrootをResoniteの中で手で消した場合、次のplan／applyは止まらず、一致を確かめたsessionで記録IDが正確に不在なら初回の作成として進みます（何も削除せず、警告 `CREATION_PREVIOUS_ROOT_ABSENT` が付きます）。`--discard-pending` の後に世界へ残ったrootは自動では消えません。結果に出たIDを確かめて、手で消してください。

rollbackが削除するのは所有を証明できたcandidate全体だけです。残っていて所有を確認できた旧rootは保持しますが、delete-firstですでに消えた旧rootは復元しません。印のないcandidateは自動削除しません。finishは、作成完了を示す記録とtree全体の所有を再検証できた場合だけ状態を確定します。discardはローカルの保留記録だけを消し、worldの確認・清掃は行わず、残ったrootの可能性を警告します。`creation recover --bind-root`、`creation migrate`、`creation inspect` は未提供です。通常のinspectで正確なIDを確認してください。詳しい手順は[creation recovery workflow](skills/codex/resonite-build/references/creation-regenerate.md)を参照してください。

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

`resoloop validate content/panel.json --catalog catalog.json --json` は接続・Node なしで展開済み IR を検証します。catalog は明示したファイルだけを読み、自動検索・live 取得はしません。取得 identity と mapper version、内容 hash を確認し、Single の変換失敗・非有限・表現範囲外を `VALUE_CONVERSION_FAILED`、確認済み型閉包から証明できた参照不適合を `APPLY_REFERENCE_TYPE_MISMATCH` で拒否します。boolean は `true` または文字列 `"true"`、int32 は整数 `42` または文字列 `"42"` を受け入れます。`yes`、小数、int32範囲外は受け入れず、その他の数値型も新たな対応対象ではありません。string fieldはruntime値のraw textで変換されるため、TypeScriptのstringより広く受理される場合があります。通常の丸め・精度損失は許容し、member 固有の0〜1等の範囲は推測しません。欠落・未確認・識別不一致・閉包不足・外部 ID／asset／Slot member等の型不明は `APPLY_CATALOG_UNAVAILABLE` で停止し、既存issuesに型・member・JSON pathを返します。終了コードは既存の検証失敗と同じ6です。mapper 2の旧catalogは `APPLY_CATALOG_UNAVAILABLE` で拒否されます。live取得が明示認可された作業で再取得し、型宣言も再生成してください。generator/header/hash不一致は `APPLY_CATALOG_TYPES_HASH_MISMATCH` となり、hashが同じでも旧generatorの宣言を再生成します。

成功しても `strict: false` のままです。`--catalog --strict` は catalog preflight 後に従来の live strict を実行し、接続先の version も照合します。live 実行は別途認可が必要です。catalog 未指定の validate・diff・plan・apply、schema/state、出力 JSON は変更しません。catalog の hash は改変検出用で、実機取得の証明ではありません。識別情報のない旧 reflection cache は catalog に昇格できません。V11 の固定原本・identity/hash・手書き診断表は合成 fixture で、実 Component の検証結果ではありません。[開発用 export/import tool](tools/RLoop.CatalogExport/README.md) を参照してください。Reflection、範囲を絞った現況観測、変更後 inspection は引き続き必要です。

TSX の修正位置は `validate|diff|plan|apply FILE --diagnostics NEW_FILE.json` で確認できます。別 JSON の `diagnosticVersion` は `"1"`。key/member/IR path、known/unknown の source・expected・observed、completeness を持ち、既知の null と未取得を区別します。bundle/map v1 は元 AST の UTF-16 offset・1 始まりの行/列・終端を含まない range と source hash を保持します。誤 member は property 名、誤参照は値を作った式に戻ります。関数 props の直接転送、Scope、fragment、map、選択された条件枝は元の位置を保持します。spread・動的 member 名・追跡できない転送は primary が unknown、実在する式や呼出しは related です。TSX を直して新しい R で再 build し、生成 JSON の行を修正入口にしません。手書き・従来生成 JSON でも利用でき、source は unknown です。既存 stdout/stderr JSON、context.issues、report、state は不変です。診断は既存の書込み可能ディレクトリの新規ファイルに出し、書出し失敗を stderr へ明示して検証/apply の判定・exit code を維持します。catalog の成功だけで runtime 検証を complete にしません。

実験的な変数APIは `resoloop-jsx/variables` subpath から利用します。bool変数とcatalog-bound Fieldの `drive`/`bind` の使い方は[package guide](tools/resoloop-jsx/README.md#experimental-variables-api)を参照してください。このAPIは実機のResoniteでは未検証です。
