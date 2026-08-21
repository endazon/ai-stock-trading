---
title: 発注執行サービス（OrderApproved 購読 → ブローカ発注 → OrderExecuted 発行・注文/スリッページ永続化）
type: spec
status: review
related_ids: [FR-05, FR-12, UC-01, UC-02, ADR-0001, ADR-0002, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - planning:projects/ai-stock-trading/06_technical/03_moomoo-integration.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
---

# 仕様書: 発注執行サービス

> Issue [#13](https://github.com/endazon/ai-stock-trading/issues/13)（FR-05）の **Slice A**。`OrderApproved`（リスク管理
> #12・損切りの Close 含む）を購読し、`IBrokerAdapter` でブローカへ発注、`OrderExecuted` を発行する稼働サービスを組む。
> **安全既定はペーパー**（`PaperBrokerAdapter`）。moomoo 実発注は ADR-0002 が **Proposed**（OpenD PoC が Accepted 条件）
> かつ「実弾は撃たない」方針のため、本 Slice では**構成で明示的にゲートし実装しない**（後続・PoC 連動）。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-05（発注・注文状態追跡）、FR-12（ペーパートレード）
- ユースケース（UC）: UC-01/UC-02（取引サイクルの発注段）
- ADR: ADR-0001（platform 再利用・Database per Service）、ADR-0002（**Proposed**: moomoo は OpenD PoC 成功が Accepted 条件）、ADR-0003（承認済み注文のみ発注）
- 関連 IADR: [IADR-0007](../adr/IADR-0007_broker-rejection-vs-risk-rejection.md)（証券会社拒否 = OrderStatus.Rejected）、本作業で新規 [IADR-0016](../adr/IADR-0016_safe-broker-execution.md)（安全既定＝ペーパー・moomoo は実弾を撃たない）
- 対象 Issue: #13（Slice A）

## 目的・背景

取引パイプラインの下流（リスク管理が承認した注文を実際にブローカへ送り、結果を確定して発行する）を実装する。資金を扱う
ため**安全側**で設計する: 既定はペーパー（`PaperBrokerAdapter`・参照価格で即時約定）、moomoo 実発注は ADR-0002 の PoC 完了・
Accepted まで**実装せず、構成で選ぶと安全に停止**する（実弾防止）。注文実体とスリッページを永続化し、月報レビュー（FR-16）の
データ源とする。

## 対象範囲

新規サービス `OrderExecutionService`（`AiStockTrading.OrderExecution.*`）。ドメイン／アプリ／Worker の三層。

### ドメイン（`OrderExecutionService.Domain`）

- `SlippageCalculator.Compute(plannedPrice, averageFillPrice, side)` — 実効スリッページ（計画価格と平均約定価格の差）を
  取引毎に算出する（アーキ概要「執行方針」・受け入れ基準「スリッページを取引毎に記録」）。買いは高く約定＝不利、売りは安く
  約定＝不利を符号で表す。
- `ExecutionRecord`（注文実体＋スリッページの記録値オブジェクト）。

### アプリケーション（`OrderExecutionService.Application`）

- ポート: `IBrokerAdapter`（既存 Contracts ポート）／`IExecutedOrderStore`（注文/スリッページの永続化）／`IClock`。
- `OrderExecutionService.ExecuteAsync(OrderApproved)`:
  1. `broker.PlaceOrderAsync(intent)` で発注（Close も同一経路）。
  2. `BrokerOrder` から `OrderExecuted`（DecisionId 相関・Status/FilledQuantity/AveragePrice）を組み立てる。
  3. スリッページを算出し、注文実体＋スリッページを `IExecutedOrderStore` に永続化する。
  4. `OrderExecuted` を返す（発行は Worker）。

### Worker（`OrderExecutionService.Worker`）

- `OrderApprovedConsumer`（`IConsumer<OrderApproved>`）→ `ExecuteAsync` → `OrderExecuted` を `Publish`。
- **ブローカ選択（構成 `Broker:Provider`・既定 `paper`）**: `paper` は `PaperBrokerAdapter`。`moomoo` は **未実装ゲート**
  （選択すると起動時に明示的な例外で停止し「OpenD PoC 完了・ADR-0002 Accepted まで利用不可」を告知＝実弾防止）。IADR-0016。
- EF 永続化: `ExecutedOrderRow`（注文実体＋スリッページ）＋ `InitialCreate` マイグレーション（専有 DB `order_execution_svc`）。
- 実行時基盤は test-support shim（本番非使用・IADR-0013）を用いる。認可エンドポイントは本 Slice では設けない（照会 API は後続）。

## 受け入れ基準

CI で緑にする範囲（ユニット＋fake/paper アダプタ＋MassTransit テストハーネス）:
- [ ] `OrderApproved` を受けると `PaperBrokerAdapter` で発注し `OrderExecuted`（Filled）を発行する。
- [ ] ブローカ拒否（数量/価格不正）は `OrderExecuted`（Rejected）として発行される（IADR-0007）。
- [ ] Close（損切り）注文も同一経路で発注・発行される。
- [ ] スリッページが取引毎に算出・永続化される（買い/売りの不利方向を符号で表す）。
- [ ] `Broker:Provider=moomoo` を選ぶと起動時に安全に停止する（実弾防止ゲート）。
- [ ] 既存テスト（現行数）を緑に保つ。

実 API/実コンテナ前提（CI 既定では実行しない）:
- [ ] moomoo デモ環境（`TrdEnv.SIMULATE`）での発注→状態追跡→約定イベント（ADR-0002 PoC・後続）。
- [ ] RabbitMQ/Postgres E2E（Testcontainers・#24）。

## 対象外（後続）

- **moomoo 実発注アダプタ**（OpenD ゲートウェイ・C# SDK・`TrdEnv.SIMULATE` PoC・ADR-0002 の Accepted 化）。実弾は撃たない。
- API 認証情報の Vault 秘匿（NFR セキュリティ・#24 連携）。
- 執行方針の詳細（マーケタブルリミット・寄付/昼休け直後の成行抑制）は moomoo アダプタ実装と併せて確定。
- 注文状態の照会 API（UC）・非同期約定（Accepted→PartiallyFilled→Filled）の状態遷移追跡は後続（Paper は即時終端）。
- **`IPortfolioStateProvider`/`IPositionStore` の実データ供給**: 本サービスは `OrderExecuted` を発行して**データ源**となる。
  #12/#10 のプレースホルダ置換（`OrderExecuted` 購読でポジション/損益を射影）は各サービス側の後続作業。

## テスト方針

- `SlippageCalculator` は純粋関数として単体検証（買い/売り・有利/不利・ゼロ）。
- `OrderExecutionService.ExecuteAsync` は `PaperBrokerAdapter` ＋インメモリ store で検証（Filled/Rejected/Close/スリッページ）。
- `OrderApprovedConsumer` は MassTransit `ITestHarness` で `OrderExecuted` 発行を検証。
- moomoo ゲートは「選択で起動失敗」をユニット検証（実弾防止）。

## 関連仕様

- 連携元: [20260709_risk-management-application](20260709_risk-management-application.md)（`OrderApproved` 発行元）、[20260710_stop-loss-execution](20260710_stop-loss-execution.md)（Close の `OrderApproved`）
- ペーパーブローカ: [20260709_paper-broker-validation](20260709_paper-broker-validation.md)
- 実装ADR: [IADR-0016](../adr/IADR-0016_safe-broker-execution.md)、[IADR-0007](../adr/IADR-0007_broker-rejection-vs-risk-rejection.md)

## 未決事項

- moomoo PoC（日本株 API 発注範囲・米国株信用・差金決済実装・海外 IP 可否）は ADR-0002 の未決。PoC 完了で実アダプタと Accepted 化。
- 認証情報の Vault 秘匿方式・非同期約定の状態遷移追跡は後続で確定。
