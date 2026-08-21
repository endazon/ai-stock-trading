---
title: IADR-0074 Reserved 滞留の自動リコンサイルはプローブ・ポート＋fail-safe 既定 no-op で行い、実照会は後続へ分離する
type: impl-adr
status: Accepted
related_ids: [FR-05, UC-01, UC-02, ADR-0002, ADR-0003, IADR-0016, IADR-0056, IADR-0057, IADR-0059, IADR-0067]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0074: Reserved 滞留の自動リコンサイルはプローブ・ポート＋fail-safe 既定 no-op で行い、実照会は後続へ分離する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・方針「実弾は撃たない」「二重発注を絶対に起こさない」）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-05**（発注執行）、UC-01 / UC-02、ADR-0002（ブローカ選択）、ADR-0003（承認済み注文のみ発注）
- 対象 Issue: [#141](https://github.com/endazon/ai-stock-trading/issues/141)
- 前提 IADR: [IADR-0057](IADR-0057_order-dispatch-idempotency.md)（**本件を後続として明示**・at-most-once）、
  [IADR-0059](IADR-0059_dedupe-retention-purge.md)（**Reserved は時間経過で消さない**・解消は本件か人手）、
  [IADR-0056](IADR-0056_moomoo-simulate-poc-complete-real-gated.md)（§3 が実弾解禁の前提として本件を挙げる）、
  [IADR-0067](IADR-0067_order-lifecycle-telemetry.md)（訂正・取消の配管）
- 関連仕様書: [20260718_141_reservation-reconcile](../specs/20260718_141_reservation-reconcile.md)

## コンテキストと課題

IADR-0057 の3相冪等化により、予約が `Reserved` のまま結果が無い状態は「未発注」と「発注済みだが記録できていない」の
**区別が付かない**。再処理は再発注せず拒否（at-most-once）され、`_error` キューに滞留して**人手で解消**するしかない。
IADR-0059 は「Reserved は時間経過で消してはならない（消せば再配送で二重発注＝実損）」と定め、その解消を
**本 issue（#141）の自動リコンサイル**か人手に委ねている。

**核心的な制約（client order id 調査）**: 滞留 `Reserved` 予約が持つのは `DecisionId` のみで、ブローカ注文 ID を
持たない。現状 `MoomooOrderRequest` は client order id を伝播せず、`IBrokerAdapter.GetOrderAsync(orderId)` は
**ブローカ注文 ID による照会**のため、注文 ID を持たない滞留 `Reserved` を直接は照会できない。実照会には
(a) 発注時に `DecisionId` を client order id として伝播 → それで照会、または (b) 銘柄・数量・時刻窓での突き合わせ、の
いずれかが要り、**双方とも実 OpenD 依存**である。これは受け入れ基準3（OpenD SIMULATE E2E）の領域であり、
本リポの fail-safe 既定（Broker=paper・外部連携空=no-op）と「実弾は撃たない」方針の下では、実照会は
**後続・#82 系 E2E へ分離**すべきである。

## 決定

**リコンサイルを「滞留検出 → プローブ照会 → 3値による解消」の機構として実装し、プローブは差し替え可能な
ポート `IReservationBrokerProbe` として抽象化する。既定実装は常に `Indeterminate` を返す no-op とし、
実 OpenD 照会は opt-in の後続とする。定期実行は既定無効。**

1. **プローブ・ポート（3値）**: `IReservationBrokerProbe.ProbeAsync(decisionId)` は
   `ReservationProbeResult`（`Outcome ∈ {Placed, NotPlaced, Indeterminate}` ＋ `Placed` 時の `BrokerOrder`）を返す。
   - `Placed`: ブローカに当該注文が**確実に存在**する（発注済み）。
   - `NotPlaced`: ブローカに当該注文が**確実に存在しない**（未発注）。
   - `Indeterminate`: 照会不達・判定不能。**この時は何もしない**。

2. **既定 no-op プローブ**: `IndeterminateReservationBrokerProbe` は常に `Indeterminate` を返す。
   これにより、実照会が配線されるまで**予約は決して自動解放・終端化されない**（プローブ経路が発火しない）。
   これが fail-safe の要（構造上 `NotPlaced` を返し得ない＝二重発注を招く解放が起きない）。

3. **リコンサイラの判定（`OrderReservationReconciler`・Application）**: 滞留 `Reserved`
   （`ReservedAt < stallCutoff`）を走査し、各予約について:
   - `executed_orders` に**記録あり**（`FindByDecisionId != null`）→ phase-4（Save 成功・MarkCompleted 失敗）の
     **自己修復**。ブローカ照会せず `MarkCompleted` ＋ `OrderExecuted` 再発行。
   - 記録なし → プローブ照会:
     - `Placed(order)` → `ExecutionRecord` を保存し `MarkCompleted`、`OrderExecuted` を発行（**確定**）。
       照会（`ProbeAsync`）は実装次第で有意な待ち時間を持つ非同期になり得るため、`Save` の**直前**に
       `FindByDecisionId` を再確認する（TOCTOU 対策）。照会待機中に通常フローが同一 `DecisionId` を確定していれば、
       二重 `Save`（`executed_orders` 主キー競合）を避け自己修復（既存記録で `MarkCompleted`）に倒す。
     - `NotPlaced` → 予約を `Release`（削除）。ブローカ呼び出しはしない（未発注＝取り消す対象がない）。
       解放後は元の `OrderApproved` 再配送が改めて予約→発注できる（正しく再発注される）。
     - `Indeterminate` → **据え置き**（人手/`_error` の現行安全側を壊さない）。
   - **各予約は独立処理**: 1 件の例外（照会・保存失敗等）でバッチ全体を止めない（try/catch で分離し、失敗件数のみ
     集計して Worker がログ。未処理の予約は `Reserved` のまま＝据え置きで二重発注は起きない）。次回巡回で再試行する。

4. **定期実行は既定無効**: `OrderReservationReconciliationService`（BackgroundService）は IADR-0059 の
   retention と同型。`Reconciliation:Enabled=false` が既定で、有効化しても no-op プローブ下では
   `Placed/NotPlaced` 経路は発火しない（自己修復経路のみ作動＝ブローカ非依存で安全）。滞留閾値は再配送窓
   （約42秒）＋`_error` 滞留の外側に置き、下限クランプ（1 時間）で設定ミスに耐える。

5. **新イベントを足さない**: 終端化は既存の `OrderExecuted`（監査済み・Risk/Notification が冪等消費）を再利用する。
   新イベントを足すと監査 Consumer 追随（AuditConsumerCoverageTests）が要り、滞留の可観測性は当面**構造化ログ**で足りる。
   Worker 層が `OrderExecuted` を発行する（Application は MassTransit 非依存の既存レイヤリングを維持）。

6. **スキーマ変更なし**: 既存 `order_dispatch_reservations` と `State` インデックス（#131 で作成・「滞留 Reserved を
   洗い出すための検索用」と明記）をそのまま使う。走査（`FindStalledReserved`）・解放（`Release`）はストア I/F に追加する。

## 検討した代替案

- **発注時に `DecisionId` を client order id として伝播し、それで照会する**: 最も確度が高いが、`MoomooOrderRequest` と
  OpenAPI 配線・実 OpenD 依存で、受け入れ基準3（SIMULATE E2E）の領域。本 PR では **後続へ分離**し、プローブ・
  ポートの差し替えで受けられる形にした。
- **銘柄・数量・時刻窓で突き合わせる**: client order id を伝播できない場合の代替。確度が下がり誤終端化・誤解放の
  リスクがある。プローブ実装の一形態として後続で検討可能だが、既定にはしない。
- **時間経過で Reserved を解放する**: IADR-0059 が明確に禁じる（消せば二重発注）。採らない。
- **プローブを 2 値（発注済み/未発注）にする**: 「不明」を表現できず、照会不達を安全側に倒せない。3値必須。

## 結果

- 良い: 滞留 `Reserved` の自動解消**機構**が入り、実照会プローブを差し替えるだけで有効化できる。既定は
  完全な no-op（無効＋不確定）で、二重発注を招く解放は構造上起き得ない。新イベント・スキーマ変更なし。
- 悪い/コスト: 既定構成では自動解消は **phase-4 自己修復のみ**（ブローカ照会経路は no-op）。実照会・OpenD SIMULATE
  E2E（受け入れ基準3）は後続に残る＝本 PR は `Refs #141`（`Closes` にはしない）。専用メトリクス Meter・通知イベントは
  監査追随コストを避けて当面ログに留める。
- 追跡: 実 OpenD プローブ（client order id 伝播 or 時刻窓突合）＋ OpenD SIMULATE E2E を #82 系の後続で扱う。
