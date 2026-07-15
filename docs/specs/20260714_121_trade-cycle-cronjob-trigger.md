---
title: 取引サイクルの本番スケジューラ（in-process → K8s CronJob）— Issue #121
type: spec
status: draft
related_ids:
  - FR-02
  - IADR-0023
  - IADR-0052
  - IADR-0054
author: claude
created: 2026-07-14
updated: 2026-07-14
plan_refs:
  - "../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md (FR-02: 取引サイクル)"
related_specs:
  - "../adr/IADR-0054_collection-scheduler-mode-run-once.md"
  - "20260713_122_k8s-helm-chart.md"
---

# 仕様書: 取引サイクルの本番スケジューラ（Issue #121）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-02（取引サイクル）
- 関連 ADR: IADR-0023（合流・市場カレンダー・休場ガード）／IADR-0052（AST k8s chart・CronJob 骨子）
- 実装判断: [[IADR-0054]]（収集スケジューラモード＝Collection:Trigger ＋ run-once HTTP トリガ）
- Issue: #121（本番スケジューラ）／ Refs #21（取引サイクル配線）・#24・#22

## 目的・背景

取引サイクルの定時トリガーを、現行の各サービス in-process ポーリング（IADR-0023）から、本番では
**K8s CronJob** 駆動へ切替可能にする。合流・市場カレンダー・休場ガードは実装済み（#21・IADR-0023）で、
本作業は**スケジューラ方式の差し替え**に限定する。

## 対象範囲

**対象（本 PR）**
- `InformationCollectionService`:
  - `Collection:Trigger`（InProcess 既定 / External）を追加。External では in-process 巡回（`CollectionPollingService`）を停止。
  - `POST /internal/collection/run-once` エンドポイントを追加（1 巡回 `RunOnceAsync` を起動）。
- AST chart: `tradingCycle.cronjob.enabled=true` のとき CronJob が run-once を叩き、情報収集へ
  `Collection__Trigger=External` を注入（二重起動防止）。既定は無効＝in-process 維持（fail-safe）。

**対象外**
- 判断側（TradeDecision）のトリガ化: 収集の `InformationCollected` が起点のため、収集の run-once で足りる。
  価格変動系（`PriceMovementDetected`）は MarketMonitor の別トリガで、本 PR では扱わない。
- platform 宣言的スケジュール（#22）での駆動は将来の代替。

## 設計

- **休場ガードは下流で担保**: 市場カレンダーの開場日ゲート（IADR-0023）は `TradeDecision` の
  `InformationCollectedConsumer`（`calendar.IsOpen`）にあり、**トリガ方式に依らず**適用される。よって
  run-once/in-process は市場カレンダーと整合する（休場日はサイクルが起動しても発注に至らない）。CronJob の
  `schedule` は取引時間帯に合わせる（belt-and-suspenders）。
- **fail-safe**: `Collection:Trigger` 未設定=InProcess（現行）。費用統制 Halted・収集ゼロは `RunOnceAsync`
  内で no-op に倒れる。run-once は 200 を返し、CronJob（`curl -fsS`）が成功判定する。

## 受け入れ基準

- [x] in-process はスケジュール未設定時の既定として維持（`Collection:Trigger` 既定 InProcess）
- [x] External で in-process 巡回を停止（テスト: 収集・発行しない）
- [x] `POST /internal/collection/run-once` が 1 巡回を起動し 200 を返す（統合テスト）
- [x] CronJob 有効時に情報収集が External へ切替わる（chart レンダリング検証）
- [ ] k3d 実クラスタで CronJob から run-once 起動を確認（要ユーザ環境）

## テスト方針

- 単体（xUnit + MassTransit TestHarness）: 既定 InProcess／External で巡回しない／実効間隔境界（既存）。
- 統合（WebApplicationFactory）: run-once エンドポイントが 200。
- chart: `helm template`（既定=External 不在／`--set ...enabled=true`=External 注入）。

## 計画書との差異

- 差異: なし。IADR-0023 の市場カレンダー整合はトリガ方式非依存で満たす。

## 未決事項

- 実クラスタでの CronJob 起動確認（`kubectl create job --from=cronjob/trading-cycle-trigger`）。
