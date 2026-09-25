---
title: IADR-0407 承認済みの新規建ての取引判断（DecisionId）が再配送されたら再審査しない — 自分の未約定分で自分を拒否しない。手仕舞いは従来どおり再審査する
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-20, UC-01, UC-02, ADR-0009, IADR-0057, IADR-0129, IADR-0148, IADR-0255, IADR-0342, IADR-0346, IADR-0394, IADR-0398]
author: claude (Claude Code)
created: 2026-09-25
updated: 2026-09-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 1 日あたりの発注金額上限・保有建玉数の上限)
  - planning:projects/ai-stock-trading/07_adr/ADR-0009_trading-controls-priority.md (手仕舞い・損切りは止めない)
---

# IADR-0407: 承認済みの新規建ての取引判断が再配送されたら再審査しない

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-09-25
- 決定者: claude（起票 [#832](https://github.com/endazon/ai-stock-trading/issues/832) 項目 2。利用者レビューは PR で受ける）

## 起点・関連

- 関連する計画書 ID: FR-10（日次枠・保有建玉数・段階資金）、ADR-0009（手仕舞いは止めない）
- 対象 Issue: [#832](https://github.com/endazon/ai-stock-trading/issues/832) 項目 2（PR #831 / #829 の監査の非ブロッキング指摘）
- 関連する実装仕様書: [20260925_832_rescreen-idempotency](../specs/20260925_832_rescreen-idempotency.md)
- 関連 IADR: [IADR-0346](IADR-0346_count-working-entry-orders-in-risk-limits.md)（未約定の新規建ての算入。本件の穴を広げた前提。**変えない**）、
  [IADR-0057](IADR-0057_order-dispatch-idempotency.md) / [IADR-0398](IADR-0398_forgone-decision-never-redispatched.md)（発注執行の DecisionId 予約。**変えない**）、
  [IADR-0148](IADR-0148_control-violation-supply-and-unavailable-state.md)（審査の観測は発行より先に記録・DecisionId で冪等）、
  [IADR-0255](IADR-0255_business-metrics-and-dashboards.md)（審査メトリクス）

## コンテキスト

`OrderScreeningService.Screen` は DecisionId を見ずに毎回スナップショットを組んで評価する。IADR-0346 以降、スナップショットは
**承認済みで未終端の新規建て**を保有建玉数・日次枠・段階資金へ算入する。したがって、自分の `OrderApproved` が台帳
（`approved_orders`）と注文アクティビティへ射影された**後に**同じ `TradeDecisionMade` が再配送されると（at-least-once・ack 喪失）、
**射影済みの自分自身が枠に数えられ**、承認済みの判断が拒否へ反転する。実測（保有建玉数の上限 1）: 再審査は `MaxPositionsExceeded` を返した。

拒否へ反転すると、同じ DecisionId について `OrderApproved` と `OrderRejected` の両方が監査・通知へ流れ、審査メトリクスも二重に刻まれる。
発注そのものは発注執行の予約（DecisionId）が守るため二重発注にはならないが、記録が実態（承認して発注した）と食い違う。

## 決定

1. **新規建て（`PositionEffect.Open`）の判断は、評価の前に `IPortfolioLedgerStore.FindApprovedPositionEffect(DecisionId)` を引き、
   承認行があれば再審査しない。** `ScreeningOutcome.ApprovedReplay`（第 3 の形。`Approved` も `Rejected` も `null`）を返す。
   判定コア・ロックアウトの設定・損切りの供給の読み取りはいずれも走らせない。
   - 承認行は自分の `OrderApproved` の射影であり、行があることは「同じ判断の承認を発行済み」を意味する。
2. **`TradeDecisionMadeHandler` は第 3 の形を最初に見て、何も発行せず・観測も記録せず・審査メトリクスも刻まずに戻る**（Information ログ 1 行）。
   例外にしない（投げても共通再試行が同じ判断を再処理するだけで結論は変わらない）。
   - **承認を発行し直さない**: 承認は「どの設定（損切りの実行機構）で承認したか」の記録でもあり（IADR-0342）、台帳はそれを持たないため再構成できない。
     発行し直さなくても、最初の承認は自分の射影に届いている（＝発行は済んでいる）。
3. **手仕舞い（Close）は対象外**で、従来どおり再審査する。再審査して承認を発行し直しても発注執行が DecisionId で止める一方、
   抑止すると、最初の発行が発注執行へ届かなかった手仕舞いが**出ない側**へ倒れる（ADR-0009）。
   台帳の読み取りも新規建てに限り、読み取りの失敗が手仕舞いの審査を新たに巻き込まない（IADR-0394 の損切りの供給と同じ規律）。
4. **拒否済みの判断の再配送は従来どおり再審査する**（台帳に行が無い）。本決定の射程外。

## 棄却した案

| 案 | 棄却理由 |
| --- | --- |
| 自分の DecisionId だけを未約定の算入から除いて再評価する | 自分の**約定分**は射影の建玉へ畳まれ DecisionId で除けない。部分約定の後は保有建玉数・段階資金で拒否へ反転し得る（形として塞がない） |
| 承認行から `OrderApproved` を組み直して発行し直す | 承認時点の損切りの実行機構を台帳が持たない（現在の設定で埋めると記録を偽る）。発注執行が DecisionId で止めるので発行し直す得も無い |
| 手仕舞いも抑止する | 最初の発行が一部の宛先へ届かなかった場合に手仕舞いが出ない側へ倒れる（ADR-0009 に反する向き） |
| 審査結果（承認・拒否）を DecisionId で別表に保存して再生する | 状態を増やす。承認側は台帳が既に持っており、拒否側の再生は本 issue の射程外 |

## 結果

- 良い点: 承認済みの判断が再配送で拒否へ反転しない。同じ DecisionId の承認と拒否が監査・通知に並ばない。観測・メトリクスが審査の件数と一致する。
- 残余リスク:
  - 最初の発行が発注執行のキューへ届かず、自分の射影だけ済んだ状態で再配送されると、**その新規建ては出ない**（抑止）。
    発注しない側であり、次の定時判断で改めて審査される。その承認は未終端の新規建てとして当日中の枠を食う（IADR-0346 の既存の残余リスクと同じ向き）。
  - 射影の**前**に届いた再配送は通常の審査を受け、同じ DecisionId の承認が 2 度発行され得る（従来と同じ。発注執行の予約が 2 本目を止める）。
- 追随: テスト仕様書 FR-10（T-10-870〜T-10-875）。
