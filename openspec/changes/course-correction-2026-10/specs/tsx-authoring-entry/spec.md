# Spec Delta

## Purpose

TSX で制作物を書く人が使う import の入口と、Slot・Component の key を省ける条件を定める。入口と規則を 1 つにそろえ、build の target によって何が変わるかを 1 つの表で説明できるようにする。

## ADDED Requirements

### Requirement: import の入口は resoloop-jsx の 1 つとする
`resoloop-jsx` から import した Slot と Component は、`key` を書かなくても TypeScript の型検査を通らなければならない（MUST）。key を省いてよいかどうかは、build の target が決める。

#### Scenario: key を省いた TSX の型検査
- **WHEN** 書く人が `resoloop-jsx` から import し、`key` の無い Slot を書いて型検査を実行する
- **THEN** `key` が無いことによる型エラーは出ない

### Requirement: creation の target では key を省ける
`build --target creation` は、`key` を省いた Slot・Component と、同じ親の下の同じ名前の Slot を受け入れ、`creation-1` の定義を出力 SHALL する。

#### Scenario: key を省いた定義を build する
- **WHEN** `resoloop-jsx` から import し、`key` を省いた TSX を `build entry.tsx -o out.json --target creation` で build する
- **THEN** 終了 code 0 で `creation-1` の定義が出力され、`apply --mode regenerate` がその定義を受け付ける

#### Scenario: 同じ名前の兄弟
- **WHEN** 同じ親の下に、同じ名前の Slot を 2 つ書いた TSX を `--target creation` で build する
- **THEN** build は成功し、2 つの Slot が定義の中で宣言の順のまま区別される

### Requirement: 通常の target では key が無ければ build で止まる
target を指定しない build は、`key` の無い Slot・Component を見つけたら `EXPLICIT_KEY_REQUIRED` で止まらなければならない（MUST）。エラーは source の位置（ファイル・行・列）を示し、出力ファイルは書かない。

#### Scenario: 通常の build で key を省いた
- **WHEN** `key` を省いた TSX を、`--target` も `--draft` も付けずに build する
- **THEN** 0 でない終了 code と `EXPLICIT_KEY_REQUIRED`、該当の要素のファイル・行・列が返り、出力ファイルは作られない

### Requirement: resoloop-jsx/draft は互換の別名とする
`resoloop-jsx/draft` からの import は、引き続き使えなければならない（MUST）。同じ TSX は、import 元が `resoloop-jsx` でも `resoloop-jsx/draft` でも、同じ option の下で byte 単位に同じ出力になる。

#### Scenario: import 元だけを変える
- **WHEN** 同じ TSX の import 元を `resoloop-jsx/draft` から `resoloop-jsx` へ変え、同じ option で build する
- **THEN** 2 つの出力ファイルは byte 単位で一致する

### Requirement: --draft の出力は apply できない
`build --draft` は、欠けた key を生成して下書きを出力 SHALL する。その出力を `validate` または `apply` に渡すと、`APPLY_DRAFT_KEY_UNSTABLE` で止まらなければならない（MUST）。

#### Scenario: 下書きを apply しようとする
- **WHEN** `--draft` で build した出力を `apply` に渡す
- **THEN** 何も書き込まれず、`APPLY_DRAFT_KEY_UNSTABLE` が返る

### Requirement: 併用できない option は build の前に拒否する
`--target creation` と、`--draft`・`--bundle`・`--catalog`・`--build-id` のどれかを同時に指定した build は、評価を始める前に使い方の誤りとして止まらなければならない（MUST）。

#### Scenario: creation と draft を同時に指定した
- **WHEN** `build entry.tsx -o out.json --target creation --draft` を実行する
- **THEN** 使い方の誤りとして 0 でない終了 code が返り、出力ファイルは作られない

### Requirement: build は決定的である
同じ入力（TSX、import する file、option）に対する build は、何度実行しても byte 単位で同じ出力を返さなければならない（MUST）。key を省いた要素に割り当てる識別子も、実行ごとに変わってはならない。

#### Scenario: 2 回 build する
- **WHEN** `key` を省いた要素と同じ名前の兄弟を含む TSX を、同じ option で 2 回 build する
- **THEN** 2 つの出力ファイルは byte 単位で一致する

### Requirement: key の規則は 1 つの表で説明する
target ごとの key の規則は、`tools/resoloop-jsx/README.md` の 1 つの表で説明しなければならない（MUST）。`README.md`・`README_JA.md`・`README-DETAILS.md`・配布する Skill は、同じ規則を別の言葉で繰り返さず、その表を参照する。

#### Scenario: 規則を調べる
- **WHEN** 書く人が「key を省いてよいか」を調べる
- **THEN** `tools/resoloop-jsx/README.md` の表の 1 行から、自分の target（通常・`--draft`・`--target creation`）での答えと、apply できるかどうかが分かる
