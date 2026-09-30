# Workbench読み取り対応表（W2-B）

更新: 2026-09-30
対象: ResoLoop W2-B の実装（このブランチ、コード確定 commit `0d334cc`（W2-B R2・R3 修正後。この文書の更新 commit を除く））、resonite-workbench `7d40c92`。R2（B1: 「不明」応答は接続変化ではない、B2: fieldの `MemberValue.Type` 充填、2026-09-30のlive比較が根拠）と R3 を含む。R3 の規定挙動: `type describe` の「不明」応答の扱い（`reflection.component` の「不明」は `reflection.type` で1回裏取りし、既知の非Component型なら `COMPONENT_TYPE_NOT_FOUND` でCLIが `DescribeTypeAsync` にフォールバック、それ以外は `WORKBENCH_UNAVAILABLE`）、「不明」の判定は `value` 存在＋nullに限定（欠落は不正な応答）、切断を挟む接続切り替わりでも型・member定義cacheを消去、fieldの `MemberValue.Type` は非ジェネリックな通常型のみ充填。`SLOT_NOT_FOUND`/`COMPONENT_NOT_FOUND` の「Unknown」扱いは不変。
表記: 無印はResoLoop（このworktreeのルート）からの相対パス。`WB:`はresonite-workbench側の参照（このworktreeには同梱されていない）。行番号は1-based。
根拠を確認できなかった項目には「未確認」と書く。

W2-A版（`agent/wb-w2a-mapping` 作成時、resonite-workbench `d549280` 前提）から変わった点:

- **R1（実装済み）**: `SlotRecord` に transform（position/rotation/scale）・`persistent`・`isReferenceOnly` が入った。Slotのトップレベルフィールドは、報告されれば全部埋まるようになった。
- **R2（実装済み）**: `reflection.search` が追加された。`type search` と、短い型名の一意解決ができるようになった。
- **R3（実装済み）**: `reflection.enum` が追加された。enum型の `EnumValues` / `IsFlags` が埋まる。
- **R4（保留）**: `world.observe` / `world.slot` にmember値をまとめて含める `includeMembers` は入っていない。member値は `reflection.component` ＋ member本数ぶんの `member.read` で取る（往復回数は3節）。
- **R5（保留）**: `TypeDefinitionInfo` の `isWorldElement` / open genericの `genericParameters` は入っていない。決められる型だけを返し、それ以外は `BACKEND_UNSUPPORTED` にした。

対象RPC（`src/RLoop.Workbench/Protocol/RpcNames.cs:58-91`）: `session.status`、`world.observe`、`member.read`、`reflection.search` / `reflection.component` / `reflection.type` / `reflection.enum`。helloは `session.read`・`world.read`・`member.read`・`reflection.read` を要求する（`src/RLoop.Workbench/WorkbenchResoniteClient.cs:37-39`）。`world.slot` / `world.hierarchy` / `reflection.capabilities` / `reflection.member` は現状使っていない。

読み取りの実装は `src/RLoop.Workbench/WorkbenchResoniteClient.Read.cs`（RPC合成）と、純粋変換の `WorkbenchSlotMapper.cs` / `WorkbenchMemberMapper.cs` / `WorkbenchReflectionMapper.cs` に分かれている。上限とエラーは `WorkbenchErrors.cs`。

---

## 1. フィールドごとの対応

### 1.1 `SlotInfo`（`src/RLoop.Core/Models.cs:101-115`）← `world.observe` の `SlotRecord` ＋ unexpanded/excluded stub

変換は `WorkbenchSlotMapper.BuildSlot`（`src/RLoop.Workbench/WorkbenchSlotMapper.cs:125-211`）。

| フィールド | 得られるか | 取り方 / 根拠 |
| --- | --- | --- |
| `Id` | 完全 | `SlotRecord.Id`（`:139`） |
| `Name` | 完全 | `SlotRecord.Name`。wire null→`string.Empty`（`:140,:205`。直結も `?? string.Empty` で同じ、`ResoniteLinkClientAdapter.cs:637`） |
| `ParentId` | 完全 | `SlotRecord.ParentId`（`:141`） |
| `IsActive` | 完全（未報告はnull） | `RequiredNullableBool`（`:142`）。wire null→null。直結 `slot.IsActive?.Value` と同じnullable意味 |
| `Position` / `Rotation` / `Scale` | 完全（R1。未報告はnull） | `SlotRecord.transform` の `{position,rotation,scale}` はwire JSON文字列。`ParseFloat3`/`ParseFloatQ` で `Vector3Value`/`QuaternionValue` へ（`:150-161,:259-270`）。transformが無い・nullのときnullのまま |
| `IsPersistent` | 完全（R1。未報告はnull） | `OptionalNullableBool`（`:147`） |
| `Tag` | 完全（未報告はnull） | `OptionalNullableString`（`:146`） |
| `IsReferenceOnly` | 完全（R1） | `SlotRecord.IsReferenceOnly`（`:148`）。要求depthより下の子・`excluded` の子は `IsReferenceOnly: true` のstub（id・name・parentIdのみ、`:191-200`）。stub名は unexpanded/excludedエントリまたはdepth外のフルrecordから取る（`StubName`、`:217-231`）。**W2-B R1 で確定**: 要求depth内で読み取りに失敗した子（unexpandedの`ReadFailed`）はstub化せず `WORKBENCH_UNAVAILABLE` で失敗する（1.5節・4節） |
| `Components`（`ComponentSummary[]`） | 一部 | `SlotRecord.Components` は `Id`+`ComponentType` のみ（`:163-173`）。member値は `includeComponentData` のとき `FillComponentMembersAsync`（`WorkbenchResoniteClient.Read.cs:315-347`）が `reflection.component`＋`member.read` で埋める（往復回数は3節）。stubには付かない |
| `Children` | 完全（上限内） | `childIds` を再走査（`:175-201`）。要求depthより深い子はstub化 |
| `Path` | 完全（ResoLoop側で計算） | `WorldService.AddPaths`（`src/RLoop.Core/WorldService.cs:1877`）。RPCに依存しない |
| `Members`（slotレベル。`inspect --members` のslot部分。Parent・Position等のSlot自身のsync member） | **不可** | `Members: null` で固定（`:210`）。直結は `Link.Slot` のフィールドを `MemberValue` へ包む（`ResoniteLinkClientAdapter.cs:650-655`）。`OrderOffset` 相当のフィールドはWorkbench側に無い。必要なときは直結経路を使う |

### 1.2 `ComponentInfo` / `MemberValue`（`src/RLoop.Core/Models.cs:86-99`）

| フィールド | 得られるか | 取り方 / 根拠 |
| --- | --- | --- |
| `Id` / `Type` | 完全 | 観測済みなら `_componentTypes` cache、未観測なら `member.read` の型probe（`__resoloop_type_probe__`）で `componentType` を得る（`RequireComponentTypeAsync`、`WorkbenchResoniteClient.Read.cs:353-371`）。どちらでも得られなければ `COMPONENT_NOT_FOUND` |
| `Members` | 取れるがN+1 | (a) `reflection.component(componentType)` で宣言member名一覧とfieldの `valueType`（`MemberDefinitionsAsync`、型ごとにcacheされる `DeclaredMembers` を返す、`:374-397`）。(b) 各member名に `member.read`（`ReadComponentMembersAsync`、`:412-435`）。1つでも読めないmemberがあれば `WORKBENCH_UNAVAILABLE`（`:426-431`）。部分mapは返さない。(a) が `Unknown` を返すとき（WorkbenchがComponent型定義を読めない）も同じく `WORKBENCH_UNAVAILABLE` で、messageに型名とWorkbenchのunknownReasonを含む。`Unknown` 応答のmetadataにはconnectionIdが無いため、接続切り替わり判定（4節）の対象にはならない |
| `MemberValue` 本体 | 一部 | `WorkbenchMemberMapper.MapMember`（`src/RLoop.Workbench/WorkbenchMemberMapper.cs:17-61`）。kind対応: `field`→`"field"`、`reference`→`"reference"`、`list`→`"list"`+`Elements`再帰、`syncObject`→`"syncObject"`+`Members`再帰。`opaque`はwireTypeが `"empty"`→`"empty"`、`"playback"`→`"SyncPlayback"`+`[FrooxEngine]FrooxEngine.SyncPlayback`、その他（array/dictionary等）→`"opaque"`+wireType（`:47-58`）。直結が返す `"dictionary"` の `Members` や `SyncPlayback` の `Value` 相当は得られない |
| `MemberValue.Value` | 一部 | `ObservedField.valueJson`（wireのJSON文字列）を `JsonNode.Parse` して入れる（`:74-92`）。直結の `BoxedValue` serialize（`ResoniteLinkClientAdapter.cs:670-671`）と整形まで一致するかは**未確認**（live項目） |
| `MemberValue.Type` | 一部 | 通常のfieldは `reflection.component` のmember定義 `valueType` からassembly接頭辞を除いて入れる（`[mscorlib]System.Single`→`System.Single`、直結のCLR完全名と同じ表記）。enum fieldは従来どおり `enumType`（直結もenum fieldは `EnumType`、`:666-669`）。nullのままなのはgeneric/nullable値型（valueTypeが `isGenericParameter: true` の `T` 等もジェネリック扱い、W2-B R3）、定義に一致しないmember、sync object内のnested memberとlist要素、field以外のmember（referenceは `targetType`、opaqueは変更なし） |
| `MemberValue.Id` | 一部（未確認） | `OptionalString "id"`（`:20`）。全member種で必ず報告されるかは未確認（live項目） |

### 1.3 `TypeInfo`（`src/RLoop.Core/Models.cs:171-187`）← `reflection.type`（enumなら＋`reflection.enum`）

変換は `WorkbenchReflectionMapper.MapType`（`src/RLoop.Workbench/WorkbenchReflectionMapper.cs:119-185`）。`reflection.type` の「不明」応答は `type describe` で `WORKBENCH_UNAVAILABLE`（4節、W2-B R3）。

| フィールド | 得られるか | 取り方 / 根拠 |
| --- | --- | --- |
| `FullTypeName` / `AssemblyName` / `Namespace` / `Name` / `BaseType` / `IsAbstract` / `IsInterface` / `IsGeneric` / `IsEnum` / `IsComponent` / `IsSyncObject` | 完全 | `reflection.type` の `TypeDefinitionInfo`（`:124-137`）。`Name` 未報告時は `[Assembly]Namespace.Type` から導出（`ShortName`、`:179,:333-354`）。`BaseType`/`Interfaces` は `Render`（`:308-330`）で直結の `ModelMapper.Render` と同じ規則で文字列化 |
| `IsWorldElement` | 導出（R5保留） | Workbenchは報告しない。`isComponent‖isSyncObject`→true、`isEnum‖isValueType`→false と決められる型だけ答える。それ以外（普通のclassなど）は `BACKEND_UNSUPPORTED`（`:141-145`） |
| `GenericParameters` | 一部（R5保留） | open generic（`genericArguments` が全て `isGenericParameter`）ならパラメータ名を返せる（`:152-157`）。閉じたgenericは `BACKEND_UNSUPPORTED`（`:158-163`） |
| `Interfaces` | 完全 | `TypeDefinitionInfo.Interfaces` を `Render`（`:130-133`） |
| `EnumValues` / `IsFlags` | 完全（R3） | `reflection.type` が `isEnum` を報告したときだけ `reflection.enum` を呼ぶ（`WorkbenchResoniteClient.Read.cs:151-156`）。`status:"Found"` 以外は `WORKBENCH_UNAVAILABLE`（`WorkbenchReflectionMapper.cs` `EnumDefinition`、`:240-246`） |

### 1.4 型検索（`SearchComponentTypesAsync`）

**対応済み（R2）**。`reflection.search` で `[Assembly]Namespace.Name` 形の名前をサーバー上限500件取得し（`WorkbenchResoniteClient.Read.cs:70-86,:23`）、直結と同じ `SearchScore`（exact < `.Name` 末尾 < 最終segment prefix < substring）でre-rankする（`:82-85,:500-507`）。`truncated:true` のときもranked subsetを返す（signatureが報告できない、`:80-81`）。

短い型名の一意解決（`DescribeComponentTypeAsync`・`DescribeTypeAsync` の短い名前入力）は `ResolveComponentTypeAsync`（`:457-496`）: `reflection.search` の結果のうち、assemblyを除いた名前が入力に一致または `.{type}` で終わる候補が**ちょうど1件かつ非truncated**のときだけ受理する。0件・複数・truncatedは `COMPONENT_TYPE_NOT_FOUND`/`TYPE_NOT_FOUND`（検索結果という積極的な証拠があるため。直結はsessionのComponent型一覧を持つので、全件確定できる点が違う）。`reflection.component` が「不明」のとき `DescribeComponentTypeAsync` は同じ完全名で `reflection.type` を1回追加で呼び（`:101-116`、往復が1増える）、既知かつ `isComponent` false なら「型は存在するがComponentではない」証拠として `COMPONENT_TYPE_NOT_FOUND`（CLIの `type describe` は `DescribeTypeAsync` にフォールバックして成功）、`reflection.type` も「不明」または `isComponent` true なら `WORKBENCH_UNAVAILABLE`（`IsKnownNonComponent`、`:275-281`）。`DescribeTypeAsync` の「不明」は `WORKBENCH_UNAVAILABLE`（4節、W2-B R3）。

### 1.5 観測の上限と既定値

- `world.observe` は `maxDepth` 1-32、`maxSlots` 1-8,192。ResoLoop側は要求depthを `WorkbenchLimits.RequireDepth` で検証し、0未満（`-1`=無制限）または33以上は**RPCを呼ばずに** `WORKBENCH_OBSERVE_LIMIT_EXCEEDED`（終了コード7、`WorkbenchErrors.cs:9-28`）。depth 0は `Math.Max(1, depth)` で1段観測し、level 1以下の子をstub化する（`WorkbenchSlotMapper.cs:39-45,:184-200`）。
- 応答側の打切り: `truncation` の `SlotLimit` bitが立っていれば `WORKBENCH_OBSERVE_LIMIT_EXCEEDED`（`:97-102`）。**W2-B R1 で確定**: `ReadFailed`（要求した深さの範囲内で読み取りに失敗した子）はstub化せず `WORKBENCH_UNAVAILABLE` で失敗する。stub化されるのは要求depthより下の子（`DepthLimit`の未展開やdepth外のフルrecord）と `excluded` の子だけで、stub名の取り方は1.1節のまま。接続中ユーザーのSlot（`User ...`、除外reasonは `UserRoot`）は常に `excluded` 扱いで、要求depth内でもstubとしてのみ返り、含める選択肢は無い（直結では展開される。2026-09-30 live比較: `hierarchy --depth 2` で直結269 Slot / Workbenchは183 Slot展開）。
- 既存コマンドの `--depth` 範囲（最大64）と既定値は変更していない: `hierarchy` 既定2（`src/RLoop.Cli/Program.cs:519`）、`find` 既定8（`:533`）、`inspect` 既定1（`:553,:563`）、`tool audit` 既定16（`:683`）、`uix audit` 既定6・上限32（`:637-638`）。既定値が32を超えるコマンドは無いため、「Workbench経由のときだけ既定を32にする」切り替えは入れていない。
- 深さ64固定の内部処理は、Workbench経由では `WORKBENCH_OBSERVE_LIMIT_EXCEEDED` で失敗する: runtimeRelocatable Slotの再解決（`WorldService.cs:285`）、`item audit`（`:319`）、`diff`/`plan` の `PrepareAsync`（`:911,:931`）。

---

## 2. コマンドごとの判定

「対応」は、Workbench経由でも直結（`--backend link`）と同じ形の結果を返せること（欠ける値はnullまたはスタブとして明示される）。「延期」は、同じ結果を保証できない値の欠落またはコストのため、このラウンドでは対応しないもの。

ゲートの現状: `BackendSupport.WorkbenchSupport`（`src/RLoop.Cli/BackendSupport.cs:26-52`）は下表と同じ区分を宣言し、`RequireSupported`（`:59-72`、`Program.cs:119` で全コマンドに効く）は `--backend workbench` のとき `supported` 以外を `BACKEND_UNSUPPORTED` で拒否する（理由は `DeferralReason`、`:75-90` がerror contextに出す）。「対応」の8コマンドはclient read pathに到達できる: `GetSessionInfoAsync`（W1）と `GetSlotAsync`/`GetComponentAsync`/`SearchComponentTypesAsync`/`DescribeComponentTypeAsync`/`DescribeTypeAsync` の5メソッドが配線済み。`doctor` は `planned-w2` の据え置き。`validate` は `supported` だが、member値の事前検査 `ValidateComponentMemberAsync`（`WorkbenchResoniteClient.Read.cs:165-169`、後述の表にも明記）だけは通過させず明示的に `BACKEND_UNSUPPORTED` で失敗する。

| コマンド | 判定 | 根拠 / 残る差 |
| --- | --- | --- |
| `status` / `ping` | 対応 | `GetSessionInfoAsync`（W1実装、`WorkbenchResoniteClient.cs:83-165`。`session.status` を厳密に読む） |
| `hierarchy` | 対応 | `world.observe`→`SlotInfo`（`Read.cs:43-58`）。`--include-components` はmember.readのN+1。`--depth` 33以上/`-1` は `WORKBENCH_OBSERVE_LIMIT_EXCEEDED` |
| `find` | 対応 | `GetSlotAsync` のみ（`WorldService.cs:89-99`）。`IsReferenceOnly` はR1で取れるので `--exclude-reference-only` も機能する |
| `observe` | 対応 | `GetSessionInfoAsync`＋stable reference解決＋`GetComponentAsync`（`Observation.cs:10-37`）。全て実装済みの読み取り経路 |
| `inspect` | 対応 | `InspectAsync`/`InspectComponentsAsync`（`WorldService.cs:80-145`）。slotの `members`・未報告transformはnullのまま（1.1節） |
| `type` | 対応 | `search`/`describe`/`--member`/`query`/`check --request` は `reflection.*` で完結。`specialize` は接続不要（`Program.cs:159-168`）。例外は `check --manifest`（次行） |
| `validate`（非strict） | 対応 | 接続を要しないschema検証（`Program.cs:151-157`） |
| `validate --strict` / `type check --manifest` | **明示的に `BACKEND_UNSUPPORTED`** | member値の変換検証 `client.ValidateComponentMemberAsync`（`ApplyWorkflow.cs:479`、`Program.cs:610-615,:794-804`）は直結adapterだけが実装する（`ResoniteLinkClientAdapter.cs:143`）。Workbench経路では検証自体が実行できないため、通過させずに拒否する方針 |
| `doctor` | 延期 | `resonite-link-url` チェックがlink URLの解決・探索に依存（`Program.cs:383-385`） |
| `uix audit` | 延期 | member値がnullのときを「有効」と扱う（`UixAudit.cs:68,:100-102` `IsFalse`）ため、Workbenchのnull値と衝突する。加えてslotごとに `GetSlotAsync`×2（`:45-49`）でN+1が掛け算になる |
| `item audit` | 延期 | 深さ64固定（`WorldService.cs:319`）で上限超過。全Component member走査のN+1も重い |
| `tool audit` | 延期 | `Position`/`Rotation`/`Scale` が未報告のとき単位値へ黙ってフォールバック（`ToolAudit.cs:109-111`）。Workbenchでは誤った結果を返し得る |
| `capture` | 延期 | `--frame` は祖先transform必須（`CanvasFraming.cs:44-45`）。live captureは専用camera Slot/Componentの作成を伴い、書き込み経路が要る |
| `diff` / `plan` | W4 | 書き込み経路の前提ゲート。`PrepareAsync` は深さ64・transform・persistentを使う（`WorldService.cs:911,:931`） |
| `slot` / `component` / `apply` / `test` / `flux` | W4 | 書き込み経路。Workbench clientの write系メソッドは全て `BACKEND_UNSUPPORTED`（`WorkbenchResoniteClient.cs:167-197`） |
| `scene` / `blender` / `logs` | link-only | Resonite接続を使わない処理（`scene summary` のoffline要約、Blenderローカル実行、ログfile tail） |

---

## 3. Workbenchへの追加RPC（残りのもの）

実装済み（W2-A時点の提案1〜3が取り込まれた）:

- **R1**: `world.observe`/`world.slot`/`world.hierarchy` の `SlotRecord` に transform・`persistent`・`isReferenceOnly`（→ 1.1節が「完全」になった）。
- **R2**: `reflection.search`（→ `type search` と短い型名の一意解決、1.4節）。
- **R3**: `reflection.enum`（→ enumの `EnumValues`/`IsFlags`、1.3節）。

残っているもの:

- **R4**: `world.observe`/`world.slot` のComponentにmember値をまとめて含める `includeMembers`。
  - 判断材料（このブランチのclient実装での往復回数。`tests/RLoop.Workbench.Tests/WorkbenchReadTests.cs` のwire logから）:
    - `GetSlot`+`includeComponentData` = `world.observe` 1 ＋ `reflection.component`（Componentの型ごと、client内cache）＋ memberごとに `member.read` 1。fixture（2 Slot・2 Component・3 member）で6往復（`:464-477`）。
    - `GetComponent` = 型不明なら `member.read` probe 1 ＋ `reflection.component` 1 ＋ member数 N の `member.read`（`:622-630`）。直前の `GetSlot` で型が既知なら N のみ（`:660-671`）。
    - `member.read` は1回ごとにComponent全体をResoniteLinkから読むので、memberの多いComponentでは遅い。
    - 効く先: `hierarchy --include-components`、`inspect --members`/`--component`/`--member`、（対応した場合の）`uix audit`/`item audit`/`observe`。
  - 無くても値自体は取れる（1.2節）が、Slot数×Component数×Member数の往復になる。
- **R5**: `TypeDefinitionInfo` に `isWorldElement` と open genericの `genericParameters`。
  - 効く先: `type describe` の非Component型で `BACKEND_UNSUPPORTED` になる範囲の解消（1.3節）。優先度は低い。

---

## 4. 実装の方針（W2-Bで実際に取ったもの）

- **Coreのモデル（`SlotInfo`/`ComponentInfo`/`TypeInfo`等）と `IResoniteClient` のsignatureは変更しない**。変更禁止領域（`src/RLoop.Core/**`、`src/RLoop.ResoniteLink/**`、`src/RLoop.Workbench/Protocol/**`）には触れていない。`RLoop.Workbench` は `RLoop.Core` だけを参照する。
- **得られない値はnull**。0・空・既定値で埋めない。対象: 未報告のtransform・`isActive`/`isPersistent`/`tag`、slotレベル `members`、member定義から `type` を決められないfield（generic/nullable値型・定義不一致・nested/list要素）とfield以外のmemberの `type`、opaque memberの `Value`/`Members`/`Elements`。`MemberValue.Id` も報告が無ければnull。
- **決められない非nullable値は推測せず拒否**。`IsWorldElement`/`GenericParameters` が決められない型は `BACKEND_UNSUPPORTED`（`WorkbenchReflectionMapper.cs:141-163`）。
- **上限は切り詰めずに拒否**。depth 0〜32・8,192 Slotを超える要求と `SlotLimit` truncationは `WORKBENCH_OBSERVE_LIMIT_EXCEEDED`（終了コード7）。**W2-B R1 で確定**: `DepthLimit`・`excluded`・要求depthより下の未展開は引き続きstub化、要求depth内の `ReadFailed` は `WORKBENCH_UNAVAILABLE`。
- **「Unknown」は不在の証明ではない**。Workbenchのwireには「型が存在しない」を明示する形が無い（ReflectionResult は `value` と `unknownReason` だけ）ため、「不明」は不在の証明にならない。「不明」とみなすのは `value` プロパティが存在して null のときだけ（`IsUnknown`、`:264-267`）で、`value` 自体が欠落した応答は不正な応答（`WORKBENCH_UNAVAILABLE`、`WorkbenchReflectionMapper.cs:210-219`）として扱う（W2-B R3）。`world.observe` の `completeness:"Unknown"` は `SLOT_NOT_FOUND` で返すが、messageに「Unknownは不在の証明ではない」と含める（`WorkbenchSlotMapper.cs:60-68`）。`COMPONENT_NOT_FOUND` も同じ方針。`type describe` では `DescribeComponentTypeAsync` が `reflection.component` の「不明」に対し同じ完全名で `reflection.type` を1回追加で呼び、既知かつ `isComponent` false なら「型は存在するがComponentではない」証拠として `COMPONENT_TYPE_NOT_FOUND`（CLIは `DescribeTypeAsync` にフォールバックして成功するので、非Component型も従来どおり describe できる）、`reflection.type` も「不明」または `isComponent` true（Componentなのに定義を読めない）なら `WORKBENCH_UNAVAILABLE` で失敗し、messageに型名とWorkbenchのunknownReasonを含む（`TypeValue`/`ComponentValue`、`WorkbenchReflectionMapper.cs:189-203`）。`DescribeTypeAsync` の「不明」は `WORKBENCH_UNAVAILABLE`。完全名の打ち間違え（存在しない型）も同じ「不明」応答で `WORKBENCH_UNAVAILABLE` になり得るため、その場合は `type search` で正しい名前を確認する。`COMPONENT_TYPE_NOT_FOUND` が返るのは2条件だけ: 短い名前が `reflection.search` で一意に解決できない（該当なし・曖昧・500件の窓で切り詰め）か、型は存在するがComponentではない（describe が type にフォールバック）（1.4節）。`TYPE_NOT_FOUND` は `DescribeTypeAsync` の短い名前解決失敗時だけ（`:138-145`）。
- **stale・接続切り替わり・応答の形の不正・member読み取り失敗・型定義の読み取り失敗（`reflection.component` / `reflection.type` の `Unknown`）は `WORKBENCH_UNAVAILABLE`**。全読み取りRPCは `CallReadAsync`（`WorkbenchResoniteClient.Read.cs:178-227`）を通り、`stale` を拒否し、1つの論理読み取り内のconnectionId一致は `ReadOperation` が検査する（`:513-533`）。接続IDは `ObserveConnectionId`（`:236-247`）が `_lastKnownConnectionId`（`:36`、最後に確認した非nullの接続ID）として追跡し、異なる非null IDが来たとき `_componentTypes`/`_memberNames` の両cacheを消す（切断のnull報告を挟んで別接続へ移った場合も同じ。null IDはidentityを持たず記録を更新しない）。`Meta.ConnectionId` が null の間（切断報告後、再接続の最初の応答まで）はcacheを参照しない（`ConnectionConfirmed`、`:38-41`。`RequireComponentTypeAsync`・`MemberDefinitionsAsync` のlookupに効く）。`GetSessionInfoAsync`（session.status）の応答でも同じ条件で消す（`WorkbenchResoniteClient.cs:99`、W2-B R1で追加。`ConnectAsync` の無条件クリアと `_lastKnownConnectionId` 初期化は `WorkbenchResoniteClient.cs:65-68`）。
- **コマンド対応の正本は `BackendSupport.WorkbenchSupport`**。2節の表と同じ区分で `supported`/`planned-w2`/`planned-w4`/`link-only` を宣言し、CLIゲート（`RequireSupported`）が `supported` 以外を拒否する。

---

## 未確認の事項（liveで確かめるべき項目）

2026-09-30のlive比較（実セッションで両経路を実行）で確認済み、上記各節へ反映した項目: 接続中ユーザーのSlotは常に `excluded`（reason `UserRoot`）で要求depth内でもstub化（`hierarchy --depth 2`: 直結269 Slot / Workbench 183 Slot展開）。`reflection.component` が `Unknown` を返すComponent型が存在（`[FrooxEngine]FrooxEngine.GradientStripTexture`、unknownReason "ResoniteLink failed to read the definition of component type ...: Object reference not set to an instance of an object."）。以下は引き続き未確認:

- `ObservedField.valueJson` の整形（数値・`NaN`/`Infinity`・文字列escaping）が、直結の `BoxedValue` serialize（`ResoniteLinkClientAdapter.cs:670-671`、`JsonOptions` は `:630-633`）と一致するか。
- `reflection.component` の `MemberDefinitionInfo.valueType` の綴りが、直結の `Render(field.ValueType)`（`ResoniteLinkClientAdapter.cs:694`）と同じか。`MemberValueSyntax.IsStructuredTuple` を使うstrict検証（`ApplyWorkflow.cs:463-470`）に効く。
- enum fieldの `valueJson` の形（直結は `JsonValue.Create(enumField.Value)`、`:666-669`）。enum名文字列か、数値か。
- `--depth 0` の直結の挙動（Workbenchではルートだけフルで子はstubになる。直結で同じ指定のとき children が返るか）。
- RefIDの形式とsession判定（Workbenchの `id` が直結の要素IDと同じ形式か、`connectionId` の比較による同一session判定が直結と等価か）。
- `MemberValue.Id`（`member.read` の `"id"`）が全member種で必ず報告されるか（`WorkbenchMemberMapper.cs:20` はOptional）。
