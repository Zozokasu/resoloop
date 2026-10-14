# Roadmap

状態が変わったら、その作業の commit と同じ時に行を直す。

全体の順序と、項目ごとの状態・根拠・担当する change の正本。最初の内容は `openspec/changes/course-correction-2026-10/design.md` の対応表を写したもの（2026-10-13 の時点）。「plan/」は旧計画の S0〜S5、「項目」は Notion のロードマップの番号、「Better」は BetterResoloop の B1〜B6。未検証の ID（U01 など）は `openspec/unverified.md` を見る。

## 対応表

| ID | 内容 | plan/ | 項目 | Better | 状態 | 根拠 | 担当する change |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R01 | TSX・JSON の宣言を反映する | S0・S1 | 1 | — | 完了 | main `69a386c` | — |
| R02 | 競合の検出・失敗の照合・削除 | S3 | 2 | — | 完了（故障の場合は未検証 U14〜U17） | main `94c12ed` | course-correction-2026-10（未検証だけ） |
| R03 | 型情報で TSX を検査する | S2 | 3 | — | 完了（代表例） | `5edafc0`、`c9fd625` | — |
| R04 | Slot・Component・Field を ID で書く | — | 4 | — | 完了（代表例） | `5edafc0` | — |
| R05 | Object を定義して使う | S4-3 | 5 | — | 実験（単位 01〜20）。単位 21 と型付きの参照の文書は保留。`handle`・`ref.export` は R13 で置き換える見込み | `2993dab` | b3-objects-parent-variables |
| R06 | ProtoGraph の配置を apply と同じ確認の下で行う | S4-1・S4-2 | 9 | — | 完了（代表例） | `5edafc0` | — |
| R07 | 変数と Field をつなぐ | S4-4 | 8 | B1 | 実験（bool だけ） | `e00ce41` | b3-objects-parent-variables |
| R08 | Slot の有効・無効を書く | — | — | G1 | 完了 | `e00ce41` | — |
| R09 | root を作り直す apply と回復 | — | — | B2a・B2b | 実験（手書きの JSON、delete-first） | `e00ce41` | — |
| R10 | 同じ session の ID での照合 | — | — | — | 完了。重いレビューの指摘（P1 1 件、P2 2 件）は修正して merge 済み。実機は確認済み（U01、2026-10-14 の L4） | `03479cf`、修正の merge `b88ed8a`、`better-impl-b2c-review-report.md`、`cc-live-l4-report.md` | course-correction-2026-10 |
| R11 | TSX から作り直しの定義を出す | — | — | B2c-1 | 完了。重いレビューの指摘（P2 1 件）は修正して merge 済み。実機は確認済み（U02〜U05、2026-10-14 の L4。TSX の import 元は `resoloop-jsx/draft`） | merge `b88ed8a`（branch は `ba57564`）、`better-impl-b2c-review-report.md`、`cc-live-l4-report.md` | course-correction-2026-10 |
| R12 | Workbench なしで制作の経路をそろえる | S5 | 14 | — | 未着手 | — | course-correction-2026-10 |
| R13 | Object と親の変数、入れ子、配置口 | — | 6 | B3 | 未着手（4 点は確定） | `course-correction-2026-10` の `design.md` の決定 6 | b3-objects-parent-variables |
| R14 | 止めない警告を構造化して返す | — | — | B3 の前提 | 未着手 | — | structured-warnings |
| R15 | 無効な新しい root を先に作る入れ替えの順 | — | — | B2c-2 | 待ち（U08 の後） | `course-correction-2026-10` の `design.md` の決定 7 | stage-inactive-replace-order |
| R16 | 作り直しで Script・Asset・Flux を扱う | — | 7・10 | B4 | 未着手 | — | creation-script-asset-flux |
| R17 | UIX を部品で組み立てる | — | 11 | — | 未着手 | — | 未定 |
| R18 | Object の単位で更新する | — | 12 | B5 | 未着手 | — | object-scoped-regenerate |
| R19 | 作り直しを既定にする。通常の apply の扱いを決め直す | — | — | B6 | 未着手 | — | regenerate-default |
| R20 | 1 つの作品を通しで作り、編集を続ける | S4 の出口 | 13 | — | 未着手 | — | 未定 |
| R21 | Resonite → 定義の別の経路 | — | — | — | 形は未定（`/ccg:spec-research` から） | ユーザーの見解 2026-10-13 | 未定 |
| R22 | 自動で足される Component を Reflection などから引く | — | — | — | 時期は未定 | ユーザーの指示 2026-10-12 | 未定 |
| R23 | `--url auto` の探索（1 回に約 12 秒）を速くする | — | — | — | 未着手 | `better-handoff-2026-10-13.md` §7 | 未定 |

## 後続の change

後続の change は、R14、R13、R15、R16、R18、R19 の順に進める。R15 は U08 が済んだ時点で、順を前へ動かしてよい。

名前は予定で、それぞれ `/ccg:spec-research` から始める。

1. `structured-warnings`: 止めない警告を、build と validate の結果に構造化して返す。U22 の小さい修正を含める。
2. `b3-objects-parent-variables`: `course-correction-2026-10` の `design.md` の決定 6 の上に、Object と親の変数、入れ子、配置口。`handle`・`ref.export` の置き換えと、単位 21 をここで扱う。
3. `stage-inactive-replace-order`: U08 が済んだ後。U09 を扱うかも、ここで決める。
4. `creation-script-asset-flux`: 作り直しで Script・Asset・Flux を扱う。
5. `object-scoped-regenerate`: Object の単位の更新。
6. `regenerate-default`: 作り直しを既定にし、通常の `apply` の扱いを決め直す。
