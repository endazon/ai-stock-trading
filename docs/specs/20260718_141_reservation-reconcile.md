---
title: 作業仕様書 #141 発注予約の自動リコンサイル（Reserved 滞留をブローカ照会で解消）
type: work-spec
status: In Progress
related_ids: [FR-05, UC-01, UC-02, ADR-0002, ADR-0003, IADR-0016, IADR-0056, IADR-0057, IADR-0059, IADR-0067]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
issue: 141
---

# 作業仕様書 #141: 発注予約の自動リコンサイル

## 起点・関連

- 対象 Issue: [#141](https://github.com/endazon/ai-stock-trading/issues/141)
- 計画書 ID: **FR-05**（発注執行）、UC-01 / UC-02、ADR-0002 / ADR-0003
- 前提 IADR: [IADR-0057](../adr/IADR-0057_order-dispatch-idempotency.md)（3相冪等化・本件を後続として明示）、
  [IADR-0059](../adr/IADR-0059_dedupe-retention-purge.md)（Reserved は時間経過で消さない）、
  [IADR-0067](../adr/IADR-0067_order-lifecycle-telemetry.md)（訂正・取消の配管）
- 本作業の設計判断: [IADR-0074](../adr/IADR-0074_reservation-reconciliation.md)

## 背景と課題

#131 / IADR-0057 で発注を「予約 → 発注 → 確定」の3相に冪等化し、「ブローカ発注成功 → 永続化失敗」の窓での
二重発注を **at-most-once** で塞いだ。その代償として、予約が `Reserved` のまま結果が無い状態は「未発注」と
「発注済みだが記録できていない」の区別が付かず、再処理は再発注せず `OrderDispatchReservationConflictException`
で拒否される。当該メッセージは再試行を使い切って `_error` キューに滞留し、**解消は人手**である（IADR-0057 決定2）。

本作業は、この `Reserved` 滞留を **ブローカ照会で自動的に解消**する機構を、二重発注を絶対に起こさない fail-safe で
導入する。

## client order id 調査（設計の前提）

- `Reserved` 予約が持つのは `DecisionId` のみで、**ブローカ注文 ID を持たない**（確定時に初めて記録）。
- 現状 `MoomooOrderRequest`（`Symbol/Market/Side/Quantity/Price`）は **client order id を伝播せず**、
  `PlaceOrderAsync` はブローカ採番 `OrderId` を返す。`IBrokerAdapter.GetOrderAsync(orderId)` は
  **ブローカ注文 ID による照会**であり、注文 ID を持たない滞留 `Reserved` の照合には使えない。
- したがって滞留 `Reserved` の実照会は「DecisionId を client order id として発注時に伝播 → それで照会」または
  「銘柄・数量・時刻窓での突き合わせ」が必要で、**いずれも実 OpenD 依存**（確度・設計は IADR-0074 §決定）。
- 本 PR は実 OpenD 照会を **後続・#82 系 E2E へ分離**し、機構と安全既定（no-op プローブ）までを実装する。

## スコープ

含む:

- 滞留 `Reserved`（`ReservedAt` が閾値より古い）を定期検出し、プローブ照会で実状態を確定して解消する機構。
- プローブ・ポート `IReservationBrokerProbe`（3値: `Placed / NotPlaced / Indeterminate`）と、
  **既定 no-op 実装**（常に `Indeterminate`＝何も解放・終端化しない）。
- リコンサイラ `OrderReservationReconciler`（Application）と定期実行 `OrderReservationReconciliationService`
  （Worker・BackgroundService・**既定無効**）。
- 予約ストアへの走査・解放メソッド追加（`FindStalledReserved` / `Release`）。スキーマ変更なし。
- 可観測性: 走査/終端化/解放/不確定の件数を構造化ログに出す。

含まない（後続）:

- 実 OpenD 照会プローブ（DecisionId 伝播 or 時刻窓突合）と OpenD SIMULATE E2E（受け入れ基準3）。
- 専用メトリクス Meter / 通知イベント（新イベント＝監査追随を要するため見送り。IADR-0074 §結果）。

## 受け入れ基準（Issue #141）

| # | 基準 | 本 PR での担保 |
| --- | --- | --- |
| 1 | `Reserved` 滞留がブローカ照会で自動解消（発注済み→確定・未発注→解放） | fake プローブで `OrderReservationReconciler` を単体検証（`Placed`→終端化＋`OrderExecuted` 発行、`NotPlaced`→解放） |
| 2 | 照会不達・不確定は**解放しない**（fail-safe） | `Indeterminate` 据え置きを単体検証。既定 no-op プローブは常に `Indeterminate` |
| 3 | 実基盤（OpenD SIMULATE）E2E で確認 | **後続へ分離**（実 OpenD 依存）。本 PR は `Refs #141` |

## 設計（詳細は IADR-0074）

1. `IReservationBrokerProbe.ProbeAsync(decisionId)` → `ReservationProbeResult`（`Outcome` ＋ `Placed` 時の `BrokerOrder`）。
2. `OrderReservationReconciler.ReconcileAsync(stallCutoff, batchSize)`:
   - `FindStalledReserved(stallCutoff, batchSize)` で滞留 `Reserved` を取得。
   - 各予約について:
     - `executedOrders.FindByDecisionId` に**記録あり** → phase-4 断絶の自己修復（`MarkCompleted` ＋ `OrderExecuted` 再発行）。ブローカ照会不要。
     - 記録なし → `ProbeAsync`:
       - `Placed(order)` → `ExecutionRecord` 保存 ＋ `MarkCompleted` ＋ `OrderExecuted` 発行（**確定**）。
       - `NotPlaced` → `Release`（予約削除・**未発注**）。ブローカ呼び出しなし（未発注なので取り消す対象がない）。
       - `Indeterminate` → 据え置き（人手/`_error`）。
   - 返り値: 件数サマリ ＋ 発行すべき `OrderExecuted` の一覧（発行は Worker）。
3. `OrderReservationReconciliationService`（BackgroundService）が定期走査し、返された `OrderExecuted` を発行する。
   既定無効（`Reconciliation:Enabled=false`）。

## 冪等性・安全性（最優先）

- `NotPlaced` を返すのはプローブが**確実に未発注**と判定した時のみ。no-op 既定は決して `NotPlaced` を返さない
  ＝構造的に fail-safe（IADR-0057 が守る at-most-once を破らない）。
- `Placed` 分岐は `FindByDecisionId` が null の時のみ到達＝二重計上・二重発注なし。
- `OrderExecuted` は既存イベント（監査済み・Risk/Notification が冪等消費）＝**新イベントなし・監査 Consumer 追加不要**。
- 既存 `order_dispatch_reservations` の `State` インデックスを使用＝**マイグレーション不要**。
- 滞留閾値は再配送窓（約42秒）＋`_error` 滞留の外側に置き、下限クランプで設定ミスに耐える。

## テスト

- `OrderReservationReconciler`: 記録あり自己修復 / `Placed`→終端化＋発行 / `NotPlaced`→解放 / `Indeterminate`→据え置き /
  非滞留（cutoff 内）は対象外 / batchSize 上限。
- `IReservationBrokerProbe` no-op 実装: 常に `Indeterminate`。
- `IOrderReservationStore.FindStalledReserved` / `Release`: InMemory ＋ EF 実装の双方。
- `ReconciliationOptions` / `ReconciliationPolicy`: 既定・下限クランプ・Interval クランプ。
- DI 解決（Program.cs の登録）: hosted service ＋ 既定 no-op プローブが解決する。

## 検証（DoD）

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` 緑。
- `dotnet format` 整形・nullable 警告ゼロ。
- コミット件名は `種別(FR-05): ...`。PR は `Refs #141`。
