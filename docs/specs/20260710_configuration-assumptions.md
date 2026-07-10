---
title: 設定管理サービス Slice A（全体前提条件のバージョン管理・変更履歴・利用者変更・概算費用関数・変更通知）
type: spec
status: review
related_ids: [FR-17, FR-13, UC-06, ADR-0001, ADR-0007]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
---

# 仕様書: 設定管理サービス Slice A（全体前提条件の一元管理）

> Issue [#19](https://github.com/endazon/ai-stock-trading/issues/19)（FR-13/FR-17・Must）の **Slice A（FR-17 全体前提条件）**。
> 税金・手数料・為替・計算方針・月次費用上限を**設定として一元管理**し、バージョン管理・変更履歴・利用者のみ変更・
> 照会 API・概算費用関数を提供する。変更時は `AssumptionsChanged` イベントを発行し、通知サービスが Discord 通知する。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-17（全体前提条件の一元管理・バージョン管理・利用者のみ変更。Must）。関連 FR-13（設定変更・Should）
- ユースケース（UC）: UC-06（設定変更・変更履歴・通知）
- 技術検討: `05_trading-assumptions.md`（税制・手数料・為替・計算方針・概算費用関数・月次費用上限）、`01_architecture-overview.md`
  （「全体前提条件の一元管理」＝設定ストアで一元管理し、損益集計・AI判断・リスク統制が共通参照する）
- ADR: ADR-0001（新規サービス・Database per Service）、ADR-0007（変更は利用者のみ・変更履歴を記録）
- 関連 IADR: 本作業で新規 [IADR-0021](../adr/IADR-0021_trading-assumptions-configuration.md)。バージョニング/履歴は [IADR-0012](../adr/IADR-0012_risk-settings-persistence.md) を踏襲
- 対象 Issue: #19（Slice A）

## 目的・背景

現状は `TradingDefaults`（コード上の初期値）のみで、全体前提条件（税金・手数料・為替・計算方針・月次費用上限）の設定
ストア・バージョン管理・変更履歴・利用者変更手段が無い。これらは損益集計（報告書）・AI 判断の採算評価（取引判断）・
費用込み上限判定（リスク管理）が共通参照する横断設定であり、専用サービスで一元管理する（アーキ概要）。moomoo の
手数料・為替スプレッド実額は「要確認」（口座開設後に登録）のため、**既定は未登録（0）**とし利用者が登録する。

## 対象範囲

### 新規サービス `ConfigurationService`（Domain + Application + Worker）

- **Domain**:
  - `TradingAssumptions`（税制＝譲渡益税率、手数料＝市場別 `CommissionSchedule(Rate,Minimum,Cap)`、為替スプレッド率、
    計算方針＝最小期待利益倍率、月次費用上限 `MonthlyCostLimits(Total,Llm,Infrastructure,Data)`）。
  - `TradingAssumptionsDefaults`: 譲渡益税率 20.315%・最小期待利益倍率 1.5・月次費用上限（総額20,000/LLM15,000/インフラ5,000/データ0）。
    手数料・為替スプレッドは**要確認のため既定 0（未登録）**（05 §2・§3 の「数値は固定せず設定値として保持」）。
  - `CostCalculator`（純関数・概算費用関数 05 §4）: `EstimateOneWayCost`＝手数料＋為替スプレッド相当、`EstimateRoundTripCost`＝往復、
    `MinimumViableProfit`＝往復費用×倍率（この額を下回る期待利益の取引は見送りの判定に用いる。税の精緻化は後続）。
- **Application**: `IAssumptionsStore`（単一行＋Version 楽観排他・未設定は既定シード）、`IAssumptionsChangeLog`（追記・新しい順照会）、
  `IClock`、`AssumptionsService`（利用者のみ更新＝アクター・理由必須、Version 増分、前後値つき履歴記録）、`AssumptionsChangeEntry`、InMemory 実装。
- **Worker**: EF 永続化（`AssumptionsDbContext`・単一行 JSON＋Version＝IADR-0012 踏襲・変更履歴追記・専有 DB `configuration_svc`・Migration）、
  OwnerOnly エンドポイント（`GET /assumptions`＝現在値＋version、`PUT /assumptions`＝更新、`GET /assumptions/history`）、更新時に
  `AssumptionsChanged` イベントを発行（MassTransit）。実行時基盤は test-support shim（本番非使用・IADR-0013）。

### 共有契約（`AiStockTrading.Shared.Contracts`）

- 新規イベント `AssumptionsChanged(Version, Actor, Reason, ChangedAt)`（変更通知の疎結合な起点）。

### 通知サービス（`NotificationService`）

- `AssumptionsChanged` を購読し、`NotificationFormatter` で整形して通知する（FR-17「変更時の Discord 通知」を満たす）。

## 受け入れ基準

CI で緑にする範囲（ユニット＋MassTransit テストハーネス＋EF InMemory＋WebApplicationFactory）:
- [ ] 前提条件の更新で `Version` が上がり、変更履歴（アクター・理由・前後値・日時）が記録される。
- [ ] 更新はアクター・理由が必須（欠如は 400）。照会・更新は OwnerOnly（未認証 401・ロール無し 403）。AI・自動処理はロールを持たず変更できない。
- [ ] 未設定時は既定値（譲渡益税率 20.315%・月次費用上限 20,000）をシードして返す。
- [ ] `CostCalculator`: 手数料（定率・最低額・上限クランプ）＋為替スプレッド（非 JPY 市場）で片道/往復/最小期待利益を算出する。
- [ ] 更新時に `AssumptionsChanged` イベントが発行される。
- [ ] `NotificationService` が `AssumptionsChanged` を購読し通知を送信する（変更通知）。
- [ ] 楽観排他: 読み込み版と DB 版が不一致なら更新が競合として弾かれる（409）。
- [ ] Worker が起動しヘルスが応答する。既存テストを緑に保つ。

実 API 前提（CI 既定では実行しない）:
- [ ] PostgreSQL 経由の永続化・履歴の E2E。

## 対象外（後続）

- FR-13 の監視銘柄・変動閾値・収集間隔の設定（各サービス＝市場監視 #10 等の所管。本スライスは横断の全体前提条件に限定）。
- 報告書への `assumptions_version` 記録・生成時凍結（報告書サービス #14 未実装）。本スライスは version を API で公開するに留める。
- 損益集計・AI 判断・リスク統制からの `CostCalculator` 実利用（各サービスの費用対応は後続。本スライスは純関数と設定供給を用意）。
- 税・為替の精緻化（外国税額控除・約定時レート/日次終値の実レート連携）、NISA 枠・損益通算の集計（FR-18 将来拡張）。

## テスト方針

- `TradingAssumptionsDefaults`・`CostCalculator` は純関数として単体検証（既定値・クランプ・為替・往復・最小期待利益）。
- `AssumptionsService` は InMemory ストア＋履歴で更新・Version 増分・アクター/理由必須・前後値記録を検証。
- `EfAssumptionsStore` は EF InMemory でシード・往復・Version 増分・楽観排他を検証。
- エンドポイントは `ConfigurationWorkerWebApplicationFactory`（InMemory DB・TestAuthHandler）で OwnerOnly・更新・履歴・409 を検証。
- `AssumptionsChanged` 発行は MassTransit ハーネス、通知購読は NotificationService 側テストで検証。

## 関連仕様

- 連携先: [20260710_notification-outbound](20260710_notification-outbound.md)（`AssumptionsChanged` 購読）
- 実装ADR: [IADR-0021](../adr/IADR-0021_trading-assumptions-configuration.md)、踏襲 [IADR-0012](../adr/IADR-0012_risk-settings-persistence.md)

## 未決事項

- 手数料・為替スプレッドの実額（moomoo 口座開設後に登録・05 §2/§3 の要確認）。
- 損益/AI判断/リスク統制からの費用関数の実利用配線、報告書の version 凍結（#14）は後続で確定する。
