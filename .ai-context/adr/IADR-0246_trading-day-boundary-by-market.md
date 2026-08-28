---
title: IADR-0246 日次統制・期間集計の取引日境界を市場現地の取引日で解釈する（JST 固定の廃止）
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-06, ADR-0009, IADR-0008, IADR-0018, IADR-0181, IADR-0246]
author: claude (Claude Code)
created: 2026-08-28
updated: 2026-08-28
plan_refs:
  - planning:projects/ai-stock-trading/04_workflows/01_scheduled-trading-cycle.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# IADR-0246: 日次統制・期間集計の取引日境界を市場現地の取引日で解釈する

- 状態: Accepted
- 日付: 2026-08-28
- 決定者: claude（起票 #337。#249 を吸収）

## 起点・関連

- 関連する計画書 ID: FR-10（日次損失上限・日次発注枠・同日再エントリー）・FR-06（期間集計）・ADR-0009
- 関連する実装仕様書: [`20260828_337_trading-cycle-and-screening.md`](../specs/20260828_337_trading-cycle-and-screening.md)

## コンテキストと課題

`PortfolioProjection.TradeDate` は**固定 +9（JST・DST なし）**で取引日を切っており（IADR-0018 が
「市場別の取引日境界は後続」と明記した暫定）、日次統制の当日判定（`clock.Today`＝JST）も同じだった。

米国市場（主対象）では JST 0 時＝**ET 10–11 時（セッションの真ん中）**である。実害は具体的で、

- 日次損失ロックアウトが ET 10:00 に発動しても、JST 日付が変わる ET 10–11 時に「翌営業日」へ達し、
  **同一の米国セッションの途中でデイリーストップが解除される**。
- 当日発注枠（equity の 150%/日）・同日再エントリー・当日実現損益が ET 夜間（JST 翌日）の約定を
  別日に数え、**1 セッションが 2 つの「日」に割れる**。

## 決定

1. **取引日の導出は `TradingDay` に 2 基準を持たせる。** `Of(instant)`＝JST（従来）と
   `Of(instant, market)`＝市場現地（US=America/New_York・JP=Asia/Tokyo）。変換は `TimeZoneInfo`
   （DST を吸収・固定オフセット禁止は MarketCalendar と同方針）。
2. **日次統制・日次集計は市場現地の取引日で解釈する。** `PortfolioProjection`（当日判定は約定の市場ごと・
   `TradeDate(instant, market)` へ置換し旧シグネチャは**削除**＝取り残しをコンパイルエラーで検出）、
   `PeriodFillQuery`（「日次上限が見ている 1 日」＝「日報が集計する 1 日」の一致を市場別解釈のまま保つ）、
   `OrderScreeningService`（ロックアウトの当日判定・解除日は**注文の市場**の現地取引日）。
3. **表示（`RiskStatusService`）は最も遅れている市場の現地取引日で判定する**（`TradingDay.EarliestCurrent`）。
   市場を特定できない表示文脈で JST（先行側）を使うと、米国セッションで**まだ効いている統制を
   「解除済み」と表示**してしまう。保守側（遅い方）に倒す。
4. **JST のまま残すもの（意図的な線引き）**: 観測到達（`IPositionObservationArrivalStore`）・買戻し推定の
   期間（**IADR-0181 が JST 一本化を確定済み**。FR-21 の突合構造に触れない）・`SystemClock.Today`・
   日次バッチの起動ゲート（`ObservedDrawdownRefreshService` / `WithdrawalEvaluationService`——起動条件で
   あり統制の境界ではない）・報告書の生成タイミング（`ReportSchedule`——生成の都合であり集計境界は
   `PeriodFillQuery` 側で市場別になる）。

## 結果

- 良い影響: デイリーストップ（ADR-0009）が計画どおり「その市場の当日」を通して効く。日次集計が
  セッションと一致する。
- 悪い影響 / トレードオフ: 口座横断の値（equity・日次損失）を市場別の「日」で切るため、日米両市場で
  同時に建てた場合、日次カウンタのリセット時刻が銘柄の市場によって異なる。主対象が米国市場・
  東証は当面監視/検証用（計画 04_workflows/01）であるため許容する。
- 残余リスク: `IBusinessCalendar`（週末のみ）は市場の祝日を持たない——ロックアウトの「翌営業日」が
  祝日に当たると 1 日早く解ける可能性は従来と同じ（#21 系の関心事・IADR-0245 と同根）。
