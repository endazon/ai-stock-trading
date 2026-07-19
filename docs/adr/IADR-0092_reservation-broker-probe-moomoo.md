---
title: IADR-0092 Reserved 滞留の実照会プローブは DecisionId を moomoo remark に伝播し、SIMULATE の現在＋履歴注文を照合する。解放は「確実に未発注」の既知窓に限り、他は Indeterminate
type: impl-adr
status: Accepted
related_ids: [FR-05, UC-01, UC-02, ADR-0002, ADR-0003, IADR-0016, IADR-0056, IADR-0057, IADR-0059, IADR-0074]
author: endazon (with Claude Code)
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0092: Reserved 滞留の実照会プローブ（moomoo remark 突合・SIMULATE）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: endazon（利用者・方針「実弾は撃たない」「二重発注を絶対に起こさない」）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-05**（発注執行）、UC-01 / UC-02、ADR-0002（ブローカ選択）、ADR-0003（承認済みのみ発注）
- 対象 Issue: [#141](https://github.com/endazon/ai-stock-trading/issues/141)（残スコープ「実照会プローブの配線」）
- 前提 IADR: [IADR-0074](IADR-0074_reservation-reconciliation.md)（リコンサイル**機構**とプローブ・ポートを確立し、
  **実照会を本 IADR へ後続分離**）、[IADR-0057](IADR-0057_order-dispatch-idempotency.md)（3相冪等化・at-most-once）、
  [IADR-0016](IADR-0016_safe-broker-execution.md)（SIMULATE 限定・実弾を撃たない）、
  [IADR-0056](IADR-0056_moomoo-simulate-poc-complete-real-gated.md)（§3 実弾解禁の前提として本件を挙げる）
- 関連仕様書: [20260719_141_reservation-probe-wiring](../specs/20260719_141_reservation-probe-wiring.md)

## コンテキストと課題

IADR-0074 は滞留 `Reserved` の自動リコンサイル機構（3値プローブ・ポート `IReservationBrokerProbe`＋既定 no-op＋
`OrderReservationReconciler`＋既定無効 BackgroundService）を確立したが、**実照会は no-op のまま後続へ分離**されていた。
既定 no-op（常に `Indeterminate`）下では `Placed/NotPlaced` 経路は発火せず、自動解消は phase-4 自己修復のみに留まる。

本 IADR は実照会プローブを配線する。**核心の制約（IADR-0074 で確認済み）**: 滞留 `Reserved`（`OrderDispatchReservation`）が
持つのは `DecisionId` と `ReservedAt` のみで、ブローカ注文 ID も intent（銘柄・数量）も持たない。よって突合キーは
`DecisionId` に限られ、`IBrokerAdapter.GetOrderAsync(orderId)`（注文 ID 照会）は使えない。実照合には
**発注時に `DecisionId` をブローカ側の client order id として伝播**する必要がある（IADR-0074 §代替案(a)＝最高確度）。

moomoo OpenAPI は注文に任意文字列 `remark`（client order id相当）を付与でき、注文一覧照会で読み戻せる。SDK 反射で
`TrdPlaceOrder.C2S.SetRemark(string)` と `TrdCommon.Order.Remark`（`moomoo-api` 10.8.6808）の存在を実測確認した。

## 決定

**発注時に `DecisionId` を moomoo `remark` に伝播し、実プローブが OpenD(SIMULATE) の現在＋履歴注文を全市場列挙して
`remark == DecisionId` で照合する。一致→`Placed`、成功裏に列挙して一致ゼロ→`NotPlaced`、それ以外（照会失敗・
タイムアウト・部分列挙・窓を覆えない・SDK 例外）→必ず `Indeterminate`。既定は no-op プローブのまま・実プローブは
`moomoo` かつ opt-in 時のみ・SIMULATE 固定。**

1. **DecisionId → remark 伝播（shared 契約は無改修）**: shared `IBrokerAdapter` は変更せず、OrderExecution 内に
   capability ポート `IClientOrderIdBroker`（`PlaceOrderAsync(OrderIntent, Guid decisionId, ct)`）を新設する。
   `MoomooBrokerAdapter` のみ実装し、`OrderExecutionService` は `broker is IClientOrderIdBroker` で分岐して DecisionId を
   伝播する。paper（`PaperBrokerAdapter`）とテスト fake は本ポートを実装しないため**従来経路のまま無改修**で動く
   （remark 非対応でも冪等・発注は成立する）。remark 書式は `MoomooClientOrderId.From(Guid)`（`.ToString("N")`＝
   32 桁・remark 長制限に安全）に単一化し、発注側と照合側で同一の書式を使う（不一致による取りこぼしを防ぐ）。

2. **実プローブ `MoomooReservationBrokerProbe`（Worker・`IReservationBrokerProbe`）**: `IMoomooTradeClient` の新メソッド
   `FindOrderByClientIdAsync(clientOrderId, reservedAtUtc, ct)` で SIMULATE 口座の現在（`GetOrderList`）＋履歴
   （`GetHistoryOrderList`・`ReservedAt` を覆う時刻窓）を全対応市場（US/JP）で列挙し `Remark` 一致を探す:
   - 一致あり → `Placed`。moomoo 注文スナップショット（`Code/TrdSide/Qty/Price/OrderStatus/FillQty/FillAvgPrice`）から
     `BrokerOrder`（と `OrderIntent`）を再構成して返す。終端／非終端を問わず「注文が存在する」＝発注済みとして扱い、
     再発注しない。
   - **全列挙が成功裏に完了して一致ゼロ → `NotPlaced`**（確実に未発注）。
   - client の例外・タイムアウト・部分列挙（一部市場/一部照会だけ成立）→ **`Indeterminate`**（据え置き）。
   `FindOrderByClientIdAsync` は「確実に見つからない＝`null`」「照会失敗＝例外送出」を厳密に区別し、プローブは例外を
   `Indeterminate` に写像する（`OperationCanceledException` は握りつぶさず伝播）。

3. **プローブ・ポートの最小拡張**: `IReservationBrokerProbe.ProbeAsync(Guid, …)` を
   `ProbeAsync(OrderDispatchReservation, …)` に変更する。健全な `NotPlaced` 判定には履歴照会窓を予約の `ReservedAt`
   で確実に覆う必要があり（窓外の発注済み注文を見落として誤って `NotPlaced` を返すことを構造的に防ぐ）、DecisionId
   だけでは窓を決められないためである。no-op プローブ・`OrderReservationReconciler`・既存テストを追随させる（挙動不変）。

4. **既定は no-op・実プローブは opt-in**: `Broker:Provider=moomoo` かつ `Reconciliation:UseBrokerProbe=true` のときのみ
   `MoomooReservationBrokerProbe` を DI 登録する。paper／OpenD 無し／未設定は `IndeterminateReservationBrokerProbe`
   （IADR-0074 の安全既定）を維持する。実プローブ有効時も `IMoomooTradeClient`（＝OpenD 接続）を発注アダプタと
   **単一インスタンス共有**し、接続を二重化しない。

5. **SIMULATE 固定**: 列挙は既存 `BuildHeader`（`TrdEnv_Simulate`）で SIMULATE 口座に閉じる。`TrdEnv_Real` に一切
   触れない（IADR-0016 / IADR-0056 / `MoomooBrokerOptions.EnsureSimulate` の閂は不変）。

6. **CI と実基盤の切り分け**: プローブの判定ロジックと写像は fake `IMoomooTradeClient` ＋ SDK 非依存写像で単体検証する。
   `MMApiMoomooTradeClient` の実 OpenD 配線（`GetHistoryOrderList` の時刻フィルタ・接続・実注文の突合）は実基盤依存の
   ため **CI 対象外**とし、ローカル OpenD SIMULATE ＋ #82 系の統合 E2E で通す（受け入れ基準3）。本 PR は `Refs #141`。

## at-most-once（二重発注ゼロ）の担保

- 「**不明な窓では解放しない**」を構造で守る。`NotPlaced`（解放）は remark 突合が成功裏に完了し、現在＋履歴
  （`ReservedAt` を覆う窓）を全市場列挙して一致ゼロの**既知の窓**でのみ返す。照会経路のあらゆる不確実性は一律
  `Indeterminate` に倒す。
- `remark` 伝播が発注側の不変条件になったことで、発注が OpenD に受理されていれば当該注文は必ず `remark` 付きで
  現在／履歴一覧に現れる。ゆえに「成功裏の全列挙で不在」は「未受理＝未発注」と等価であり、`NotPlaced` は安全。
- リコンサイラの TOCTOU 対策（`Save` 直前の `FindByDecisionId` 再確認）は不変で、実プローブでも二重 `Save`／二重発注は
  起き得ない。既定 no-op のため、opt-in しない限り解放・終端化は phase-4 自己修復（ブローカ非依存）のみ。
- **移行上の注意**: `remark` 伝播より前に発注された注文には `remark` が無いため「不在＝未発注」の等価は成り立たない。
  本実装は SIMULATE・実弾解禁前（IADR-0056 §3）であり、そのような正規発注の蓄積は無い。実弾解禁時は remark 伝播が
  十分に浸透してから実プローブの `NotPlaced` を有効化する運用前提とする（既定 opt-in・SIMULATE 限定で担保）。
- **履歴窓の網羅**: `NotPlaced`（不在）の健全性は履歴照会が `ReservedAt` を覆うことに依存する。moomoo の
  `GetHistoryOrderList` は照会範囲に上限があり、過大な範囲は**エラー**（`RetType != 0`）で返る＝`EnsureSucceeded` が
  例外送出→`Indeterminate` に倒れる（黙って直近だけを返して不在と誤認しない）。この「範囲超過はエラーで返る」挙動は
  ローカル OpenD SIMULATE で確認する（受け入れ基準3）。運用上は滞留閾値を moomoo の履歴保持内に置く前提とする。

## 検討した代替案

- **shared `IBrokerAdapter` に client order id 引数を足す**: 影響が paper・全テスト fake に波及する。capability ポート
  `IClientOrderIdBroker`（OrderExecution 内）に閉じることで shared 契約と paper を無改修に保てるため、こちらを採る。
- **銘柄・数量・時刻窓で突き合わせる**: 予約行が intent を持たないため予約単体からは不可能。かつ確度が下がり誤終端化・
  誤解放のリスクがある。remark 突合（一意 GUID）を採る。
- **プローブ・ポートを `Guid` のまま据え置く**: 履歴窓を予約の `ReservedAt` で覆えず、古い発注済み注文を見落として
  誤って `NotPlaced` を返し得る（二重発注の芽）。`OrderDispatchReservation` を渡す最小拡張を採る。
- **実プローブでも `NotPlaced` を返さない（Placed/Indeterminate のみ）**: 最も安全だが受け入れ基準「未発注は解放」を
  実プローブで満たせない。remark 伝播により `NotPlaced` を安全に判定できるため、fail-safe を保ったまま実装する。

## 結果

- 良い: 滞留 `Reserved` を moomoo/OpenD(SIMULATE) の実照会で自動解消できる（発注済み→確定・未発注→解放）。shared 契約・
  paper・スキーマ・イベント契約は無改修（`OrderExecuted` 再利用＝監査 Consumer 追随不要）。既定は完全 no-op を維持。
- 悪い/コスト: 実 OpenD 照会（履歴時刻フィルタ・接続）の通し検証は実基盤依存でローカル SIMULATE ＋ #82 系に残る＝
  本 PR は `Refs #141`（`Closes` にしない）。`Placed` 再構成の `OrderIntent` は moomoo 注文由来のため
  `PositionEffect`/`ProductType` は既定（`Open`/`Cash`）で近似する（ローカル永続の報告用途に限られ、下流 `OrderExecuted` は
  非依存）。
- 追跡: 実 OpenD SIMULATE の E2E（受け入れ基準3）を #82 系で扱う。実弾解禁は IADR-0056 §3 の前提充足が別途必要。
