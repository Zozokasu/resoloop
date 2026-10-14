# Spec Delta

## Purpose

定義から root ごと作り直す経路（`apply --mode regenerate`）について、TSX から定義を出す入口、入れ替えの順、消す前の所有の確認を定める。新しい制作の機能は、この経路へ足す。

## ADDED Requirements

### Requirement: TSX から作り直しの定義を出せる
`build --target creation` は、TSX から `creation-1` の定義を出力 SHALL する。`creationId` は、entry の `ownership.key`、無ければ root の Slot に明示した `key` とする。root を `Scope` で包んでいても、`creationId` には Scope を適用する前の `key` を使う。どちらも無い、または空白だけの場合は、build を止める。

#### Scenario: root を Scope で包んだ
- **WHEN** `ownership.key` を書かず、`key="room"` の root を `Scope` で包んだ TSX を `--target creation` で build する
- **THEN** 出力の `creationId` は `room` になり、その定義は `validate` を通る

#### Scenario: ownership.key から決まる
- **WHEN** entry に `ownership.key` を書いた TSX を `--target creation` で build する
- **THEN** 出力の `creationId` は、その `ownership.key` と同じ値になる

#### Scenario: creationId を決められない
- **WHEN** `ownership.key` も、root の Slot の `key` も書いていない TSX を `--target creation` で build する
- **THEN** build は 0 でない終了 code で止まり、出力ファイルは作られない

### Requirement: 入れ替えの順は delete-first だけを受け付ける
`apply --mode regenerate` は、`--replace-order delete-first` だけを受け付け SHALL する。別の値は、実機へ何も送る前に拒否する。無効な新しい root を先に作る順は、無効な Slot の下の ProtoFlux の実機の確認が済むまで足さない。

#### Scenario: 別の順を指定した
- **WHEN** `apply out.json --mode regenerate --replace-order stage-inactive --yes` を実行する
- **THEN** 実機への書込みは 1 件も送られず、受け付ける値が `delete-first` だけであることを示すエラーが返る

### Requirement: 所有を確かめてから古い root を消す
作り直しは、記録した root の正確な ID について、root に書いた所有の印が記録と一致し、接続先が記録した session と同じだと確かめた場合だけ、古い root を消さなければならない（MUST）。所有の確認から削除までの間に、実機の読取りを挟んではならない（MUST NOT）。

#### Scenario: 印が一致しない
- **WHEN** 記録した root の所有の印が、実機で別の値に変わっている状態で作り直しを実行する
- **THEN** 削除は 1 件も送られず、所有を確かめられなかったことを示すエラーが返る

#### Scenario: 記録した root が正確に不在
- **WHEN** 記録した session と同じ session で、記録した root の ID が存在しないと観測できる
- **THEN** 何も消さずに初回の作成として進み、警告 `CREATION_PREVIOUS_ROOT_ABSENT` を返す

### Requirement: 作り直しの後は定義の値になる
作り直しが成功した後、定義に書いた Slot・Component・Field の値は、実機で定義の値と一致しなければならない（MUST）。作り直しの前に実機で変えた値を、次の定義へ持ち越してはならない（MUST NOT）。

#### Scenario: 実機で値を変えた後に作り直す
- **WHEN** 作成の後に実機で Field の値を手で変え、同じ定義で作り直しを実行する
- **THEN** 作り直しの後の Field の値は、定義に書いた値になる

### Requirement: Resonite が自動で足す Component を受け入れる
作成の後の確認では、Slot の数は定義と一致し、Component の数は定義以上であれば成功 SHALL とする。多かった数は、結果の `engineAddedComponents` で返す。

#### Scenario: UIX の Component を含む定義
- **WHEN** `RectTransform` を書かずに UIX の Component を持つ定義を作り直す
- **THEN** 作り直しは成功し、結果の `engineAddedComponents` に、Resonite が足した Component の数が入る

### Requirement: 途中で止まった作り直しは回復のコマンドで閉じる
作り直しが途中で止まった場合、次の作り直しは始めず、`creation recover` の `rollback`・`finish`・`--discard-pending` のどれかで閉じるまで止まり続けなければならない（MUST）。結果の分からない書込みを、確かめずに送り直してはならない（MUST NOT）。

#### Scenario: 期限切れで止まった後に作り直す
- **WHEN** 作り直しがコマンドの期限で止まり、回復のコマンドを実行しないまま、もう一度作り直しを実行する
- **THEN** 書込みは 1 件も送られず、回復のコマンドの案内が返る
