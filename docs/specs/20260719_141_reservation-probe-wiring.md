---
title: 作業仕様書 #141 実照会プローブの配線（moomoo/OpenD SIMULATE で Reserved 滞留を解消）
type: work-spec
status: In Progress
related_ids: [FR-05, UC-01, UC-02, ADR-0002, ADR-0003, IADR-0016, IADR-0056, IADR-0057, IADR-0059, IADR-0074]
author: endazon (with Claude Code)
created: 2026-07-19
updated: 2026-07-19
issue: 141
---

# 作業仕様書 #141: 実照会プローブの配線

## 起点・関連

- 対象 Issue: [#141](https://github.com/endazon/ai-stock-trading/issues/141)（残スコープ「実照会プローブの配線」）
- 計画書 ID: **FR-05**（発注執行）、UC-01 / UC-02、ADR-0002（ブローカ選択）/ ADR-0003（承認済みのみ発注）
- 前提 IADR: [IADR-0074](../adr/IADR-0074_reservation-reconciliation.md)（本件の機構＝プローブ・ポート＋既定 no-op を
  確立し、**実照会は後続へ明示分離**）、[IADR-0057](../adr/IADR-0057_order-dispatch-idempotency.md)（3相冪等化・
  at-most-once）、[IADR-0056](../adr/IADR-0056_moomoo-simulate-poc-complete-real-gated.md)（§3 実弾解禁の前提）、
  [IADR-0016](../adr/IADR-0016_safe-broker-execution.md)（SIMULATE 限定・実弾を撃たない）
- 本作業の設計判断: [IADR-0092](../adr/IADR-0092_reservation-broker-probe-moomoo.md)

## 背景と課題

IADR-0074（PR #177）で滞留 `Reserved` の自動リコンサイル**機構**（`IReservationBrokerProbe` の3値ポート＋既定
no-op `IndeterminateReservationBrokerProbe`＋`OrderReservationReconciler`＋既定無効 BackgroundService）を導入した。
既定 no-op プローブ下では `Placed/NotPlaced` 経路は発火せず、実照会は「後続・#82 系 E2E」として明示的に残されていた。

本作業はその残スコープ、すなわち **moomoo/OpenD(SIMULATE) に実際に照会して `Reserved` を解消できる実プローブ**を
配線する。**実弾（`TrdEnv_Real`）には一切触れない**（SIMULATE のみ）。

## client order id 突合の制約（PR #177 の申し送り）

- 滞留 `Reserved` 予約（`OrderDispatchReservation`）が持つのは **`DecisionId` と `ReservedAt` のみ**。ブローカ注文 ID も
  intent（銘柄・数量）も持たない。したがって **突合キーは `DecisionId` に限られる**。
- `IBrokerAdapter.GetOrderAsync(orderId)` は**ブローカ採番の注文 ID**による照会のため、注文 ID を持たない滞留
  `Reserved` には使えない（IADR-0074 で確認済み）。
- 実照合を可能にする唯一の手段は、**発注時に `DecisionId` をブローカ側の client order id（moomoo の `remark`）として
  伝播**し、それで注文一覧を照合すること（IADR-0074 §検討した代替案(a)＝最高確度）。SDK 反射で
  `TrdPlaceOrder.C2S.SetRemark` と `TrdCommon.Order.Remark` の存在を実測確認済み（`moomoo-api` 10.8.6808）。

## 決定（詳細は IADR-0092）

1. **DecisionId を `remark` に伝播して発注する。** shared `IBrokerAdapter` は無改修とし、OrderExecution 内の
   capability ポート `IClientOrderIdBroker`（`PlaceOrderAsync(intent, decisionId, ct)`）を新設。`MoomooBrokerAdapter`
   のみ実装し、`OrderExecutionService` が `broker is IClientOrderIdBroker` で分岐する（paper・テスト fake は無改修＝
   remark 非対応でも従来どおり動く）。`remark` の書式は `MoomooClientOrderId.From(Guid)`（`"N"` 32桁）に単一化。
2. **実プローブ `MoomooReservationBrokerProbe`**（Worker・`IReservationBrokerProbe` 実装）が `IMoomooTradeClient` の
   新メソッド `FindOrderByClientIdAsync(clientOrderId, reservedAtUtc, ct)` で現在＋履歴注文を全市場列挙し
   `remark == DecisionId` で照合する:
   - 一致した注文が見つかる → `Placed`（moomoo 注文スナップショットから `BrokerOrder` を再構成）。
   - 全市場・現在＋履歴（`ReservedAt` を覆う窓）を**成功裏に列挙**して一致ゼロ → `NotPlaced`（確実に未発注）。
   - 照会失敗・タイムアウト・部分列挙・SDK 例外 → **必ず `Indeterminate`**（据え置き・解放しない）。
3. **プローブ・ポートを最小拡張**: `ProbeAsync(Guid decisionId, …)` → `ProbeAsync(OrderDispatchReservation, …)`。
   健全な `NotPlaced` 判定には履歴照会窓を `ReservedAt` で確実に覆う必要があるため（窓外の発注済み注文を
   見落として誤って `NotPlaced` を返すことを構造的に防ぐ）。no-op プローブ・リコンサイラ・既存テストを追随。
4. **既定は no-op のまま。** 実プローブは `Broker:Provider=moomoo` かつ `Reconciliation:UseBrokerProbe=true` の
   opt-in 時のみ DI 登録。paper／OpenD 無し／既定は `IndeterminateReservationBrokerProbe`（無改修の安全既定）。
5. **SIMULATE 固定。** 列挙は既存 `BuildHeader`（`TrdEnv_Simulate`）で SIMULATE 口座に閉じる。`TrdEnv_Real` に触れない。

## at-most-once（二重発注ゼロ）の担保

- 「**不明な窓では解放しない**」を構造的に厳守する。`NotPlaced`（解放）は **remark 突合が成功裏に完了し、現在＋
  履歴（`ReservedAt` を覆う窓）を全市場列挙して一致ゼロ**の時だけ返す。列挙が成立して初めて「確実に未発注」＝既知の窓。
- 照会経路のあらゆる不確実性（例外・タイムアウト・部分列挙・窓を覆えない）は一律 `Indeterminate` に倒す。
- リコンサイラの TOCTOU 対策（`Save` 直前の `FindByDecisionId` 再確認）は不変。実プローブでも二重 `Save`・二重発注は
  起き得ない。既定 no-op のため、opt-in しない限り解放・終端化は phase-4 自己修復（ブローカ非依存）のみ。

## 影響範囲

| 層 | 変更 |
| --- | --- |
| Application.Ports | `IReservationBrokerProbe.ProbeAsync` を `OrderDispatchReservation` 受けに変更／`IClientOrderIdBroker` 新設 |
| Application.Adapters | `IndeterminateReservationBrokerProbe` を新シグネチャに追随 |
| Application.Services | `OrderExecutionService` が `IClientOrderIdBroker` 分岐で DecisionId を伝播／`OrderReservationReconciler` は `reservation` を渡す |
| Worker.Composable.Adapters | `IMoomooTradeClient` に `FindOrderByClientIdAsync` 追加・`MoomooOrderRequest` に `Remark`／`MMApiMoomooTradeClient` の実 OpenD 配線（GetOrderList＋GetHistoryOrderList・remark 照合・`OnReply_GetHistoryOrderList` 結線）／`MoomooBrokerAdapter` が `IClientOrderIdBroker` 実装／`MoomooReservationBrokerProbe` 新設／`MoomooClientOrderId` 書式単一化 |
| Worker/Program.cs | `IMoomooTradeClient` を DI 共有・opt-in で実プローブを登録 |

**変更しないも（無改修）**: shared `IBrokerAdapter` / `PaperBrokerAdapter` / `OrderReservationReconciliationService`
（Background.Service 骨格）／スキーマ（マイグレーション無し）／イベント契約（`OrderExecuted` 再利用・監査 Consumer 追随不要）。

## テスト（受け入れ基準の写像）

- `MoomooReservationBrokerProbe`（fake `IMoomooTradeClient`）:
  - 一致注文あり → `Placed`・intent 再構成（銘柄・数量・状態・約定平均）が正しい（受け入れ基準1 発注済み→確定）。
  - 全列挙成功で一致ゼロ → `NotPlaced`（受け入れ基準1 未発注→解放）。
  - client 例外／タイムアウト → `Indeterminate`（受け入れ基準2 fail-safe）。
  - 非終端（Submitted/PartiallyFilled）でも注文が存在すれば `Placed`（発注済み＝再発注しない）。
  - `OperationCanceledException` は握りつぶさず伝播。
- `MoomooBrokerAdapter`: `IClientOrderIdBroker.PlaceOrderAsync` が `remark = DecisionId("N")` を client 要求に載せる。
- `OrderExecutionService`: broker が `IClientOrderIdBroker` の時のみ DecisionId を伝播し、そうでなければ従来経路（無改修 fake で回帰）。
- `MMApiMoomooTradeClient`: SDK 非依存の写像（`MapState` 等）・remark 書式の単体テスト。**実 OpenD 照会（履歴時刻
  フィルタ・接続）は実基盤依存のため CI 対象外**＝ローカル OpenD SIMULATE ＋ #82 系で通す。
- 既存 `OrderReservationReconcilerTests` をポート新シグネチャに追随（挙動不変）。

## 受け入れ基準（issue #141）

- [x] `Reserved` 滞留がブローカ照会で自動解消される（発注済みは確定・未発注は解放）＝実プローブ＋fake で単体検証
- [x] 照会不達・不確定は解放しない（fail-safe）＝`Indeterminate` 経路で単体検証
- [ ] 実基盤（OpenD SIMULATE）での E2E で確認する＝**実 OpenD 依存のためローカル SIMULATE ＋ #82 系へ分離**（本 PR は `Refs #141`）

## 実 OpenD SIMULATE 通し手順（ローカル）

1. OpenD を SIMULATE 口座で常駐（`docs/adr/IADR-0053` の docker 化・`Broker:Moomoo:OpenD:Host/Port`）。
2. `Broker:Provider=moomoo`・`Reconciliation:Enabled=true`・`Reconciliation:UseBrokerProbe=true`・
   `Reconciliation:StallThresholdHours` を短縮して起動。
3. 予約だけ残る滞留を意図的に作り（発注後に確定永続化を止める／`_error` 滞留を再現）、巡回で `Placed`→確定、
   未発注を `NotPlaced`→解放、OpenD 停止時に `Indeterminate`→据え置きになることを確認。
4. `TrdEnv_Real` に切り替わらないこと（`MoomooBrokerOptions.EnsureSimulate` の閂）を確認。
