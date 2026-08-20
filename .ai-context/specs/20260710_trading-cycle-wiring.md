---
title: 取引サイクルの配線 Slice A（定時/イベント駆動の合流・市場カレンダー・スケジューラ方式）
type: spec
status: review
related_ids: [FR-02, FR-04, FR-03, UC-01, UC-02, ADR-0003, ADR-0006, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/04_workflows/01_scheduled-trading-cycle.md
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
---

# 仕様書: 取引サイクルの配線 Slice A（定時/イベント駆動の合流）

> Issue [#21](https://github.com/endazon/ai-stock-trading/issues/21)（FR-02・Must）の Slice A。定時（`InformationCollected`）と
> 価格変動（`PriceMovementDetected`）の2系統を**取引判断サービスの同一パイプラインへ合流**させ、**市場カレンダー**（日米休場日・
> 市場ローカル TZ）で開場日のみサイクルを起動する。スケジューラ方式を IADR で確定する。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-02（定時トリガーで情報収集→AI判断→発注の取引サイクル。Must）。関連 FR-04（判断）・FR-03（価格変動）
- ユースケース（UC）: UC-01（定時サイクル）、UC-02（価格変動トリガー）
- 業務フロー: `04_workflows/01_scheduled-trading-cycle.md`（COL→TRD 収集完了イベントで判断起動・前提チェック「市場開場中?」）
- ADR: ADR-0003（AI 判断ガードレール）、ADR-0006（Hetzner・スケジューラ実現）、ADR-0001（新規サービス）
- 関連 IADR: 本作業で新規 [IADR-0023](../adr/IADR-0023_trading-cycle-scheduling-and-merge.md)
- 対象 Issue: #21（Slice A）

## 目的・背景

現状、価格変動系統（`PriceMovementDetected`→取引判断）は結線済みだが、定時系統（`InformationCollected`→取引判断）が未結線で、
両系統が同一パイプラインへ合流していない。また休場日ガードは市場監視の平日判定のみで、祝日を考慮しない。#9 で
`InformationCollected` を発行できるようになったため、これを取引判断へ合流させ、祝日対応の市場カレンダーで開場日ゲートを行う。

## 対象範囲（取引判断サービス `TradeDecisionService` 内）

- **トリガー抽象化**（`DecisionTrigger`）: 判断の起点を「銘柄・市場・種別（Scheduled/PriceMovement）＋任意の価格変動文脈」に一般化し、
  定時・イベント両系統を同一の `DecideAsync` に合流させる。`DecisionTrigger.FromPriceMovement` / `DecisionTrigger.Scheduled`。
  `TradeDecisionPromptBuilder` は種別に応じて価格変動セクション（PriceMovement）または定時セクション（Scheduled）を出力する。
- **市場カレンダー**（`IMarketCalendar` / `MarketCalendar`）: 市場ローカル TZ（日本=JST、米国=US Eastern）で「取引日か（週末・
  休場日でない）」を判定する。休場日は市場別に構成注入（既定は空＝週末のみ。祝日データ源は後続）。開場日のみサイクルを起動する。
- **合流の結線**（Worker）:
  - `InformationCollectedConsumer`（新規）: 収集完了で監視銘柄（`IWatchlistProvider`）を巡回し、市場カレンダーで開場中のもののみ
    `DecideAsync(Scheduled)` を実行し、成立すれば `TradeDecisionMade` を発行する（定時系統）。
  - `PriceMovementDetectedConsumer`（改修）: 市場カレンダーで開場判定を追加（祝日ガード）し、`DecideAsync(PriceMovement)` に合流する。
  - `IWatchlistProvider`（新規ポート）＋ `ConfigurationWatchlistProvider`（構成 `TradeCycle:Watchlist`＝{Symbol,Market} 群）。実 watchlist
    （市場監視 #10 の監視銘柄）連携は後続。
- **スケジューラ方式**（IADR-0023）: 現段階は各サービスの in-process 定時ポーリング（BackgroundService。市場監視=価格、情報収集=定時）。
  本番の K8s CronJob 化・市場ローカル時刻スケジュールは platform 統合（#22・ADR-0006）で確定する。

## 受け入れ基準

CI で緑にする範囲（ユニット＋MassTransit テストハーネス）:
- [ ] `MarketCalendar`: 週末・構成休場日は非開場、平日・非休場日は開場（市場ローカル TZ 判定）。
- [ ] `InformationCollectedConsumer`: 収集完了で watchlist を巡回し、開場銘柄のみ判断→成立で `TradeDecisionMade` を発行する（定時合流）。
- [ ] `InformationCollectedConsumer`: 休場日（カレンダー閉場）の銘柄はサイクルを起動しない。
- [ ] `PriceMovementDetectedConsumer`: 休場日は判断せずスキップし、開場中は従来どおり `TradeDecisionMade` を発行する。
- [ ] `DecisionTrigger`: PriceMovement/Scheduled いずれの起点でも `DecideAsync` が同一ロジックで判断する（合流）。
- [ ] 既存テスト（取引判断コア・価格変動結線）を緑に保つ。

実コンテナ前提（CI 既定では実行しない・Testcontainers）:
- [ ] ペーパーで定時サイクル・イベント駆動サイクルが判断→リスク→発注→記録→通知まで完了する E2E（計画の受け入れ基準）。

## 対象外（後続）

- 本番スケジューラ（K8s CronJob）・市場ローカル時刻スケジュール定義（#22・ADR-0006）。
- 実 watchlist 連携（市場監視 #10 の監視銘柄を取引判断へ供給）。本スライスは構成ベースの暫定 watchlist。
- 祝日データ源（日米の休場日一覧の取り込み）。本スライスは構成注入（既定 空＝週末のみ）。
- 前提チェックのうち「日報確定済み?」（`IDailyPolicyProvider` 既存）・「kill switch 無効?」（リスク管理側）以外の統合。
- 取引時間帯（場中の時刻精度・寄付き/引け）ガード。本スライスは取引日粒度。

## テスト方針

- `MarketCalendar` は純ロジックとして単体検証（週末・休場日・TZ）。
- `DecisionTrigger`／`TradeDecisionPromptBuilder` は写像・出力の単体検証。
- Consumer は MassTransit `ITestHarness`＋フェイク（判断サービス・watchlist・カレンダー）で合流・休場ガード・発行を検証。

## 関連仕様

- 連携元: [20260710_information-collection](20260710_information-collection.md)（`InformationCollected` 発行元）、[20260710_market-monitor-core](20260710_market-monitor-core.md)（`PriceMovementDetected`）
- 連携先: [20260710_trade-decision-core](20260710_trade-decision-core.md)（判断コア）
- 実装ADR: [IADR-0023](../adr/IADR-0023_trading-cycle-scheduling-and-merge.md)

## 未決事項

- 本番スケジューラ方式（CronJob/Quartz）・市場カレンダーの祝日データ源・実 watchlist 連携は後続（#22 / #10 連携）で確定する。
