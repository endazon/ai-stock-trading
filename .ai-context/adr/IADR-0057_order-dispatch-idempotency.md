---
title: IADR-0057 発注の冪等化は「発注前 DecisionId 予約」の3相で行い、不明な窓は再発注せず拒否する
type: impl-adr
status: Accepted
related_ids: [FR-05, UC-01, UC-02, ADR-0002, ADR-0003, IADR-0016, IADR-0056]
author: endazon (with Claude Code)
created: 2026-07-16
updated: 2026-07-16
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0057: 発注の冪等化は「発注前 DecisionId 予約」の3相で行い、不明な窓は再発注せず拒否する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-16
- 決定者: endazon（利用者・方針「実弾は撃たない」）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-05**（発注執行）、UC-01 / UC-02、**ADR-0003**（承認済み注文のみ発注）
- 対象 Issue: [#131](https://github.com/endazon/ai-stock-trading/issues/131)
- 関連 IADR: [IADR-0016](IADR-0016_safe-broker-execution.md)（安全既定 paper・実弾防止ゲート）、
  [IADR-0056](IADR-0056_moomoo-simulate-poc-complete-real-gated.md)（**§3 が本件を実弾解禁の前提と明記**）
- 関連仕様書: [20260716_131_order-idempotency-reservation](../specs/20260716_131_order-idempotency-reservation.md)

## コンテキストと課題

発注執行の冪等性は、これまで**発注後の `DecisionId` 照合のみ**だった。`executed_orders` に同一 `DecisionId` の
行があれば再発注せず既存結果を再発行する、という後読みのチェックである。

これは「**ブローカ発注成功 → 永続化失敗**」の窓を守れない。この窓でプロセスが落ちると記録が残らないため、
MassTransit の再配送（`UseAiStockTradingRetry`＝2s/10s/30s）で同じ `OrderApproved` が再処理された際、
チェックは素通りし **同一 `DecisionId` で二重発注**する。SIMULATE では実害が限定的だが、実弾では
二重建玉＝実損であり、IADR-0056 §3 はこれを解禁の前提条件として挙げていた。

## 決定

**「予約 → 発注 → 確定」の3相にし、ブローカ発注の前に `DecisionId` の一意予約をコミットする。
予約済みだが未確定＝発注済みか不明の再処理は、再発注せず例外で拒否する（at-most-once）。**

1. **予約表**: `order_dispatch_reservations`（`DecisionId` を主キー＝**一意制約が競合の権威**）を追加する。
   予約はブローカ発注の**前**に、独立したトランザクションでコミットする。
2. **不明な窓は拒否する**: 予約が `Reserved` のまま結果が無い状態は「未発注」と「発注済みだが記録不能」の
   **区別が付かない**。再発注せず `OrderDispatchReservationConflictException` を投げる。例外は再試行3回の後
   `_error` キューへ送られ、人手（将来は自動リコンサイル）で解消する。
3. **完了の権威は `executed_orders` に据え置く**（相1の `FindByDecisionId` を残す）。予約表の導入前に既に
   存在する行にも冪等性を効かせるための**後方互換**であり、予約表は「発注着手」だけを守る。役割は重複しない。
4. **本 IADR は実弾を解禁しない**。`TrdEnv_Real` の解禁には引き続き**別 IADR＋明示 config** を要する
   （IADR-0016 の BrokerFactory ゲート＋SIMULATE 固定は本 PR で一切変更しない）。

## 検討した代替案

- **トランザクショナル outbox（発注意図を outbox 行にして別 dispatcher が送る）**: 一般解だが、
  「dispatcher が送信後にコミットできず落ちる」窓は**同じ形で残る**（外部副作用は取り消せないため、
  outbox でも at-most-once か at-least-once かの選択は避けられない）。本件が守りたいのは
  「ブローカ発注の一意性」1点であり、dispatcher プロセス・outbox のポーリング/掃除といった常設機構を
  増やすコストに見合わない。予約行は outbox の「送信前にコミットされた一意キー」という核だけを取り出したもの。
- **再処理時にブローカへ照会して既存注文を確認してから再発注する**: 最も情報量が多く取りこぼしも無いが、
  実 API 依存（moomoo の client order id 対応の調査が要る）で本 PR に収まらず、CI の単体でも固定できない。
  照会によるリコンサイルは**後続 issue（[#141](https://github.com/endazon/ai-stock-trading/issues/141)）に分離**し、
  本 PR は「二重発注しない」ことだけを確定させる。
- **at-least-once（不明なら再発注する）**: 二重発注＝実損を招く。実弾では *未発注の取りこぼし*（機会損失・可逆）
  の方が *二重発注*（実損・不可逆）より受容できるため採らない。

## 影響

- **肯定的**: 「発注成功→永続化失敗」を跨いでもブローカ発注が高々1回に限定され、#131 の受け入れ基準を満たす。
  IADR-0056 §3 の実弾解禁前提のうち「冪等化」が充足される。
- **制約**: 予約が `Reserved` のまま残った注文は**自動では再開しない**（意図的な at-most-once の代償）。
  当該メッセージは `_error` キューに滞留し、人手のリコンサイルを要する。自動リコンサイルは後続
  issue（[#141](https://github.com/endazon/ai-stock-trading/issues/141)）。
- **運用**: `_error` キューの滞留は「発注済みか不明の注文」を意味するため、実弾解禁時には監視対象に含める必要がある。

## 備考

実弾解禁の残条件（IADR-0056 §3）のうち、本 IADR が充足するのは**冪等化のみ**である。リスク統制・監査・上限
（`TradingDefaults`）の実弾向け再確認、秘匿情報の Vault 化、および自動リコンサイル（#141）は未充足のまま残る。
