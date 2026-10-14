# Spec Delta

## Purpose

差分で合わせる通常の `apply`・`plan`（`--mode regenerate` を付けない経路）の、凍結した契約を定める。この経路は既存の制作物のために残し、不具合と安全の修正だけを受ける。新しい制作の機能は、作り直しの経路へ足す。

## ADDED Requirements

### Requirement: 通常の apply は凍結する
通常の `apply` と `plan` が受け付ける option の一覧と、返しうる error code の一覧は、固定 SHALL する。一覧を変える変更は「凍結の例外」として扱い、ユーザーの承認が無ければ入れてはならない（MUST NOT）。

#### Scenario: option を足そうとする
- **WHEN** 変更が、通常の `apply` に新しい option を足す
- **THEN** 固定した一覧と実装を比べる test が失敗し、一覧の更新とユーザーの承認が無い限り受け入れられない

#### Scenario: 不具合を直す
- **WHEN** 変更が、option と error code の一覧を変えずに、通常の `apply` の不具合を直す
- **THEN** 回帰を確かめる test を足した上で、凍結の例外としてではなく受け入れられる

### Requirement: 既存の文書と state を読み続ける
通常の `apply` は、これまで受け付けてきた定義の文書と、保存済みの state（v1・v2・v3）を、変わらず読めなければならない（MUST）。

#### Scenario: 以前の state で再 apply する
- **WHEN** この change の前の版が書いた state v3 と、同じ定義で `plan` を実行する
- **THEN** 変更 0 件の計画が返り、state の読込みのエラーは出ない

### Requirement: 記録した ID での照合は同じ session の中だけ
同じ Slot・同じ型の Component の候補を絞れないとき、記録した ID で決めてよいのは、(a) state を書いたのが同じ session で最後まで成功した `apply`、(b) 記録した ID が候補の中にある、(c) 同じ Slot の中で記録した ID に重複が無い、(d) state の key と ID の対応が、その `apply` の保存したものから変わっていない、のすべてを満たす場合だけ SHALL とする。途中で止まった `apply` の後と、`--prune` の削除の対象の照合では、記録した ID で決めてはならない（MUST NOT）。

#### Scenario: 同じ session で再 apply する
- **WHEN** 同じ Slot に同じ型の Component を 2 つ持つ定義を `apply` し、同じ session のまま、同じ定義でもう一度 `apply` する
- **THEN** 2 回目は記録した ID で 2 つを見分け、変更 0 件で成功する

#### Scenario: session が変わった
- **WHEN** 上の 1 回目の後に Resonite を開き直し、新しい session で同じ定義を `apply` する
- **THEN** `STABLE_COMPONENT_AMBIGUOUS` で止まり、何も書き込まれない

#### Scenario: session を確かめられない接続
- **WHEN** `--url ws://…` の直接の指定で接続し、同じ定義を 2 回 `apply` する
- **THEN** 2 回目は `STABLE_COMPONENT_AMBIGUOUS` で止まる

#### Scenario: 途中で失敗した後に再 apply する
- **WHEN** 上の 1 回目の後、値を変えた定義の `apply` が最初の書込みで失敗し、同じ session のまま、もう一度 `apply` する
- **THEN** `STABLE_COMPONENT_AMBIGUOUS` で止まり、何も書き込まれない

#### Scenario: state の ID を手で入れ替えた
- **WHEN** 上の 1 回目の後、state の 2 つの Component の ID を手で入れ替え、同じ session で `apply` する
- **THEN** `STABLE_COMPONENT_AMBIGUOUS` で止まり、何も書き込まれない

#### Scenario: 同じ型の片方を prune で消そうとする
- **WHEN** 上の 1 回目の後、2 つのうち片方を定義から外し、同じ session で `apply --prune --yes` を実行する
- **THEN** `STABLE_COMPONENT_AMBIGUOUS` で止まり、何も削除されない

### Requirement: 結果の分からない書込みが残っていれば止まる
前回の `apply` で結果を確かめられなかった書込みが記録に残っている場合、通常の `apply` は、新しい書込みを 1 件も送る前に止まらなければならない（MUST）。同じ書込みを確かめずに送り直してはならない（MUST NOT）。

#### Scenario: 保留が残っている
- **WHEN** 結果の分からない書込みが記録に残った状態で `apply` を実行する
- **THEN** 書込みは 1 件も送られず、保留があることと、次に取れる操作が返る

### Requirement: 削除は所有を確かめた正確な ID だけ
通常の `apply` が Slot または Component を削除するのは、その正確な ID が、この定義の管理下にあると確かめられた場合だけ SHALL とする。確かめられない場合は、何も削除せずに止まる。

#### Scenario: 記録した ID の先が別のものに変わっていた
- **WHEN** 記録した ID の Slot が、実機で別の名前と別の中身に変わっている状態で、削除を含む `apply` を実行する
- **THEN** その Slot は削除されず、所有を確かめられなかったことを示すエラーが返る

### Requirement: 作り直しの定義は通常の apply で受け付けない
`creation-1` の定義を `--mode regenerate` なしで `apply` または `plan` に渡した場合、通常の経路は処理せず、mode が合わないことを示して止まらなければならない（MUST）。

#### Scenario: mode を付け忘れた
- **WHEN** `creation-1` の定義を `apply out.json` で実行する
- **THEN** 何も書き込まれず、`--mode regenerate` が要ることを示すエラーが返る
