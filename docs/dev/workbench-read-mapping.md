# Workbench読み取り対応表（W2-A）

作成: 2026-09-29
対象: ResoLoop `4f24b58`（main）、resonite-workbench `d549280`（`plan/workbench-design.md`と同じ照合HEAD。差分は未確認）。
表記: 無印はResoLoop（`C:\Users\jojoh\Documents\resoloop`）からの相対パス。`WB:`はresonite-workbench（`C:\Users\jojoh\Documents\resonite-workbench`）からの相対パス。行番号は1-based。
根拠を確認できなかった項目には「未確認」と書く。

対象RPC: `world.observe` / `world.slot` / `world.hierarchy`、`member.read`、`reflection.capabilities` / `reflection.component` / `reflection.type` / `reflection.member`（`WB:docs/rpc-protocol.md:127-135`）。

---

## 1. フィールドごとの対応

### 1.1 `SlotInfo`（`Models.cs:101-115`）と `WB:SlotRecord`（`WB:src/ResoniteWorkbench.Core/WorldModel/SlotRecord.cs:7-15`）

| フィールド | 得られるか | 取り方 / 根拠 |
| --- | --- | --- |
| `Id` | 完全 | `SlotRecord.Id` |
| `Name` | 完全 | `SlotRecord.Name` |
| `ParentId` | 完全 | `SlotRecord.ParentId` |
| `IsActive` | 完全 | `SlotRecord.IsActive` |
| `Position` / `Rotation` / `Scale` | **不可** | `SlotRecord`にtransformのフィールドが無い（`WB:SlotRecord.cs:7-15`）。design記録2.1・5.3(0)で既に確認済み。 |
| `IsPersistent` | **不可** | `SlotRecord`に無い。design記録5.3(0)で「Slotのtransform・persistentが無い」と明記（`plan/workbench-design.md:207,227`）。 |
| `Tag` | 一部 | `SlotRecord.Tag`は取れるが、reference-onlyのstub（`world.observe`のmaxSlots/maxDepthではみ出した子）はtagを報告しない仕様（`WB:docs/rpc-protocol.md:343-344`）。「完全に読まれたSlotのnull」と「stubのnull」を区別する情報がResoLoop側に無いので、`ApplyDocumentValidator`のようにtag無しを前提にする検証には使えない場面がある。 |
| `IsReferenceOnly` | **不可** | `SlotRecord`にフィールドが無い。ResoniteLink経由では`Link.Slot.IsReferenceOnly`から直接writeしている（`src/RLoop.ResoniteLink/ResoniteLinkClientAdapter.cs:645`）。Workbenchには「観測されず`UnexpandedSlot`として存在だけ分かる」という近い概念はあるが（`WB:.../WorldModel/SlotRecord.cs:33-38`）、`SlotRecord`自体のフラグではない。`find --exclude-reference-only`、`uix audit`（`UixAudit.cs:49`）、item/tool auditの階層走査（`WorldService.cs:107,127,289`）が壊れる。 |
| `Components`（`ComponentSummary[]`、Id/Type/Members） | 一部 | `SlotRecord.Components`は`ComponentRecord(Id, ComponentType)`だけで、member値を含まない（`WB:SlotRecord.cs:31`）。ResoniteLink経由の`GetSlotAsync(..., includeComponentData: true)`は1回のRPCでmember値まで埋めるが、Workbenchでは各Componentごとに`reflection.component`（型のmember一覧、キャッシュ可）＋member本数ぶんの`member.read`が要る（`WB:docs/rpc-protocol.md:131,133`）。値そのものは1.2節の`MemberValue`と同程度に取れるが、往復コストが桁違いに増える。 |
| `Children` | 完全（深さ・件数の上限あり） | `SlotRecord.ChildIds`＋`world.observe`/`world.slot`の再帰。上限は2節参照。 |
| `Path` | 完全（ResoLoop側で計算） | `WorldService.AddPaths`はResoLoop内のヘルパーで、RPCに依存しない。 |
| `Members`（slotレベル、`--members`用。Parent/Position/Rotation/Scale/Name/Tag/IsActive/IsPersistent/OrderOffset） | **不可**（transform・persistent分）／一部（Name・Tag・IsActive・Parentは合成可能） | ResoniteLink版は`Link.Slot`の各フィールドを`MemberValue`に包んで返す（`ResoniteLinkClientAdapter.cs:650-655`）。Workbenchには`OrderOffset`に相当するフィールドも無い。 |

### 1.2 `ComponentInfo` / `MemberValue`（`Models.cs:86-99`）

| フィールド | 得られるか | 取り方 / 根拠 |
| --- | --- | --- |
| `Id` / `Type` | 完全 | `ComponentRecord.Id`/`ComponentType`、または`member.read`の`MemberReadback.ComponentType`（`WB:.../MemberReadbackService.cs:11`）。 |
| `Members`（`IReadOnlyDictionary<string, MemberValue>`） | 一部（値は取れるが1リクエスト1メンバー） | 手順: (a) `reflection.component(componentType)`で`ComponentTypeDefinition.Members`（name→`MemberDefinitionInfo`）を得てmember名一覧を確定（`WB:.../Reflection/TypeDefinitions.cs:96-101`）。(b) 各member名について`member.read(componentId, memberName)`を呼び、`ObservedMember`（`field`/`reference`/`list`/`syncObject`/`opaque`の判別共用体、`WB:.../WorldModel/ObservedMember.cs:9-38`）を`MemberValue`へ変換する。Kindの対応は概ね付く（`field`→`"field"`、`reference`→`"reference"`、`syncObject`→`"syncObject"`、`list`→`Elements`に相当）。`opaque`（array/dictionary/playback/empty）は、ResoLoop側の`"dictionary"`/`"SyncPlayback"`ほど詳細な種別を返さない（`ObservedMember.cs:36-38`）ため、これらの種別は**一部**に留まる。 |
| `MemberValue.Value`（生JSON） | 一部 | `ObservedField.ValueJson`はwireのJSON文字列で、ResoniteLinkの`BoxedValue`シリアライズ（`ResoniteLinkClientAdapter.cs:670-671`）と完全に同じ整形になるかは**未確認**。 |

### 1.3 `TypeInfo`（`Models.cs:171-187`）と `WB:TypeDefinitionInfo`（`WB:.../Reflection/TypeDefinitions.cs:71-85`）

| フィールド | 得られるか | 取り方 / 根拠 |
| --- | --- | --- |
| `FullTypeName` / `AssemblyName` / `Namespace` / `Name` / `BaseType` / `IsAbstract` / `IsInterface` / `IsGeneric`(`IsGenericType`) / `IsEnum` / `IsComponent` / `IsSyncObject` | 完全 | `reflection.type(typeName)` → `ReflectionResult<TypeDefinitionInfo>`（`WB:docs/rpc-protocol.md:134`、`WB:.../TypeDefinitions.cs:71-85`）。 |
| `IsWorldElement` | **不可** | `TypeDefinitionInfo`にフィールドが無い。ResoniteLink版は`Link.TypeDefinition.IsWorldElement`を直接写す（`ResoniteLinkClientAdapter.cs:709`）。 |
| `GenericParameters`（open genericのパラメータ名。`type specialize`が使う） | **不可** | ResoniteLink版は`Link.TypeDefinition.GenericParameters`という別フィールドから得る（`ResoniteLinkClientAdapter.cs:710`）。WBの`TypeDefinitionInfo`にあるのは`GenericArguments`（closed genericの実引数、`TypeReferenceInfo`、`WB:.../TypeDefinitions.cs:84`）で、open genericのパラメータ名という意味では別物。 |
| `Interfaces` | 完全 | `TypeDefinitionInfo.Interfaces`（`TypeReferenceInfo[]`）から`Display`文字列化できる。 |
| `EnumValues` / `IsFlags` | **不可** | ResoniteLink版は`reflection.type`相当のRPCに加えて、別RPC`enum.get`（`_link.GetEnumDefinition`、`ResoniteLinkClientAdapter.cs:261-264`）を呼んで初めて埋める。Workbenchの一覧（`WB:docs/rpc-protocol.md:118-148`）に列挙型の値一覧を返すRPCが無い。 |

### 1.4 型検索（`SearchComponentTypesAsync`）

**不可**。Workbenchのメソッド一覧（`WB:docs/rpc-protocol.md:118-148`）に、名前や部分一致でComponent型を検索するRPCが無い。design記録2.1で「型検索（`SearchComponentTypesAsync`に当たるもの）は無い」と既に確認済み（`plan/workbench-design.md:41`）。`reflection.component`/`reflection.type`は完全一致の型名が必須で、曖昧検索の代替にならない。

### 1.5 観測の上限と既存コマンドの既定値の差

- Workbenchの`world.observe`は`maxDepth` 1-32、`maxSlots` 1-8,192（省略時は8と512）、`world.hierarchy`はscopeの最新観測をそのまま返す（`WB:docs/rpc-protocol.md:128,165-187`）。この上限は「scopeごと」に効く。
- ResoLoop既存コマンドの深さの既定値・上限（`src/RLoop.Cli/Program.cs`）:
  - `hierarchy`: 既定2、上限**64**（`Program.cs:519`）。
  - `find`: 既定8、上限**64**（`Program.cs:533`）。
  - `inspect`: 既定1、上限**64**（`Program.cs:553,563`）。
  - `item audit`: 固定**64**（`WorldService.cs:319`）。
  - `tool audit`: 既定16、上限**64**（`Program.cs:683`、`WorldService.cs:328`）。
  - `uix audit`: 既定6、上限**32**（`Program.cs:637-638`）で深さはWorkbenchと一致。ただし`max-slots`既定256・上限4096（`Program.cs:638`）はslot数の概念で、Workbenchの8,192とは独立のResoLoop側の制限。
  - `apply`/`plan`/`diff`の`PrepareAsync`は深さ64固定（`plan/workbench-design.md:91`、`WorldService.cs:911,931`）。
  - 既存コマンドはslot数そのものの上限を持たない（`uix`以外）ので、大きなRootを`hierarchy --under Root`等で観測するとWorkbenchの8,192件上限に当たり得る。
- **結論**: 上限64を使うコマンド（`hierarchy`、`find`、`inspect`、`item`、`tool`、`apply`/`plan`/`diff`）は、そのままではWorkbenchの32を超える。Workbenchへ切り替える場合は、(a) 64を32にクランプして「浅くなる」ことを明示するか、(b) 32を超えるSlotを`BACKEND_UNSUPPORTED`にするか、のどちらかの決定がW2-Bで要る（本表では判断しない）。

---

## 2. コマンドごとの判定

`--backend workbench`で直結（`--backend link`）と同じ結果を返せるかを、上記1節に基づいて判定する。「返せない」は、null埋めをせずに拒否すべき対象という意味。

| コマンド | 判定 | 欠けている値 / 理由 |
| --- | --- | --- |
| `status` / `ping` | **返せる**（実装済み） | `GetSessionInfoAsync`はW1で実装済み（`docs/dev/workbench-w1-w3-handoff.md:13`）。`BackendSupport`の表はまだ`planned-w2`のままなので、コマンドは接続前に拒否される（`src/RLoop.Cli/BackendSupport.cs:27-28`）。W2-Bで表を外すだけで有効化できる。 |
| `doctor` | **返せる** | `GetSessionInfoAsync`しか使わない（`Program.cs:395`）。`status`/`ping`と同様、実装済みだが表で塞がれている。 |
| `hierarchy` | **返せない**（`--include-components`使用時、または深さ>32、または`IsReferenceOnly`に依存する後続処理で） | transform・persistentは`SlotInfo`のトップレベルフィールドとして常に埋まらない。`--include-components`はComponentのMembersを埋める分だけ往復が増える（1.2節）。深さ既定2は問題ないが、`--depth`が33以上だと拒否が必要。 |
| `find` | **返せない**（`--exclude-reference-only`使用時、または深さ>32） | `IsReferenceOnly`が無いので`--exclude-reference-only`のフィルタが効かない。 |
| `observe`（`$member:key.Name`読み取り） | **返せる可能性が高い**（未確認） | `ObserveAsync`（`Observation.cs:10-37`）はtransform・persistentを使わず、`GetSessionInfoAsync`＋stable reference解決（`GetSlotAsync`/`GetComponentAsync`、Members）だけで組める。member値の取得は1.2節の手順（`reflection.component`＋`member.read`）に依存するので、対象memberの型がWorkbenchの`ObservedMember`表現でどこまで再現できるかは未確認。 |
| `inspect` | **返せない**（既定のtransform表示、`--exclude-reference-only`、深さ>32） | `SlotInfo`のPosition/Rotation/Scale/IsPersistentが常に欠ける。 |
| `type search` | **返せない** | 1.4節のとおりRPCが無い。 |
| `type describe` | 一部返せる／一部返せない | Component型は`reflection.component`でほぼ完全（1.3節前半参照するComponentTypeInfo相当は別途確認要、MemberDefinitionInfoは概ね対応）。列挙型のとき`EnumValues`/`IsFlags`が欠ける。`IsWorldElement`と（`type specialize`が使う）`GenericParameters`も欠ける。 |
| `validate`（`--strict`なし） | **返せる**（未確認、接続不要の可能性） | 非strictの`validate`はスキーマ検証のみで`client`を使わない（`Program.cs:151-157`、`ApplyDocumentValidator.ValidateAsync(document, cancellationToken:...)`のオーバーロード）。接続が要らないなら、backendの区別自体が無意味な可能性がある。要確認。 |
| `validate --strict` | **返せない** | `client`を渡す版（`WorldService.cs:332-335`）は型のReflectionを検証するため、`type describe`と同じ制約を継承する。 |
| `diff` / `plan` | **返せない** | `PlanApplyAsync`→`PrepareAsync`は深さ64固定でtransform・persistentを使う（`plan/workbench-design.md:91`）。設計記録5.3(0)により、この経路はW4の前提ゲート対象であり、W2では対応しない。 |
| `uix audit` | **返せない**（`IsReferenceOnly`、Component Membersを使うため） | 深さ・max-slotsの既定値自体はWorkbenchの上限内だが、`UixAuditService`は`IsReferenceOnly`（`UixAudit.cs:49`）と`ComponentSummary.Members`（`:77-91`）に依存する。 |
| `item audit` | **返せない** | 深さ64固定（32超）、かつサブツリー全体のComponent Membersを走査する（`ItemAudit.cs:25-63`）ため、1.2節のN+1コストも重なる。 |
| `tool audit` | **返せない** | `ToolAudit.cs:109-111`がPosition/Rotation/Scaleを直接使う。欠けたときに`?? identity`へ暗黙フォールバックする既存コードがあるため、Workbenchでは**誤った結果を返す**危険がある（null埋め禁止の原則に反する）。 |
| `capture`（`--frame`使用時） | **返せない** | `CanvasFraming.ObserveAsync`が祖先チェーンのPosition/Rotation/Scaleを必須にしている（`CanvasFraming.cs:44-45`）。`--camera`のみ（Apply文書のカメラbookmarkを使う経路）は、Slotのtransformを読まない場合は返せる可能性があるが未確認。 |

---

## 3. Workbenchに追加してほしいRPCの具体案（優先順）

feedbackへ提案する際の元案。優先順は、影響するコマンド数と「null埋め禁止」による機能損失の大きさで決めた。

1. **`world.observe`/`world.slot`/`world.hierarchy`の`SlotRecord`にtransform（position/rotation/scale）とpersistent、可能なら`isReferenceOnly`を追加する。**
   - 影響: `hierarchy`、`find --exclude-reference-only`、`inspect`、`item`、`tool`、`capture --frame`、`uix audit`のほぼ全部。design記録5.3(0)で「Workbench側の`feedback/`に提案してよい」と承認済み（`plan/workbench-design.md:227`、`plan/workbench-design.md:26`の承認点1）。
   - 備考: `mutation`側の`updateSlot`/`slotTransform`precondition（`WB:docs/rpc-protocol.md:255-256,267-272`）は既にtransformの概念を扱っているので、読み取り側に露出させる設計上の障害は無いはず（未確認）。
2. **Component型の名前・部分一致検索RPC（`reflection.search`のようなもの）。**
   - 影響: `type search`。代替手段が無く、`SearchComponentTypesAsync`は完全に空振りするしかない。
3. **列挙型の値一覧RPC（`reflection.enum`のようなもの。`typeName`→ `{name: value}` と `isFlags`）。**
   - 影響: `type describe`で列挙型のmemberを扱うとき。
4. **`world.observe`/`world.slot`のComponentに、member値をまとめて含めるオプション（`includeMembers`のような）。**
   - 影響: `hierarchy --include-components`、`item`、`uix audit`のコスト。無くても`reflection.component`＋`member.read`の組み合わせで値自体は取れるが、Slot数×Component数×Member数のRPC往復になる。W2-Bで許容できるコストかは実測が要る。
5. `TypeDefinitionInfo`に`isWorldElement`と、open genericの`genericParameters`（名前のリスト）を追加する。
   - 影響: `type describe`の一部フィールド、`type specialize`の入力検証。優先度は低い（値が無くても`type specialize`自体の計算はできる。表示用フィールドの欠落に留まる）。

---

## 4. 実装の方針案

- **Coreのモデル（`SlotInfo`/`ComponentInfo`/`TypeInfo`等）は変えない**。上記の欠落は「Workbenchが返せない」であって「ResoLoopの型が表現できない」ではない。モデルを変えると、直結経路（upstream寄りの`RLoop.ResoniteLink`、`WorldService.cs`、既存のstate/diff/validateの全消費者）に波及するため、design記録3節の「schema-v1を含むCoreのモデルは変更しない」方針と矛盾する。
- **`WorkbenchResoniteClient`側だけで対応する**。
  - `GetSlotAsync`/`GetComponentAsync`は、1.1〜1.2節の手順（`world.slot`/`world.observe`→`reflection.component`→`member.read`の合成）で埋められる値だけを埋め、埋められない値（Position/Rotation/Scale/IsPersistent/IsReferenceOnly/EnumValues/IsFlags/IsWorldElement/GenericParameters）は**nullのまま**返す。null埋めをして「値が0」であるかのような偽の完全性を作らない（design記録5.3(0)の方針どおり）。
  - コマンド側の対応可否は、`BackendSupport`ではなく、**値が実際に必要になった場所で**`WORKBENCH_UNSUPPORTED_FIELD`（仮のコード名。設計は未確定）のような明示エラーにする案と、コマンド単位で`BackendSupport`に残す案がある。本表の2節の判定は後者（コマンド単位）を前提にしている。値単位のエラーの方が`hierarchy`のように「使わない限り困らない」コマンドを部分的に許可できるが、W2-Aの範囲では判断しない（W2-Bの設計課題として残す）。
  - upstreamのファイルへの影響は無い（`RLoop.ResoniteLink/**`、`RLoop.Core`の直結消費者は変更しない）。
- **`type search`は当面`BACKEND_UNSUPPORTED`のまま**。3節1のRPCが無い限り代替不可。

---

## 未確認の事項（この文書の範囲での既知の限界）

- `ObservedField.ValueJson`の値表現が、ResoniteLinkの`BoxedValue`シリアライズ（`ResoniteLinkClientAdapter.cs:670-671`）と数値・文字列の整形（NaN/Infinity等）まで一致するか。
- `validate`（非strict）が本当に接続を要らないか、要るなら何を使うか。
- `capture --camera`（`--frame`を使わない経路）がSlotのtransformを読むかどうか。
- `member.read`のレイテンシと、1コンポーネントあたりのmember数の実測値（3節4の優先度判断に影響する）。
- resonite-workbench側のHEADがdesign記録の照合HEAD（`d549280`）から変わっていないか（今回はコード確認のみで、gitのHEAD一致は確認していない）。
