---
title: 全体前提条件（assumptions / assumptions_change_log）データ仕様書
type: data-spec
status: review
created: 2026-07-10
updated: 2026-07-10
author: endazon (with Claude Code)
---
<!-- trace:
ids: [FR-13, FR-17, UC-06]
adrs: [ADR-0001]
iadrs: [IADR-0012, IADR-0021, IADR-0173]
specs: [01_requirements, 05_trading-assumptions, 20260710_configuration-assumptions]
issues: [#14, #358]
-->


# データ仕様書: 全体前提条件（assumptions / assumptions_change_log）

> 設定管理サービス（`ConfigurationService`）が所有する全体前提条件（税・手数料・為替・計算方針・月次費用上限）の永続化。
> FR-17（一元管理・バージョン管理・利用者のみ変更）・UC-06。設計は IADR-0021: 全体前提条件は専用の設定サービスが所有し、バージョン管理・変更履歴・イベント発行で一元管理する、
> バージョニング/履歴は IADR-0012: リスク管理設定は単一行 JSON＋バージョン列で永続化し楽観的排他制御する を踏襲。作業仕様は
> 仕様書: 設定管理サービス Slice A（全体前提条件の一元管理）。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-17（全体前提条件の一元管理・バージョン管理。Must）、FR-13（設定変更）
- ユースケース（UC）: UC-06（設定変更・変更履歴・通知）
- ADR: ADR-0001（Database per Service）、FR-17（変更は利用者のみ・変更履歴を記録）

## ドメイン型（`TradingAssumptions`）

`record TradingAssumptions`（JSON で単一行に保持）。数値計算はコードで行い LLM には計算させない（05 採用方針）。

| 属性 | 型 | 既定 | 説明 |
| --- | --- | --- | --- |
| CapitalGainsTaxRate | decimal | 0.20315 | 譲渡益税率（20.315%） |
| JapanCommission | CommissionSchedule | (0,0,0) 未登録 | 日本株手数料（Rate/Minimum/Cap。要確認・口座開設後に登録） |
| UnitedStatesCommission | CommissionSchedule | (0,0,0) 未登録 | 米国株手数料（同上） |
| FxSpreadRatio | decimal | 0 未登録 | 非 JPY 市場の為替スプレッド率（約定代金比・片道。実 FX レート連携までの近似） |
| MinimumExpectedProfitMultiple | decimal | **2** | 最小期待利益倍率。**基準は「往復費用＋税」**であり往復費用のみではない（§4・利用者決定 2026-07-23。#358 / IADR-0173: 最小期待利益の税込み基準。**旧記載の 1.5・往復費用のみは計画確定前の暫定値**） |
| CostLimits | MonthlyCostLimits | (20000,15000,5000,0) | 月次費用上限（総額/LLM/インフラ/データ・円） |

- `CommissionSchedule(Rate, Minimum, Cap)`: 手数料 = clamp(約定代金×Rate, Minimum, Cap)。Cap≤0 は上限なし。
- `CostCalculator`（純関数・05 §4）: 片道費用＝手数料＋為替スプレッド、往復＝×2、最小期待利益＝**不動点** `T = m × C × (1 − r) / (1 − m × r)`（C＝往復費用＋判断費用・r＝譲渡益税率）。**税は譲渡益（＝利益−費用）に掛かるため結果に依存し、単純な「往復×倍率」では解けない**。式の単一情報源は `AiStockTrading.Shared.Contracts.Trading.MinimumExpectedProfit`。**m × r ≥ 1 では解が無く、負のしきい値で全通過させないよう安全側へ倒す。**

## エンティティ定義（永続）

### AssumptionsRow（`assumptions`・単一行）

| 属性 | 型 | 説明 |
| --- | --- | --- |
| Id | int (PK=1) | 単一行の固定キー |
| Json | jsonb | `TradingAssumptions` の JSON |
| Version | int（並行トークン） | 楽観的排他制御（更新時 +1・IADR-0012） |
| UpdatedAt | DateTimeOffset | 最終更新時刻 |

### AssumptionsChangeRow（`assumptions_change_log`・追記専用）

| 属性 | 型 | 説明 |
| --- | --- | --- |
| Id | Guid (PK) | 履歴レコード ID |
| Actor | string(256) | 変更した利用者（preferred_username） |
| Reason | string(1024) | 変更理由（必須） |
| ChangedAt | DateTimeOffset (index) | 変更日時（新しい順照会） |
| Version | int | 確定後バージョン |
| Before / After | string? | 前後値（TradingAssumptions の文字列表現） |

## 照会・変更

- `GET /assumptions`（現在値＋Version）は **OwnerOrService**（利用者＝`trading-owner` またはサービス＝`trading-service`）。
  消費側サービス（費用統制 #139・損益集計・AI 判断）が単一の真実源を共通参照するため（IADR-0063 決定 2）。
- `GET /assumptions/history`（新しい順）・`PUT /assumptions`（更新）は **OwnerOnly 据え置き**（最小権限）。履歴は「誰がなぜ
  変えたか」の運用情報のためサービスへ開放しない。
- `PUT` は `ExpectedVersion`・`Reason` 必須（欠如は 400）。版不一致は 409（楽観排他）。成功時に `AssumptionsChanged` イベントを発行
  → 通知サービスが Discord 通知（IADR-0020/0021）。AI・自動処理は `trading-owner` を持たず変更できない。

## 消費側からの参照（共有クライアント）

- 消費側サービスは `AiStockTrading.Configuration.Client` の `IAssumptionsProvider` で参照する（IADR-0063 決定 3）。
  配線は `services.AddAiStockTradingAssumptions(configuration)` の 1 行＋`x.AddConsumer<AssumptionsChangedConsumer>()`（版の追随）。
- キャッシュは `AssumptionsChanged` で無効化し、TTL（`Configuration:AssumptionsCacheTtlSeconds`・既定 300 秒）でも失効する。
- フェイルセーフ: 取得不可時は ①last known good → ②既定値（`Version=0`＝未解決）の順に倒す（決定 5）。
  `Configuration:BaseUrl` 未設定なら HTTP を構築せず既定値のみ（決定 6）。

## 整合性・制約ルール

- 前提条件は単一行（Id=1）。更新は Version 増分・楽観排他（ロストアップデート防止）。変更履歴は追記専用。
- 過去報告書は生成時 Version を凍結参照する（遡及再計算しない・05 変更管理）。本スライスは Version を API 公開するに留め、
  報告書側の凍結は報告書サービスで実装する。

## 永続化方針

| 集約 | 永続化 | 実装 issue | 備考 |
| --- | --- | --- | --- |
| TradingAssumptions（`assumptions`） | PostgreSQL 単一行 JSON＋Version（専有 DB `configuration_svc`） | #19（PR） | 楽観排他・既定シード（IADR-0012 踏襲） |
| 変更履歴（`assumptions_change_log`） | PostgreSQL 追記専用 | #19（PR） | アクター・理由・前後値・日時・版 |

## 対象外（後続）

- 手数料・為替スプレッドの実額登録（moomoo 口座開設後・05 §2/§3 要確認）。損益/AI判断/リスク統制からの `CostCalculator` 実利用。
- 報告書の `assumptions_version` 凍結。税・為替の精緻化（外国税額控除・実レート・NISA・損益通算＝FR-18 将来拡張）。
- FR-13 の監視銘柄・変動閾値・収集間隔（各サービス所管）。

## 関連仕様

- 作業仕様書: 仕様書: 設定管理サービス Slice A（全体前提条件の一元管理）
- 実装ADR: IADR-0021: 全体前提条件は専用の設定サービスが所有し、バージョン管理・変更履歴・イベント発行で一元管理する、IADR-0012: リスク管理設定は単一行 JSON＋バージョン列で永続化し楽観的排他制御する（踏襲）
