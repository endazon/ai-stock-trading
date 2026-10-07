---
title: 計画 ID レンジの宣言行が消えたとき、同じ節の書式例を拾って黙ってレンジが縮むのを塞ぐ
type: spec
status: accepted
related_ids: [NFR, ADR-0029, IADR-0320, IADR-0504]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0029_impl-docs-restructure.md (決定 2: planning 非依存。レンジは本リポの追跡ファイルに宣言する)
---

# 仕様書: 計画 ID レンジの宣言行が消えたとき、同じ節の書式例を拾って黙ってレンジが縮むのを塞ぐ（#1233）

## 起点となる計画書（トレーサビリティ）

- 起点: 無採番 NFR（メタ作業。トレーサビリティ検査の一次情報の読み取り）
- 関連 ADR: 計画 ADR-0029 決定 2／IADR-0320（宣言側は本番の抽出関数を呼ぶ）／IADR-0504（宣言不読は `error`・exit 1）
- 起票: #1233（#1231〔#1208〕の独立監査の 🟢 を範囲外として切り出したもの）

## 目的・背景

`readPlanAdrRange()`（`scripts/lib/plan-ranges.js`）は節内の**最初の** `` `ADR-n..m` `` を拾う。
`.claude/rules/traceability.repo.md` §起点 ID の種別（固有）には宣言（`` `ADR-0001..0055` ``）の後に、
検査器の説明として書式例（`` `ADR-0001..0037` `` の形）が同じ形で書かれていた。宣言行だけが消えると書式例を拾い、
`{from:1,to:37}` を返して例外にならない（#1231 の監査の実測）。#1208 で入れた「宣言不読 → `error`・exit 1」は
書式例も同時に消えたときにしか発火しない。

## 母集合（規則 9・10。着手前に引いた）

### 当該節を読むパーサ（`git grep -ln "plan-ranges\|readPlanIds\|readPlanAdrRange" -- scripts .github`）

| 読み手 | 経路 | 本件の影響 |
| --- | --- | --- |
| `scripts/lib/plan-ranges.js` `readPlanAdrRange()` | ADR。**1 個目の一致** | 宣言消失で書式例 0037 を拾う（本件） |
| `scripts/check-test-traceability.js` `parsePlanRanges()`（`readPlanIds()`） | FR/UC/SC。**後勝ち**で上書き | 同型の潜在欠陥: FR の書式例 `` `FR-01..21` `` が宣言の**後**にあるため、宣言だけを `..22` へ前進させると書式例の `21` へ黙って縮む。宣言が消えても書式例で通る |
| `scripts/check-trace-blocks.js` | 上 2 本を呼ぶ | 上 2 本の修正で追随 |
| `scripts/gen-knowledge-graph.js` | 上 2 本を呼ぶ | 同上 |
| `scripts/check-commit-messages.js` | `readPlanIds()`／`readPlanAdrRange()` | 同上（コメント中の `` `ADR-0001..0029` の形 `` は別ファイルで節外。対象外） |
| `scripts/check-planning-adr-range.js` | `readDeclaredRanges()` が上 2 本を呼ぶ | 例外は既存の `error`・exit 1 経路へ乗る（実走で確認） |
| `scripts/check-workflow-job-refs.js` | コメントで `readPlanIds()` に言及するだけ | 対象外 |

### 節内のバッククォート囲みのレンジ形トークン（`planRangeSection()` の出力を走査）

| 節内の行 | トークン | 役割 |
| --- | --- | --- |
| 宣言（「レンジは」の次行） | `` `FR-01..21` `` `` `UC-01..07` `` `` `SC-01..04` `` `` `ADR-0001..0055` `` | 宣言 |
| 「この節は機械の単一情報源である」の項 | `` `FR-01..21` `` | 書式例（宣言と同じ形） |
| 同項 | `` `ADR-0001..0037` `` | 書式例（宣言と同じ形・古い値） |

- 他の箇所（`git grep -n -E "ADR-0001\.\.0037|FR-01\.\.21\` の形"`、`.ai-context/specs/` を除く）: `IADR-0320` の「節内 2 箇所」は当時の記録で書き換えない。
  `scripts/check-planning-adr-range.js` の試験データ（`| ADR | \`ADR-0001..0037\` |`）は節外で対象外。
- この変更で新たに誤りになる自分の記述: `scripts/README.md` の `lib/plan-ranges.js` 行「同ファイル自体は変更しない」は
  `check-test-traceability.js` の `parsePlanRanges()` を直すため注記を足した。`check-planning-adr-range.js` 行の
  「宣言が読めない（節が無い・書式が崩れた）」に「同じ種別のレンジ表記が 2 個ある」を足した。

## 対象範囲

- 対象:
  - `scripts/lib/plan-ranges.js`: 節内の ADR レンジ表記が 2 個以上なら例外（値が同じでも）
  - `scripts/check-test-traceability.js` `parsePlanRanges()`: 同じ種別の FR/UC/SC トークンが 2 個以上なら例外
  - `.claude/rules/traceability.repo.md`: 書式例 2 箇所をレンジとして読めない形（`` `FR-01..NN` `` / `` `ADR-0001..NNNN` ``）へ。**バイト数は不変**（10,667 → 10,667）
  - `scripts/check-trace-blocks.js` 自己試験・`scripts/scripts.repo.test.js`: 回帰試験
  - `scripts/README.md`: 当該 2 行
- 対象外:
  - 宣言行の位置を特定するパーサ（「レンジは」の直後など）。地の文の言い回しへ結合し、文言の改稿で壊れる。
    「ちょうど 1 個」の契約と書式例の書き換えの併用で、宣言行の消失は例外になる
  - IADR の新設（既存の fail-loud 契約〔IADR-0504 決定 3〕の取りこぼしを塞ぐだけで、新しい判断を持たない）

## 設計

1. **宣言は種別ごとにちょうど 1 個**を契約にする。2 個以上は値が同じでも例外（同じ値を許すと、書式例が宣言と同値の間は
   宣言行の消失が見えず、宣言を前進させた瞬間に後勝ち／先勝ちのどちらかで黙って誤る）。
2. 書式例はレンジとして読めない形で書く（`NN` / `NNNN`。正規表現 `\d+` に掛からない）。
3. 二重の守り: 書式例が旧来の形へ戻されても、宣言が残っていれば 2 個で例外。宣言が消えても書式例が読めない形なら 0 個で例外。
   両方が同時に起きたときだけ通る（単一の誤りでは通らない）。

## 受け入れ基準

1. 実ファイルから宣言トークンだけを除いた写しで `readPlanAdrRange()`・`readPlanIds()` が例外になる（縮まない）
2. 正常な規約（宣言 1 個＋読めない形の書式例）は宣言の値を返す（ADR `{1,55}`、FR/UC/SC 32 件）
3. 宣言が 2 個ある規約（値が違う・同じ）は例外
4. `check-planning-adr-range.js` は宣言が 2 個ある規約で `status: error`・exit 1
5. 既存の検査器が実ファイルで通る。`.claude/rules/*.md` のバイト数を増やさない

## テスト方針

- `check-trace-blocks.js --self-test`: ADR の 3 形（宣言消失→例外／正常→`{1,55}`／2 個→例外）
- `scripts.repo.test.js` #1233: 実ファイルの宣言トークン除去（受け入れ基準 1）、正常（2）、ADR 2 個・FR 同値 2 個（3）、実ファイルの各種別トークンが 1 個ずつ
- 実走: `check-planning-adr-range.js --rules <宣言 2 個の写し>` → `error`・exit 1（受け入れ基準 4）

## 計画書との差異

なし。

## 未決事項

なし。
