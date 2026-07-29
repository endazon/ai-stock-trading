---
title: moomoo 経路の約定状態ポーリングと台帳反映（統制上限の実効化）
type: spec
status: review
related_ids: [FR-05, FR-10, FR-12, ADR-0002]
author: endazon (with Claude Code)
created: 2026-07-29
updated: 2026-07-29
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/03_moomoo-integration.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# 仕様書: moomoo 経路の約定状態ポーリングと台帳反映

> [#270](https://github.com/endazon/ai-stock-trading/issues/270)（重大度: 高）。`Broker:Provider=moomoo`（SIMULATE）で
> 5 分ごとの判断サイクルのたびに新規発注が積み上がり、`SameDayReentry` も金額系上限も実効しない。
>
> **本作業で実弾（live）は解禁しない。** SIMULATE 固定の閂 0〜4（IADR-0111 / IADR-0016 / IADR-0056 / IADR-0060）は
> 一行も触らない。ブローカへの追加呼び出しは**状態照会（読み取り）のみ**で、発注・訂正・取消は一切足さない。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-05（発注執行）、FR-10（リスク統制＝実効しなくなっている当事者）、FR-12（ペーパートレード＝非干渉の対象）
- ADR: [ADR-0002](../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md)（証券会社連携）
- 関連 IADR: [IADR-0018](../adr/IADR-0018_portfolio-ledger-projection.md)（取引台帳と射影）／
  [IADR-0057](../adr/IADR-0057_order-dispatch-idempotency.md)（発注冪等化・at-most-once）／
  [IADR-0074](../adr/IADR-0074_reservation-reconciliation.md)（滞留リコンサイル・既定無効）／
  [IADR-0092](../adr/IADR-0092_reservation-broker-probe-moomoo.md)（moomoo 実照会プローブ）／
  [IADR-0111](../adr/IADR-0111_broker-tier-selection.md)（ブローカー階層・閂 0）／
  本作業で新規 [IADR-0112](../adr/IADR-0112_moomoo-fill-polling.md)
- 対象 Issue: [#270](https://github.com/endazon/ai-stock-trading/issues/270)（`Closes #270`）

## 現状（この変更の直前・実コードで確定）

| 面 | 実態 |
| --- | --- |
| `MMApiMoomooTradeClient.PlaceOrderAsync` | 発注直後は約定前。`MoomooOrderResult(orderId, Submitted, 0, 0m)` を返す（仕様どおり） |
| `OrderExecutionService.ExecuteAsync` | その結果を `executed_orders` に `Status=Accepted / FilledQuantity=0` で保存し、同内容の `OrderExecuted` を発行して**終わり**。以後この行は誰も更新しない |
| `OrderExecutedLedgerConsumer`（Risk） | `Status != Filled` または `FilledQuantity <= 0` は台帳に載せない → `trade_fills` 0 行 |
| `PortfolioProjection` | `trade_fills` **のみ**から `SymbolsTradedToday` / `DailyOrderedAmount` / 建玉 / `InvestedCapital` を導出 |
| `GetOrderAsync` / `QueryOrderAsync` の呼び出し元 | `OrderAmendmentService`（同期・paper 限定配線）と `MoomooReservationBrokerProbe`（**既定無効**・巡回下限 1 時間・滞留閾値 24 時間クランプ）のみ |
| `PaperBrokerAdapter` | 発注即 `Filled` を返すため、このギャップは paper では露呈しない |

**問題の核**: 「注文を出した」から「約定した」への遷移を**誰も観測していない**。moomoo は非同期に約定するので、
台帳は永久に「まだ何も取引していない」ままになり、次サイクルの統制判定が素通しになる。
リコンサイル（IADR-0074/0092）は *滞留 Reserved の事後救済*（時間単位・既定無効）であって通常運用の追跡経路ではない。

## 目的

1. moomoo 経路でも約定が `trade_fills` へ届き、`SameDayReentry` / 日次発注上限 / 段階資金上限 / 建玉が paper と同等に実効する。
2. 部分約定・全約定・取消/失効・拒否のいずれでも台帳が実態に一致する（未約定を約定として扱わない）。
3. 二重計上・取りこぼしを作らない（`DecisionId` 1:1 ＝ `executed_orders` 1 行、`OrderId` 1:1 ＝ `trade_fills` 1 行の不変を保つ）。
4. paper 経路に一切干渉しない（構造的に到達しない）。
5. fail-safe: 照会不達・不明はブローカ状態を**推測しない**（台帳を書かない・据え置いて次回再試行）。

## 設計

### 1. 約定追跡ポーラー（発注執行サービス内・新規）

リコンサイル（`OrderReservationReconciler` ＋ `OrderReservationReconciliationService`）と同じ 2 層構成に揃える。

```
Application/Polling/OrderFillPoller.cs          純オーケストレーション（MassTransit 非依存）
Application/Polling/FillPollingOptions.cs       構成（巡回間隔・追跡上限・バッチ）
Worker/Composable/Polling/OrderFillPollingService.cs   BackgroundService（IBus で発行）
```

照会は既存ポート **`IBrokerAdapter.GetOrderAsync`** のみを使う（moomoo 固有型は Application に持ち込まない）。
`MoomooBrokerAdapter.GetOrderAsync` は既に `QueryOrderAsync`（当日 `GetOrderList`・全対応市場走査・例外は null）を
包んでおり、本作業では**アダプタを変更しない**。

1 巡回:

1. `IExecutedOrderStore.FindPendingSince(since, batchSize)` で**非終端**（`Accepted` / `PartiallyFilled`）かつ
   `ExecutedAt >= now - MaxTrackingHours` の記録を古い順に取る。
2. 各記録を独立して処理（1 件の失敗でバッチを止めない＝リコンサイラと同じ流儀）:

| 照会結果 | 動作 |
| --- | --- |
| `null`（見つからない・照会失敗） | **何も書かない**・発行しない（`Unknown`）。次回巡回で再試行 |
| 状態・約定数ともに不変 | 何も書かない・発行しない（`Unchanged`）。イベント嵐を作らない |
| 状態が変化 または 約定数が増加 | `executed_orders` の該当行を更新（`Status` / `FilledQuantity` / `AveragePrice` / `SlippageRatio` 再計算 / `ExecutedAt`）し、`OrderExecuted` を発行 |
| 例外 | 件数のみ集計し据え置き（`Failed`）。次回巡回で再試行 |

`ExecutedAt` は終端化したときだけ更新する（`order.CompletedAt ?? now`。`OrderReservationReconciler.BuildRecord` と同じ導出）。
非終端の進捗では発注時刻を保持する（当日集計の帰属を発注日から動かさない）。

**約定数が減る方向の更新は行わない**（照会の順序前後・部分列挙で数量が巻き戻ることを許さない）。

### 2. 台帳側の受け口（リスク管理・最小の 1 条件）

```
現: if (m.Status != OrderStatus.Filled || m.FilledQuantity <= 0) return;   // 全量約定のみ
新: if (m.FilledQuantity <= 0) return;                                     // 約定があれば載せる
```

`EfPortfolioLedgerStore.AppendFill` は `OrderId` 主キーの**単調 upsert** にする。

- 既存行なし → 追加（現行どおり）。
- 既存行あり・`incoming.FilledQuantity > stored.FilledQuantity` → 累積約定として更新（数量・平均単価・約定時刻）。
- 既存行あり・それ以外 → 無視（**冪等**。再配送・巡回重複・順序前後で二重計上しない）。

moomoo の `FillQty` / `FillAvgPrice` は**累積値**であり差分ではない。したがって「行は 1 注文 1 行・値は最新の累積」
という表現が唯一の正しい写像であり、単調 upsert はこの表現をそのまま満たす。追記（差分行）にすると
`OrderId` 一意という既存の冪等キーを壊し、再配送で二重計上する。

この変更により以下が同時に直る。

- 部分約定が発生した時点で `SymbolsTradedToday` / `DailyOrderedAmount` / 建玉に反映される（統制が即座に効く）。
- **部分約定のまま取消/失効**した注文（`Cancelled` ＋ `FilledQuantity > 0`）が台帳から落ちない（現行は丸ごと無視＝過少計上）。
  これはポーラーの有無に関わらず既存のリコンサイル経路にも存在した取りこぼしである。

### 3. 既定と構成（`FillPolling` 節）

| キー | 既定 | クランプ | 意味 |
| --- | --- | --- | --- |
| `FillPolling:Enabled` | **true** | — | ポーリングの有効化 |
| `FillPolling:IntervalSeconds` | 30 | 5〜3600 | 巡回間隔（判断サイクル 5 分に対して十分細かい） |
| `FillPolling:MaxTrackingHours` | 24 | 1〜168 | 追跡を続ける上限。超過した非終端記録は対象外（＝リコンサイル/人手の領分） |
| `FillPolling:BatchSize` | 200 | 1〜10000 | 1 巡回の最大件数 |

**既定 true とした理由**（リポの「新規バックグラウンド処理は既定オフ」慣行からの意図的な逸脱）:

1. 本ポーラーは**統制が実効するための必要条件**であり、既定オフは「統制が効かない状態」を既定として出荷することになる。
   ここでの安全側（fail-safe）は「動かさない」ではなく「約定を統制へ届ける」側にある。
2. 副作用は**読み取り照会のみ**。発注・訂正・取消は一切増えない。照会不達は `null` → 何も書かずに据え置き。
3. paper では**構造的に**動かない（下記 4）。既定 ON で挙動が変わるのは moomoo 経路だけであり、
   その挙動変化こそが #270 の修正である。
4. 停止したい場合は `FillPolling__Enabled=false` の 1 環境変数で止まる。

`docker-compose.yml` / Helm values には設定点を**足さない**（既定で正しく動くため。本番 values の描画はバイト等価のまま）。

### 4. paper 非干渉（二重）

1. `Program.cs` は `brokerSelection.IsMoomoo` のときだけ `OrderFillPollingService` を登録する（paper では存在しない）。
2. 仮に登録されても `PaperBrokerAdapter` は即時終端（`Filled` / `Rejected`）を返すため非終端記録が生まれず、
   `FindPendingSince` は常に空＝ブローカ照会 0 回。

### 5. 冪等性・不変（二重計上と取りこぼしを作らない）

| 不変 | 担保 |
| --- | --- |
| `executed_orders` は `DecisionId` 1:1 | ポーラーは既存行を**更新**するのみ（`Save` を呼ばない）。`ExecuteAsync` 相1 の再発行も更新後の最新状態を返す |
| `trade_fills` は `OrderId` 1:1 | `AppendFill` の単調 upsert |
| 二重計上なし | 累積値を保持（差分加算しない）＋単調条件。複数レプリカ・巡回重複・MassTransit 再配送のいずれでも増えない |
| 取りこぼしなし | 終端化まで毎巡回で追跡。照会不能は書かずに再試行。終端かつ `FilledQuantity>0` は必ず載る |
| リコンサイルとの競合なし | リコンサイラは `Reserved`（未確定）予約を、ポーラーは `Completed` 済みの `executed_orders` を見る＝対象集合が交わらない。両者が同じ `OrderId` を発行しても台帳は単調 upsert で冪等 |
| `Shared.Contracts` 不変 | 新規イベント無し（`OrderExecuted` を再利用）。イベント契約テスト（IADR-0079）・`MessageUrn` 不変 |

下流の増分: 1 注文あたり `OrderExecuted` が複数回発行され得る（受付 → 部分 → 全量）。
監査は `MessageId` 冪等のため**時系列として正しく増える**（FR-11 の全イベント時系列記録）。通知は状態遷移ごとに 1 通で、
全量約定のみ Info・他は Warning（既存 `NotificationFormatter` のまま）。これは既にリコンサイル経路で起き得た形であり、
新しい下流契約は導入しない。

### 6. 影響範囲

- `OrderExecutionService.Application/Ports/IExecutedOrderStore.cs`（`FindPendingSince` / `UpdateOutcome` 追加）
- 同 `Adapters/InMemoryExecutedOrderStore.cs`（実装追随）
- 同 `Polling/`（新規: `OrderFillPoller` / `FillPollingOptions` / `OrderFillPollResult`）
- `OrderExecutionService.Worker/Foundation/Persistence/EfExecutedOrderStore.cs`（実装追随・**スキーマ変更なし＝Migration 無し**）
- 同 `Composable/Polling/OrderFillPollingService.cs`（新規）／`Program.cs`（moomoo 限定登録）
- `RiskManagementService.Worker/Composable/Steps/OrderExecutedLedgerConsumer.cs`（条件を約定数へ）
- 同 `Foundation/Persistence/EfPortfolioLedgerStore.cs`（単調 upsert）／`Application/Ports/IPortfolioLedgerStore.cs`（契約コメント）
- `docs/functional/FR-10_risk-controls.md`・`docs/tests/FR-10_risk-guard-core-tests.md`（約定伝播が統制の前提であることを明記）

## テスト（TDD・受け入れ基準の写像）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 未約定 → 全量約定を追跡して台帳へ届く | `OrderFillPollerTests`: `Accepted(0)` → `Filled(N)` で記録更新＋`OrderExecuted` 1 件 |
| 2 | 部分約定が反映される | `OrderFillPollerTests`: `Accepted(0)` → `PartiallyFilled(300)` → `Filled(1000)` で 2 回発行・数量は累積 |
| 3 | 変化なしは発行しない | `OrderFillPollerTests`: 同一状態・同一約定数で発行 0・更新 0 |
| 4 | 照会不能は推測しない | `OrderFillPollerTests`: `null` / 例外で更新 0・発行 0・次回巡回で再試行できる |
| 5 | 数量は巻き戻らない | `OrderFillPollerTests`: 約定数が減る応答を無視 |
| 6 | 終端・追跡期限切れは照会しない | `OrderFillPollerTests` / `EfExecutedOrderStoreTests`: `Filled`/`Cancelled`/`Rejected` と `MaxTrackingHours` 超過は対象外 |
| 7 | 終端時刻の採り方 | `OrderFillPollerTests`: `CompletedAt` 優先・無ければ巡回時刻・非終端では発注時刻を保持 |
| 8 | 台帳が部分約定・取消付き部分約定を載せる | `PortfolioLedgerConsumersTests`: `PartiallyFilled`/`Cancelled(FilledQuantity>0)` を記録・`Accepted(0)` は不記録 |
| 9 | 二重計上しない | `EfPortfolioLedgerStoreTests` 相当: 同一 `OrderId` の再送・数量減少は 1 行のまま最大値を保持 |
| 10 | **統制が moomoo 経路で実効する（回帰）** | `MoomooFillControlRegressionTests`（Risk Application）: 「発注 → Accepted(0) では統制が緩まない → 約定後は `SameDayReentry` で拒否・`DailyOrderedAmount` が減る」を paper 相当のシーケンスと対比して固定 |
| 11 | paper 非干渉 | `OrderFillPollingServiceTests`: 無効時は巡回しない。`Program` 配線は moomoo 限定（`BrokerFactoryTests`/構成テストで確認） |
| 12 | moomoo 状態写像の通し | `MoomooBrokerAdapterTests` 相当: `Submitted → FilledPart → FilledAll` がポーラー経由で `Accepted → PartiallyFilled → Filled` になる |

## 受け入れ基準チェック

- [x] moomoo（SIMULATE）で発注した注文の約定が `trade_fills` に届く
- [x] `SameDayReentry` / 日次発注上限 / 段階資金上限 / 建玉が moomoo 経路で paper と同等に実効する
- [x] 部分約定・未約定・取消/失効・拒否のエッジが台帳へ正しく（過大にも過少にも）反映される
- [x] 二重計上・取りこぼしがない（`DecisionId` 1:1・`OrderId` 1:1 の不変を維持）
- [x] paper 経路の挙動が不変（構造的に到達しない）
- [x] SIMULATE / 実弾 OFF が不変（閂 0〜4 に差分ゼロ・発注/訂正/取消の呼び出しを増やさない）
- [x] `Shared.Contracts` 不変・新規イベント無し・DB スキーマ変更無し（Migration 無し）
- [x] `dotnet build` / `dotnet test` / `dotnet format` green・CI green・gitleaks green

## スコップ外

- 発注（`Accepted`）時点で枠を消費する方式（issue の案 2）。取消・拒否時の戻しが要り、台帳の意味（＝約定実績）が変わる。
- リコンサイル（IADR-0074/0092）の下限クランプ緩和（issue の案 3）。本ポーラーが通常運用の追跡を担うため不要。
- 未約定注文の自動取消・時限失効の駆動。
- 取引日境界の市場別解釈（[#249](https://github.com/endazon/ai-stock-trading/issues/249) に残置）。
- 実弾（live）の解禁、および稼働中クラスタへの適用（デプロイは利用者の判断）。
