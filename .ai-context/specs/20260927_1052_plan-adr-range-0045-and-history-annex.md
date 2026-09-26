---
title: 計画 ADR レンジを ADR-0001..0045 へ引き直し、レンジ節の日付つき履歴を別紙へ移して読み込み予算を 90% 未満へ戻す
type: spec
status: accepted
related_ids: [NFR, ADR-0029, ADR-0044, ADR-0045, IADR-0207, IADR-0320]
author: claude (Claude Code)
created: 2026-09-27
updated: 2026-09-27
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0044_watchlist-in-decision-prompt-and-stage0-asof.md (planning#673)
  - planning:projects/ai-stock-trading/07_adr/ADR-0045_reservation-release-criterion-per-trading-env.md (planning#676)
---

# 仕様書: 計画 ADR レンジの 0045 への引き直しと、レンジ履歴の別紙化（#1052）

## 起点となる計画書（トレーサビリティ）

- 計画 ADR-0044（planning#673。planning `33490bf`）・ADR-0045（planning#676。planning `a4a4268`）の追加
- 起票: #1052

## 目的・背景

計画リポジトリに ADR-0044 / ADR-0045 が加わったが、`.claude/rules/traceability.repo.md`「起点 ID の種別（固有）」節の宣言は
`ADR-0001..0043` のままで、両 ADR を起点 ID に引くと `check-commit-messages.js` / `check-trace-blocks.js` が「実在しない」で落ちる。

前回（#1031）までの作法どおり宣言と日付つき履歴を足すと、毎セッション必読の規約（Claude の集合）が
46,072 → 46,661 バイト（予算 51,200 の 89.98% → 91.1%）になり、`check-reading-budget.js` の 90% の warn を越える。
引き直しのたびに履歴が伸びる構造そのものが原因なので、同じ PR で履歴を別紙へ出す。

## 対象範囲

- `.claude/rules/traceability.repo.md`: 宣言を `ADR-0001..0045` にし、節には宣言（FR/UC/SC/ADR のレンジ・欠番なし・
  転記元は `gen-plan-ranges.js --check` の実測）と別紙への 1 行だけを残す
- `.ai-context/annex/plan-id-range-history-annex.md`（新設）: 節から外した日付つき履歴・実測の記録を**原文のまま**置く
- `.ai-context/README.md`: 構成図に `annex/` を足す
- 対象外: 検査器のコード（節の見出し・レンジの書式は不変なので触らない）

## 設計

### 実測（計画リポ。隣接クローン `2b25250` = `origin/main`、`git rev-parse --is-shallow-repository` → `false`）

- `node tools/doc-checks/gen-plan-ranges.js --check` → exit 0。ai-stock-trading は FR [1, 21] / UC [1, 7] / SC [1, 4] / ADR [1, 45]、
  いずれも欠番なし。NFR は参考値で [1, 18]（本リポは NFR のレンジを持たない）
- `git ls-tree --name-only origin/main projects/ai-stock-trading/07_adr/` の `ADR-XXXX` が 45 件・連番

→ 変わったのは ADR だけ。FR / UC / SC の宣言は据え置く。

### 別紙の置き場を `docs/` ではなく `.ai-context/` にした理由

既存の別紙の作法（`docs/how-to/correction-population-and-window-procedure.md`・MSP の
`docs/how-to/plan-id-range-history-annex.md`）は `docs/how-to/` に置き、trace ブロックを持つ。しかし `docs/` の可視本文には
計画 ID・IADR・修飾付き issue 参照を書けない（trace ブロック規約〔ADR-0029 決定4〕。`check-trace-blocks.js` の検査 5）。
移す履歴は「どの ADR が・どの planning issue で加わったか」が本体で、`docs/` へ置くには ID を書き換えるしかなく、
「履歴を消さず原文のまま移す」を満たせない。`.ai-context/` は本文に ID をそのまま書いてよいので、ここへ置く。
既存の `adr/` `specs/` はどちらも 1 件 1 ファイルの記録で、引き直しのたびに足していく記録には合わないため、
追記専用の `annex/` を新設した（過去の項は書き換えない＝凍結の原則と両立する）。

### 母集合（規則 9・10）

- 移す文言を引いている箇所: `git grep -n "転記元\|gen-plan-ranges\|実測 2026-09-11\|2026-09-19 実測\|0032→0035\|起点 ID の種別（固有）"`
  - 見出し「起点 ID の種別（固有）」を引くもの（`git grep -l` で本ファイルを除き 14 件: `.ai-context/adr/IADR-0202`・`IADR-0207`・`IADR-0320`・
    `.ai-context/adr/README.md`、`.ai-context/specs/` の 4 件〔532・591・709_712・924〕、
    `.github/workflows/backlog-audit.yml`、`docs/traceability-appendix.md`、`scripts/check-commit-messages.js`・`check-test-traceability.js`・
    `check-trace-blocks.js`（自己試験）・`lib/plan-ranges.js`）: **見出しは変えないので追随不要**
  - 「転記元は計画 ADR の本文ではなく実測」を引く `IADR-0320` 決定 1: **その文は節に残すので追随不要**
  - 日付つき履歴・実測の個々の文言を節名つきで引く凍結記録: **0 件**
- 旧宣言 `0001..0043` を持つ箇所: 本ファイルだけ（`.ai-context/specs/` に残る旧値は確定済み記録なので書き換えない）
- この変更で新たに誤りになる自分の記述（規則 10）: 節に残した「バッククォート囲みの `FR-01..21` の形」「`` `ADR-0001..0037` `` の形」は
  書式の例であって値ではないので誤りにならない。ADR のレンジ正規表現は節内の**最初の**一致を採るため、宣言が例より前にあることを確認した

## 受け入れ基準

1. `readPlanIds()` が FR-01..21 / UC-01..07 / SC-01..04、`readPlanAdrRange()` が {1, 45} を返す
2. `check-commit-messages.js --title` で ADR-0045 は exit 0、ADR-0046 は exit 1
3. `check-reading-budget.js` の Claude の集合が 90% 未満（warn なし）
4. 別紙 §1 が移設前の節本文と一致し、削った履歴が 1 行も失われていない
5. `scripts.test.js`（`REQUIRE_REPO_TESTS=1`）・`check-trace-blocks`・`check-plan-id-qualification`・`check-cross-repo-refs`・
   `gen-knowledge-graph --check`・`check-test-traceability`・`check-doc-links` が exit 0

## テスト方針

コードは変えないため新規テストは足さない。上の検査器の本走と `--title` の探針で確かめる。

## 計画書との差異

なし（宣言を計画側の実物へ前進させるだけ）。

## 未決事項

なし。
