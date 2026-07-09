---
title: 市場監視サービスのコア（価格変動判定・損切りライン検知・イベント契約・オーケストレーション）
type: spec
status: review
related_ids: [FR-03, FR-13, FR-10, UC-02, ADR-0001, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/04_workflows/02_event-driven-trading.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 市場監視サービスのコア

> Issue [#10](https://github.com/endazon/ai-stock-trading/issues/10)（FR-03 市場監視）の **Slice A**。
> インフラ非依存の判定コア（価格変動・損切りライン）とアプリケーション層（ポート＋オーケストレーション）を実装し、
> ユニットテストで受け入れ基準のロジックを担保する。あわせて **`StopLossTriggered` / `PriceMovementDetected` の
> イベント契約**を追加し、#12 Slice C（損切りの機械執行）の依存を解消する。Worker ホスト（ポーリング・MassTransit
> 発行・永続化）は Slice B。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-03（価格変動監視・即時起動）、FR-13（監視対象・閾値の設定可能）、FR-10（損切りはリスク統制）
- ユースケース（UC）: UC-02（価格変動トリガー取引）
- 業務フロー: `04_workflows/02_event-driven-trading.md`（損切りは LLM を迂回して機械執行）
- ADR: ADR-0003（損切りは機械的に執行・AI を経由しない）、ADR-0001（platform 再利用）
- 関連 IADR（本作業で新規）: [IADR-0014](../adr/IADR-0014_market-monitor-events-and-boundary.md)（イベント契約と責務境界）
- 対象 Issue: #10（Slice A）、依存解消先: #12 Slice C

## 目的・背景

市場監視は監視銘柄の価格を定期取得し、(1) 保有銘柄が**損切りライン**に到達したら LLM を迂回してリスク管理へ直接連携、
(2) 変動率が**閾値**（前回判断時点価格比・既定 ±3%）を超えたら価格変動イベントを発行して取引サイクルを即時起動する
（クールダウン内は抑制）。本 Slice はこの判定・オーケストレーションのロジックをインフラ非依存で実装する。

## 対象範囲

### イベント契約（`AiStockTrading.Shared.Contracts/Events`）

- `PriceMovementDetected(Guid EventId, string Symbol, Market Market, decimal Price, decimal BaselinePrice, decimal ChangeRatio, DateTimeOffset DetectedAt)`
  — FR-03。取引判断サービス（#11）／サイクル（#21）が購読して対象銘柄限定のサイクルを起動する。
- `StopLossTriggered(Guid EventId, string Symbol, Market Market, TradeSide PositionSide, int Quantity, decimal Price, decimal StopLossPrice, DateTimeOffset DetectedAt)`
  — FR-03/FR-10/ADR-0003。リスク管理（#12 Slice C）が購読し、LLM を迂回して決済（Close）注文を発行する。

### ドメイン（新規 `MarketMonitorService.Domain`）

- `PriceMovementEvaluator.Evaluate(currentPrice, baselinePrice, thresholdRatio)` → `PriceMovement`（変動率・超過判定）。
  変動率 = (現在値 − 基準値) / 基準値。|変動率| ≥ 閾値 で超過。基準値は「前回 AI 判断時点の価格」（前日終値ではない）。
- `StopLossEvaluator.IsTriggered(position, currentPrice)` — ロング（Buy 建て）は `現在値 ≤ 損切り価格` で到達。
  ショート（Sell 建て・信用有効時）は `現在値 ≥ 損切り価格`。建玉方向で対称に判定する。
- 値オブジェクト: `MonitoredSymbol`（銘柄・市場）、`HeldPosition`（銘柄・市場・建玉方向・数量・取得価格・損切り価格）、
  `MarketMonitorSettings`（閾値・クールダウン・監視銘柄。既定は §5：閾値 0.03・クールダウン 15 分）。

### アプリケーション（新規 `MarketMonitorService.Application`）

- ポート: `IMonitoredSymbolStore`（監視設定）／`IPositionStore`（保有・損切り価格。**#13/#17 まではプレースホルダ**）／
  `IPriceBaselineStore`（銘柄別の前回判断時点価格）／`ICooldownStore`（銘柄別の最終トリガー時刻）／`IClock`。
  `IMarketDataSource`（価格取得）は既存契約を用いる。
- `MarketMonitorService.EvaluateRoundAsync()` — 1 巡回で以下を行い、発行すべきイベントを `MonitorRoundResult` として返す:
  1. 保有銘柄の損切り判定（到達 → `StopLossTriggered`）。損切りは**クールダウン・kill switch に関わらず**常に評価する（フェイルセーフ）。
  2. 監視銘柄の変動判定（基準値比・閾値超過 かつ クールダウン外 → `PriceMovementDetected` ＋ クールダウン更新）。
  3. 価格取得失敗（`GetLatestQuoteAsync` が null）はその銘柄をスキップ（監視継続）。
- インメモリ実装（ストア）を同梱。

## 受け入れ基準（本 Slice で検証）

- [ ] 変動率が閾値を超過し、かつクールダウン外なら `PriceMovementDetected` を生成する（基準は前回判断時点価格）。
- [ ] クールダウン中は変動超過でも価格変動イベントを生成しない。
- [ ] 保有銘柄が損切りラインに到達したら `StopLossTriggered` を生成する（ロング/ショート対称）。
- [ ] 損切りはクールダウンや変動判定と独立に評価される（フェイルセーフ）。
- [ ] 価格取得失敗の銘柄はスキップし、他銘柄の監視を継続する。
- [ ] 監視対象・閾値・クールダウンが設定で変更できる（`IMonitoredSymbolStore`）。

## 対象外（後続）

- **Slice B**: Worker ホスト（`BackgroundService` ポーリングループ・市場開場判定・MassTransit 発行・`TradeDecisionMade`
  購読による基準値更新・永続化）。実行時基盤は test-support shim（本番非使用・IADR-0013）を用いる。
- **#12 Slice C**: `StopLossTriggered` を購読した決済注文の発行（本 Slice で契約のみ提供）。
- 実データ供給: `IPositionStore`（保有・損切り価格）の実体は #13/#17 連携。本 Slice はプレースホルダ／テストスタブ。
- moomoo リアルタイム市況アダプタ（`IMarketDataSource` 実装）は #13 と併せて別途。

## テスト方針

- xUnit + FluentAssertions。`IClock`・各ストア・`IMarketDataSource` をテストダブルで固定し、閾値・クールダウン・
  損切り・取得失敗スキップを決定的に検証する。既存テスト（計 115）を緑に保つ。

## 関連仕様

- 実装ADR: [IADR-0014](../adr/IADR-0014_market-monitor-events-and-boundary.md)
- 依存解消先: #12 Slice C（[20260710_risk-management-worker](20260710_risk-management-worker.md) の後続）
- 参考: 損切り迂回は [20260709_risk-management-application](20260709_risk-management-application.md) のフェイルセーフ方針と対応

## 未決事項

- 基準値（前回判断時点価格）の更新契機は Slice B で `TradeDecisionMade` 購読により実装する。
- 損切り価格の算出（取得価格 − 3%等）と保有データの供給は #13/#17 で確定する。本 Slice は損切り価格を所与とする。
- 監視間隔・市場開場カレンダーは Slice B（#21 の市場カレンダーと整合）で確定する。
