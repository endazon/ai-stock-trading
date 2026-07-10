---
title: 費用統制サービス Slice A（月次費用台帳・LLM 上限の間隔延長/停止判定・しきい値通知）
type: spec
status: review
related_ids: [NFR, FR-17, FR-16, FR-09, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# 仕様書: 費用統制サービス Slice A

> Issue [#23](https://github.com/endazon/ai-stock-trading/issues/23)（NFR 費用）の Slice A。LLM API 費用の月次集計と、
> 月次上限（#19 `MonthlyCostLimits`）に対する **80% 到達で定時サイクル間隔延長・100% で停止** の判定、しきい値到達時の
> `CostThresholdReached` 発行（Discord 通知）を実装する。費用暴走を Stage 0 運用開始時から防ぐ。

## 起点となる計画書・課題（トレーサビリティ）

- 非機能要件（NFR・費用）: LLM 費用は月次上限を設定し、超過時は定時サイクル間隔を自動延長する
- 技術検討: `05_trading-assumptions.md` §6（月次総費用 20,000 円・LLM 15,000 円は **80% 到達で間隔延長・100% で停止**・月報で費用÷資金レビュー）
- 関連 FR: FR-17（月次費用上限は前提条件・#19）、FR-16（月報の費用レビュー）、FR-09（停止の Discord 通知）
- ADR: ADR-0001（新規サービス）
- 関連 IADR: 本作業で新規 [IADR-0027](../adr/IADR-0027_cost-control.md)。上限は [IADR-0021](../adr/IADR-0021_trading-assumptions-configuration.md)（`MonthlyCostLimits`）を参照
- 対象 Issue: #23（Slice A）

## 対象範囲（新規サービス `CostControlService`）

- **Domain**（`ConfigurationService.Domain` を参照＝`MonthlyCostLimits`）:
  - `CostGovernor`（純関数）: 月内 LLM 累計費用と上限から統制判定を返す。`ratio = 累計/LLM上限`。
    `Normal`（<80%）／`Throttled`（≥80% <100%・間隔倍率 2）／`Halted`（≥100%・停止）。
  - `CostControlState`（列挙）、`CostControlDecision(State, IntervalMultiplier)`、`CostReview`（費用÷資金 比率・FR-16）。
- **Application**: `ICostLedger`（月・カテゴリ[Llm/Infrastructure/Data] 別に費用を追記・月次集計）、`CostControlService`
  （費用記録→統制判定・しきい値の上方遷移検知）、`IClock`、InMemory 実装。
- **Worker**: EF 費用台帳（cost_entries・専有 DB `cost_control_svc`・Migration）、エンドポイント（`POST /costs/record`＝費用計上・
  `GET /costs/state`＝現在の統制判定・`GET /costs/review`＝費用÷資金）。上方遷移（Normal→Throttled→Halted）時に `CostThresholdReached`
  を発行。上限は暫定で `TradingAssumptionsDefaults.CostLimits`（#19 のバージョン付き取得は #22 後続）。実行時基盤は shim（IADR-0013）。

### 共有契約・通知

- 新規イベント `CostThresholdReached(Month, Category, Percent, State, OccurredAt)`。`NotificationService` が購読して Discord 通知（100% 停止等）。

## 受け入れ基準

CI で緑にする範囲（ユニット＋MassTransit テストハーネス＋EF InMemory＋WebApplicationFactory）:
- [ ] `CostGovernor`: LLM 累計が上限の 80% 未満は Normal、80〜100% は Throttled（間隔倍率 2）、100% 以上は Halted（停止）。
- [ ] 費用記録で月内累計が加算され、状態がしきい値を上方に跨ぐと `CostThresholdReached` が発行される（同一状態内の追加では発行しない）。
- [ ] `GET /costs/state` が現在の統制判定を返す。`GET /costs/review` が費用÷資金比率を返す。
- [ ] `NotificationService` が `CostThresholdReached` を購読し通知する。
- [ ] 月をまたぐと累計はリセットされる（月次集計）。
- [ ] Worker が起動しヘルスが応答する。既存テストを緑に保つ。

## 対象外（後続）

- 実 LLM 費用源（platform LLM ゲートウェイの計測連携・実 LLM #11 後続）。本スライスは費用計上を API/イベントで受ける。
- 定時サイクル（情報収集 #9・取引判断 #21）の poller への間隔延長/停止の配線（サービス間連携・#22）。本スライスは統制判定を照会 API で提供。
- #19 のバージョン付き上限取得（本スライスは既定値）。インフラ費 EUR→円換算の実レート（前提条件の率近似）。月報への費用レビュー供給（#14 集計連携）。

## テスト方針

- `CostGovernor`・`CostReview` は純関数として単体検証（しきい値境界・倍率・比率）。
- `CostControlService` は InMemory 台帳で記録・累計・上方遷移検知・月跨ぎを検証。
- `EfCostLedger` は EF InMemory、エンドポイント・イベント発行は WebApplicationFactory＋MassTransit ハーネス、通知は NotificationService 側で検証。

## 関連仕様

- 参照: [20260710_configuration-assumptions](20260710_configuration-assumptions.md)（`MonthlyCostLimits`）、連携先 [20260710_notification-outbound](20260710_notification-outbound.md)
- 実装ADR: [IADR-0027](../adr/IADR-0027_cost-control.md)

## 未決事項

- 実 LLM 費用計測・poller への配線（#22）・#19 バージョン付き上限取得・月報連携は後続で確定する。
