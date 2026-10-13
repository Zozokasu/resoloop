# Spec Delta

## Purpose

CLI が Resonite へつなぐ backend を直結（ResoniteLink）の 1 つに定め、除いた Workbench backend を指す古い指定が、黙って別の意味に変わらないようにする。

## ADDED Requirements

### Requirement: backend は link だけとする
CLI は、すべてのコマンドを直結（ResoniteLink）の backend で実行 SHALL する。backend の指定（`--backend`、環境変数 `RESOLOOP_BACKEND`、設定の `backend`）を省いた場合と、`link` を指定した場合の挙動は同じでなければならない（MUST）。

#### Scenario: 指定を省く
- **WHEN** backend の指定を、option・環境変数・設定のどこにも書かずにコマンドを実行する
- **THEN** 直結の backend で実行される

#### Scenario: link を明示する
- **WHEN** `--backend link` を付けてコマンドを実行する
- **THEN** 指定を省いた場合と同じ結果になる

### Requirement: 除いた backend の指定は BACKEND_REMOVED で止める
backend の値が `workbench` の場合（option・環境変数・設定のどれから来ても）、CLI は接続を試みる前に、error code `BACKEND_REMOVED` と終了 code 2 で止まらなければならない（MUST）。メッセージは、Workbench backend を除いたことと、指定を消すか `link` にすることを伝える。

#### Scenario: option で workbench を指定した
- **WHEN** `--backend workbench status` を実行する
- **THEN** 接続は試みられず、`BACKEND_REMOVED` と終了 code 2 が返る

#### Scenario: 設定ファイルに古い指定が残っている
- **WHEN** 設定ファイルの `backend` が `workbench` のまま、`apply` を実行する
- **THEN** 実機へ何も送られず、`BACKEND_REMOVED` と終了 code 2 が返る

#### Scenario: 知らない値
- **WHEN** `--backend foo` を実行する
- **THEN** これまでどおり `INVALID_BACKEND` と終了 code 2 が返る

### Requirement: wb コマンドは BACKEND_REMOVED で止める
`wb` で始まるコマンドは、下位のコマンドや option にかかわらず、`BACKEND_REMOVED` と終了 code 2 で止まらなければならない（MUST）。知らないコマンドとして扱ってはならない（MUST NOT）。

#### Scenario: wb status を実行する
- **WHEN** `wb status` を実行する
- **THEN** 接続は試みられず、`BACKEND_REMOVED` と終了 code 2 が返る

### Requirement: Workbench の pipe の option は BACKEND_REMOVED で止める
option `--workbench-pipe` を指定したコマンドは、`BACKEND_REMOVED` と終了 code 2 で止まらなければならない（MUST）。環境変数 `RESOLOOP_WORKBENCH_PIPE` と設定の `workbenchPipe` だけが残っている場合は、読まずに無視する。

#### Scenario: pipe の option を付けた
- **WHEN** `--workbench-pipe SomePipe status` を実行する
- **THEN** `BACKEND_REMOVED` と終了 code 2 が返る

#### Scenario: 環境変数だけが残っている
- **WHEN** 環境変数 `RESOLOOP_WORKBENCH_PIPE` が設定されたまま、backend の指定なしで `status` を実行する
- **THEN** 直結の backend で実行され、環境変数は結果に影響しない

### Requirement: help と文書に Workbench の使い方を残さない
`--help` の出力、`README.md`、`README_JA.md`、`README-DETAILS.md`、`tools/resoloop-jsx/README.md`、配布する Skill は、Workbench backend の使い方を説明してはならない（MUST NOT）。除いたことと、古い指定が `BACKEND_REMOVED` で止まることは、`README-DETAILS.md` の 1 か所に書く。

#### Scenario: help を読む
- **WHEN** `--help` を実行する
- **THEN** 出力に `--workbench-pipe`、`wb`、backend の値としての `workbench` は現れない
