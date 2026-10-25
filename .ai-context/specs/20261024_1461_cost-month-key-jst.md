---
title: 月次 LLM 費用上限の判定の月を JST の暦月にする（費用統制の費用計上月・既存行の引き直し・夜間の要約の月）（#1461）
type: spec
status: done
related_ids: [NFR-13, ADR-0057, ADR-0074, IADR-0027, IADR-0034, IADR-0478, IADR-0582, IADR-0584]
author: claude (Claude Code)
created: 2026-10-24
updated: 2026-10-24
---

# 仕様書: 費用計上月を JST の暦月にする（#1461）

> 本仕様書は AST `origin/develop` `4c84de49` を起点に、実装の前に作成した。判断は [IADR-0584](../adr/IADR-0584_cost-month-key-jst.md) に残す。

## 起点（トレーサビリティ）

- 起点: #1461（親 #1423。#1460 / IADR-0582 の残余リスクから起票）。
- 計画: ADR-0057 決定 3（月次 LLM 費用上限の月は JST の暦月。毎月 1 日 0:00 JST に累計が 0 へ戻る）、NFR-13、
  `05_trading-assumptions` §6.1、ADR-0074 決定 2（100% 停止の解除はこの月に依存する）。
- 前提（覆さない）: IADR-0027（月次計上と統制判定）、IADR-0034（月単位のアドバイザリロックで遷移を高々 1 回に保つ）。

## 受け入れ基準

1. 2026-10-31T15:00:00Z（JST 11/1 0:00）以降の計上は 11 月の累計に入り、その直前の計上は 10 月の累計に入る。
2. 80%・100% の判定と `/costs/state`（・`/costs/usage`）は JST の暦月の累計で答える。
3. 取引判断の停止（IADR-0582）は JST の 1 日 0:00 に解除される（取引判断は判断のたびに `/costs/state` を読むので、1 で足りる）。
4. 既存の行（UTC 由来の月キー）の扱いを決める → **補正**（記録の時刻から JST の年月で引き直す移行）。
5. 勧告ロックの bigint が新しい導出でも月ごとに一意で安定である。
6. 夜間の要約 §13（費用統制の台帳を月キーで読む）の月も JST の暦月に揃える（同じ台帳を別の月で読むと食い違う）。

## 母集合（規則 9。誤りの側の文字列 `UTC の暦月`・`UTC の年月` と、月キーの導出 `MonthKey`・`AT TIME ZONE 'UTC'` で走査した）

| 引いた場所 | 判定 |
| --- | --- |
| `CostControlAppService.MonthKey`（導出の本体）・`EfCostLedger.AdvisoryKey`（月キーの消費者）・`CostEntryRow` | 是正・注記 |
| 費用統制の試験（月の境界・消費者の試験の当月の求め方） | 是正（JST へ。消費者の試験は本番の導出を使う） |
| `scripts/nightly-ledger-summary.sh` §13・`scripts/nightly-ledger-summary.pg.test.sh` T-10-2025 | 是正（JST） |
| `TradeDecisionAppService.cs` のコメント（#1460） | 是正 |
| `docs/operations/nightly-ledger-summary-runbook.md`・`docs/tests/FR-10_risk-controls-tests.md`（T-10-2025・#1460 の節） | 是正 |
| `.ai-context/adr/IADR-0582`・`IADR-0478`・作業仕様書 `20261024_1423_llm-cost-cap-halt-covers-price-movement` | **本文は書き換えない**（凍結記録）。日付つきの追記を足した。索引の IADR-0478・IADR-0582 の行にも日付つきの追記を足した（`check-adr-index-sync` が本文の変更に索引の追随を求める） |

### 引き直し（規則 9・10。PR の AI レビューで `UTC。` の漏れを指摘された後）

`UTC の暦月`・`UTC の年月` だけでは `（UTC。…）` の書き方を拾えなかった。`UTC` と `月` の組み合わせ（`UTC。`・`UTC）`・`UTC)`・`UTC の月`）と、
月を作る式（`ToString("yyyy-MM"`・`AT TIME ZONE 'UTC'`・`UtcNow` から月を作る箇所）で全ファイルを引き直した。

| 引いた場所 | 判定 |
| --- | --- |
| `docs/data/cost-entries.md:39`「（UTC。月をまたぐと…）」 | 是正（JST） |
| `AuditEntryFactory` の `llm-cost:{yyyy-MM}`（発生時刻のオフセットのまま＝UTC） | 是正（JST。IADR-0584 決定5。相関を読む実装が無いので壊れる集計は無い） |
| `AuditEntryFactory` の `llm-fallback:{yyyy-MM}`・`trade-decision-skip:{yyyy-MM}` | **除外**: 費用統制の月ではない。月報の回数は監査台帳を期間の窓で引き、相関は使わない（IADR-0584 決定5） |
| `ReportPeriod` の月報の期間キー（`date.ToString("yyyy-MM")`） | 除外: 入力は JST の日付（報告書の暦）であり、UTC の時刻から作っていない |
| 費用統制の消費者の試験の当月（`UtcNow.ToString("yyyy-MM")`） | 是正済み（本番の導出を使う） |
| 試験・文書の「… UTC」の時刻表記（例「17:00 JST ＝ 08:00 UTC」）・`backlog-audit.yml` の「月曜 00:00 UTC」 | 除外: 曜日・時刻の表記で、費用の月ではない |
| `.ai-context/specs/20261001_1140_*`（「終端の UTC 月」） | 書き換えない（凍結された作業仕様書。当時の記述） |

## 試験の写像

T-10-3064・3065（費用統制）、T-10-2025（夜間の要約・改め）。対応表はテスト仕様書の該当節に置く。
