---
title: 報告書（reports）データ仕様書
type: data-spec
status: review
related_ids: [FR-06, FR-07, FR-16, FR-17, ADR-0001, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/04_report-templates.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# データ仕様書: 報告書（reports）

> 報告書サービス（`ReportService`）が所有する報告書（日報/週報/月報）の永続化。FR-06/07（階層方針・確定）・FR-16（テンプレート・
> 集計）・FR-17（前提条件バージョン）。設計は [IADR-0024](../adr/IADR-0024_report-confirmation-and-policy.md)、版番号確定は
> [IADR-0012](../adr/IADR-0012_risk-settings-persistence.md) 踏襲。作業仕様は [20260710_report-confirmation](../specs/20260710_report-confirmation.md)。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-06（階層方針）、FR-07（確定・確定前は不適用）、FR-16（テンプレート・集計）、FR-17（前提条件バージョン記録）
- ユースケース（UC）: UC-03〜05（報告書の確定）
- ADR: ADR-0001（Database per Service）、FR-06/FR-07（確定は利用者のみ）、ADR-0003（確定前方針は不適用）

## ドメイン型（`TradingReport`）

| 属性 | 型 | 説明 |
| --- | --- | --- |
| PeriodKey | string | 自然キー（"daily-2026-07-10" / "weekly-2026-W28" / "monthly-2026-07"） |
| Kind | ReportKind | Daily / Weekly / Monthly |
| PeriodStart | DateOnly | 対象期間の開始日（最新の確定済み日報照会に使用） |
| State | ReportState | Draft / Confirmed（確定した方針のみ取引に適用・ADR-0003） |
| BasedOn | string? | 参照した上位方針の PeriodKey（daily→週報 / weekly→月報 / monthly→前月報） |
| AssumptionsVersion | int | 適用した全体前提条件のバージョン（FR-17・#19） |
| PolicySummary | string | 翌期間の方針（確定で有効化） |
| ConfirmedAt | DateTimeOffset? | 確定日時 |

## エンティティ定義（`reports`・永続）

| 属性 | 型 | 説明 |
| --- | --- | --- |
| PeriodKey | string(64) (PK) | 自然キー |
| Kind / State | 列挙（int 格納） | 種別・状態 |
| PeriodStart | date | 期間開始日 |
| BasedOn | string(64)? | 上位方針参照 |
| AssumptionsVersion | int | 前提条件バージョン |
| PolicySummary | string(8192) | 方針テキスト |
| ConfirmedAt | timestamptz? | 確定日時 |
| Version | int（並行トークン） | 版番号付き冪等確定の楽観排他（IADR-0012） |

- インデックス: `(Kind, State, PeriodStart)`（最新の確定済み日報の照会）。

## 照会・操作

- `GET /reports`、`GET /reports/{periodKey}`、`GET /reports/daily-policy`（確定済み日報方針＝Date/Summary/AssumptionsVersion・未確定は 404）。
- `PUT /reports/{periodKey}`（ドラフト upsert・楽観排他）、`POST /reports/{periodKey}/confirm`（版番号付き冪等確定）。すべて OwnerOnly。
- **版番号付き冪等確定**: Draft→Confirmed の遷移時のみ `ConfirmedAt` 記録＋`ReportConfirmed` 発行（通知サービスが Discord 通知）。
  既に確定済みの再確定は冪等（状態変化なし・イベント重複なし）。版不一致は 409、確定済みの変更は 409、未認証 401/無権限 403。

## 整合性・制約ルール

- PeriodKey ごとに 1 行。確定済みは不変（`UpsertDraft` で変更不可）。版番号（Version）で楽観排他しロストアップデートを防ぐ。
- 確定は利用者のみ（OwnerOnly・アクター必須）。生成AI・自動処理は確定できない（ADR-0003/0007）。

## 永続化方針

| 集約 | 永続化 | 実装 issue | 備考 |
| --- | --- | --- | --- |
| TradingReport（`reports`） | PostgreSQL 1 行/PeriodKey＋Version（専有 DB `report_svc`） | #14（PR） | 版番号付き冪等確定（IADR-0012 踏襲） |

## 対象外（後続）

- 損益・費用・税の集計列（FR-16。取引台帳 #63・前提条件 #19 参照のコード集計）。LLM ドラフト・対話的確定・KB 保存（FR-08・#18）。
- 無応答時の既定動作・階層（月報→週報→日報）参照の強制・取引判断の `IDailyPolicyProvider` 結線（#22）。

## 関連仕様

- 作業仕様書: [20260710_report-confirmation](../specs/20260710_report-confirmation.md)
- 実装ADR: [IADR-0024](../adr/IADR-0024_report-confirmation-and-policy.md)、[IADR-0012](../adr/IADR-0012_risk-settings-persistence.md)（踏襲）
