# Spec Delta

## Purpose

実機が要る test を実行しなかった場合に、成功ではなく skip として報告されるようにする。受け入れの報告で、実行して通ったものと、実行していないものを取り違えないためのもの。

## ADDED Requirements

### Requirement: 実行しなかった実機の test は skip として報告する
`tests/RLoop.IntegrationTests` の、実機や外部の道具が要る test は、条件（環境変数 `RESOLOOP_RUN_INTEGRATION` が `1` であることなど）を満たさない場合、test runner に skip として報告されなければならない（MUST）。本体を実行せずに成功として返してはならない（MUST NOT）。

#### Scenario: 環境変数なしで実行する
- **WHEN** `RESOLOOP_RUN_INTEGRATION` を設定せずに `dotnet test tests/RLoop.IntegrationTests` を実行する
- **THEN** 実機が要る test はすべて skip として数えられ、失敗は 0 件になる

#### Scenario: skip の理由が分かる
- **WHEN** 上の実行の結果を読む
- **THEN** skip された各 test に、満たさなかった条件（例: `RESOLOOP_RUN_INTEGRATION is not 1`）が理由として付いている

#### Scenario: 追加の条件を満たさない
- **WHEN** `RESOLOOP_RUN_INTEGRATION=1` だが、Blender の場所など、その test が要る別の条件を満たさない状態で実行する
- **THEN** その test は skip として数えられ、理由に満たさなかった条件が付く

### Requirement: 実機の要らない test は条件なしで実行する
`tests/RLoop.IntegrationTests` の中の、実機も外部の道具も要らない test は、環境変数の設定にかかわらず実行されなければならない（MUST）。

#### Scenario: offline の test
- **WHEN** `RESOLOOP_RUN_INTEGRATION` を設定せずに test を実行する
- **THEN** session の lock の隔離を確かめる 2 件は実行され、成功として数えられる

### Requirement: 受け入れの報告は 4 つを分ける
受け入れの報告は、成功・失敗・skip・未検証の件数を分けて書かなければならない（MUST）。skip と未検証を、成功の件数に含めてはならない（MUST NOT）。

#### Scenario: offline の受け入れを報告する
- **WHEN** 担当が、実機なしの検証の結果を報告する
- **THEN** 報告には、Integration test の成功の件数と skip の件数が別々に書かれ、実機で確かめていない点は未検証として挙がる
