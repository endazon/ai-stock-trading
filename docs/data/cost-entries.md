---
title: 費用台帳（cost_entries）データ仕様書
type: data-spec
status: review
created: 2026-07-10
updated: 2026-08-21
author: endazon (with Claude Code)
---
<!-- trace:
ids: [FR-09, FR-16, FR-17, NFR-13, NFR-15]
adrs: [ADR-0001]
iadrs: [IADR-0021, IADR-0027]
specs: [05_trading-assumptions, 20260710_cost-control]
issues: [#9, #14, #19, #21, #22, #23]
-->


# データ仕様書: 費用台帳（cost_entries）

> 費用統制サービス（`CostControlService`）が所有する月次費用の追記専用台帳。NFR（費用）＝LLM 月次上限の間隔延長/停止判定・
> 月報の費用レビュー。設計は「費用統制は専用サービスが月次費用台帳を持ち、純関数で間隔延長/停止を判定する」。
> 作業仕様は 仕様書: 費用統制サービス Slice A。

## 本書が受け持つ範囲

- 非機能要件（費用）: LLM 費用は月次上限・超過時に定時サイクル間隔を自動延長（全体前提条件 §6: 80% 延長・100% 停止）
- 関連する機能要求: 全体前提条件としての月次費用上限（#19）、月報の費用レビュー
- 計画 ADR: 基盤採用（Database per Service）

## エンティティ定義（`cost_entries`・追記専用）

| 属性 | 型 | 説明 |
| --- | --- | --- |
| Id | Guid (PK) | 計上レコード ID |
| Month | string(7) (index) | 計上月 "yyyy-MM"（UTC。月をまたぐと累計がリセットされる） |
| Category | 列挙（Llm/Infrastructure/Data） | 費用カテゴリ |
| Amount | decimal | 費用（円） |
| RecordedAt | DateTimeOffset | 計上日時 |

- インデックス: `(Month, Category)`（月・カテゴリ別の集計）。

## 統制判定・照会

- `CostGovernor`（純関数）: 月内 LLM 累計 ÷ `MonthlyCostLimits.Llm`。**<80%=Normal / 80〜100%=Throttled（間隔倍率2）/ ≥100%=Halted（停止）**（05 §6）。
- `POST /costs/record`（費用計上）、`GET /costs/state`（現在の統制判定）、`GET /costs/review?capital=`（費用÷資金比率。月報の費用レビュー用）。すべて OwnerOnly。
- 統制状態が上方に遷移（Normal→Throttled→Halted）したときのみ `CostThresholdReached` を発行 → 通知サービスが Discord 通知。

## 整合性・制約ルール

- 追記専用（更新・削除しない）。集計は月・カテゴリで絞って合算する。月上限は暫定で前提条件の既定値（#19 バージョン付き取得は #22）。

## 永続化方針

| 集約 | 永続化 | 実装 issue | 備考 |
| --- | --- | --- | --- |
| CostEntryRow（`cost_entries`） | PostgreSQL 追記専用（専有 DB `cost_control_svc`） | #23（PR）| 月次費用の集計・統制判定の入力 |

## 対象外（後続）

- 実 LLM 費用計測（platform ゲートウェイ連携）・pollerへの間隔延長/停止の配線。#19 バージョン付き上限取得。
  EUR→円換算の実レート（前提条件は率近似）。月報への費用レビュー供給（#14 連携）。

## 関連仕様

- 作業仕様書: 仕様書: 費用統制サービス Slice A
- 実装ADR: 費用統制は専用サービスが月次費用台帳を持ち、純関数で間隔延長/停止を判定する／全体前提条件は専用の設定サービスが所有し、バージョン管理・変更履歴・イベント発行で一元管理する（`MonthlyCostLimits`）
