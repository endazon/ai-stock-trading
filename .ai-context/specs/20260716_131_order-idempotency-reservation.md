---
title: 発注の冪等化（発注前 DecisionId 予約・予約→発注→確定の3相）— Issue #131
type: spec
status: review
related_ids:
  - FR-05
  - UC-01
  - UC-02
  - ADR-0002
  - ADR-0003
  - IADR-0016
  - IADR-0056
  - IADR-0057
author: claude
created: 2026-07-16
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-05: 発注執行)
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
related_specs:
  - "../adr/IADR-0057_order-dispatch-idempotency.md（本 PR の設計判断）"
  - "../adr/IADR-0016_safe-broker-execution.md（安全既定 paper・moomoo ゲート）"
  - "../adr/IADR-0056_moomoo-simulate-poc-complete-real-gated.md（実弾解禁の前提として本件を明記）"
  - "20260715_13_moomoo-broker-adapter.md（#13 の moomoo アダプタ）"
---

# 仕様書: 発注の冪等化（Issue #131）

## 起点となる計画書（トレーサビリティ）

- 機能要求: **FR-05**（発注執行）／ユースケース: UC-01・UC-02（損切りの Close も同一経路）
- 関連 ADR: **ADR-0002**（moomoo OpenAPI）／**ADR-0003**（承認済み注文のみ発注）
- 関連 IADR: **IADR-0016**（安全既定 paper・実弾防止ゲート）／**IADR-0056**（SIMULATE PoC 完了・
  §3 が「実弾解禁の前提＝発注の冪等化」と明記）／**IADR-0057**（本 PR で新規作成）
- Issue: [#131](https://github.com/endazon/ai-stock-trading/issues/131)

## 目的・背景

現状の冪等性は **発注後の `DecisionId` 照合のみ**である（`OrderExecutionService.ExecuteAsync` 冒頭の
`store.FindByDecisionId`）。同ファイルのコメントが窓の存在を明示している:

> 注: ブローカ発注は成功したが Save 前に失敗した窓は本チェックでは捕捉できない（記録が無いため）。
> 実発注（moomoo）では outbox 等の発注前予約が必要になる（IADR-0016 の後続）。

すなわち「**ブローカ発注成功 → 永続化失敗**」でプロセスが落ちると、`executed_orders` に行が無いまま
MassTransit の再配送（`UseAiStockTradingRetry`＝2s/10s/30s の3回）が同じ `OrderApproved` を再処理し、
**同一 `DecisionId` で二重発注**し得る。SIMULATE では実害が限定的だが、実弾（`TrdEnv_Real`）では
そのまま二重建玉＝実損になるため、IADR-0056 §3 は本件を解禁の前提条件として挙げている。

## 対象範囲

**対象（本 PR）**

- `IOrderReservationStore` ポート（Application 層）と、その **EF 実装**（`order_dispatch_reservations` 表・
  `DecisionId` を主キー＝一意制約）＋インメモリ実装。
- `OrderExecutionService` を **予約 → 発注 → 確定** の3相にする。予約はブローカ発注の**前に**独立トランザクションで
  コミットする。
- 予約済みだが未確定（＝発注済みか否か不明）の再処理を **`OrderDispatchReservationConflictException` で拒否**し、
  **再発注しない**（fail-safe＝at-most-once）。
- MassTransit 再配送で二重発注・二重計上しないことをテストで固定する（#131 の受け入れ基準）。
- IADR-0056 §3 の実弾解禁前提に、本件充足と残条件を追記。

**対象外（後続 issue に明示分離）**

- **ブローカ照会による自動リコンサイル**（stuck 予約の自動解消）。実 OpenD／実 API 依存のため本 PR では扱わず、
  **[#141](https://github.com/endazon/ai-stock-trading/issues/141)** に切り出した（本 PR では `_error` キュー滞留＋
  人手のリコンサイルへ回す）。
- **実弾（`TrdEnv_Real`）の解禁そのもの**。別 IADR＋明示 config が必要（IADR-0056 §3 据え置き）。
- moomoo への client order id 伝播（OpenAPI の対応調査が必要・リコンサイル issue と同時に扱う）。

## 設計

### 3相（予約 → 発注 → 確定）

```
1. 完了照合  : executed_orders に DecisionId があれば既存結果を再発行（現挙動・後方互換）
2. 予約      : order_dispatch_reservations へ DecisionId を INSERT してコミット（一意制約が権威）
               → 失敗（既存行あり）＝発注済みか不明 → 例外で拒否（再発注しない）
3. 発注      : broker.PlaceOrderAsync
4. 確定      : executed_orders へ Save →（同 DecisionId の）予約を Completed に更新
```

再配送時の網羅:

| 落ちた位置 | 再処理時の状態 | 挙動 | ブローカ発注回数 |
| --- | --- | --- | --- |
| 発注前（予約後） | 予約=Reserved・結果なし | 例外で拒否 | 1 回未満（0 回のまま） |
| **発注成功→Save 失敗** | 予約=Reserved・結果なし | **例外で拒否** | **1 回**（← 本件が塞ぐ窓） |
| Save 成功→Complete 失敗 | 予約=Reserved・**結果あり** | 相1で既存結果を再発行 | 1 回 |
| 全成功 | 予約=Completed・結果あり | 相1で既存結果を再発行 | 1 回 |

### なぜ「拒否」か（at-most-once を選ぶ）

予約が Reserved のまま結果が無い状態は、「発注していない」と「発注したが記録できていない」の**区別が付かない**。
実弾では *二重発注*（実損）より *未発注の取りこぼし*（機会損失）の方が可逆であるため、**再発注しない**側に倒す。
拒否は例外→再試行3回→`_error` キューへ送られ、人手（将来は自動リコンサイル）で解消する。

### 二重の権威を置く理由

相1（`executed_orders` 照合）は予約表の導入前に既に存在した行にも効くため、**後方互換のために残す**。
予約表は「発注の窓」だけを守る。役割は次のとおりで重複しない:

- `executed_orders` = **完了**の権威（結果の再発行元）
- `order_dispatch_reservations` = **発注着手**の権威（二重発注の防止）

## 受け入れ基準

- [x] 同一 `OrderApproved` の再処理で、ブローカへの発注が高々1回に限定される（**発注成功後の永続化失敗を跨いでも**）
- [x] 予約は必ずブローカ発注の**前**にコミットされる
- [x] Save 成功・Complete 失敗を跨いだ再処理は、既存結果を再発行する（拒否しない）
- [x] 予約済み未確定の再処理は例外で拒否し、ブローカへ発注しない
- [x] 既存挙動（ペーパー約定・Rejected・Close・スリッページ・完了済み再処理の再発行）が回帰しない
- [x] EF 実装で `DecisionId` の一意制約により並行予約が高々1つに限定される（契約とラウンドトリップは単体で固定。
      実 PostgreSQL の並行排他は実基盤 E2E 側の担保）
- [x] `TrdEnv_Real` 解禁は本件＋別 IADR＋明示 config を前提とする旨が IADR に記載される（IADR-0056 §3 / IADR-0016）
- [x] 既定は安全側（本 PR はブローカ既定 paper・moomoo ゲート・SIMULATE 固定を一切変更しない）

## テスト方針

TDD（テスト先行）。実コンテナ／実 API 非依存の単体で受け入れ基準を固定する。

- 単体（Application・xUnit + FluentAssertions）: 予約ストアのフェイクで各クラッシュ窓を再現する。
  - 発注成功→Save 失敗→再処理 → `PlaceCount == 1` かつ2回目は例外（**本件の中核**）
  - 予約済み未確定の再処理 → 発注 0 回・例外
  - Save 成功→Complete 失敗→再処理 → 既存結果を再発行・発注1回
  - 予約はブローカ発注前にコミットされる（順序の固定）
- 単体（Worker・EF InMemory／`EfExecutedOrderStoreTests` に準拠）: 予約の往復・重複予約が false・Completed 更新。
- 一意制約（実 PostgreSQL 依存）は **実基盤 E2E（#82 基盤）側の担保**とし、CI の単体からは切り分ける。

## 計画書との差異

- なし（IADR-0056 §3 が前提条件として挙げた項目を実装するもの）。

## 未決事項

- stuck 予約の自動リコンサイル（ブローカ照会）→ **#141 に起票済み**。
- moomoo への client order id 伝播（OpenAPI 対応の調査）→ #141 に含めた（照会の突き合わせキーの前提となるため）。
