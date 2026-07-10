---
title: 報告書サービス Slice A（報告書ドメイン・確定管理・確定済み日報方針の照会・確定通知）
type: spec
status: review
related_ids: [FR-06, FR-07, FR-16, FR-17, FR-09, UC-03, UC-04, UC-05, ADR-0001, ADR-0007]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/04_report-templates.md
  - ../../planning/projects/ai-stock-trading/04_workflows/03_reporting-cycle.md
---

# 仕様書: 報告書サービス Slice A（確定管理・方針の実体）

> Issue [#14](https://github.com/endazon/ai-stock-trading/issues/14)（FR-06/07/16/17・Must）の Slice A。報告書（日報/週報/月報）の
> ドメイン・確定管理（版番号付き冪等確定）・**確定済み日報方針の照会**・確定通知イベントを実装する。取引判断（#11）の
> `IDailyPolicyProvider`（現プレースホルダ＝方針なしで取引しない）の実データ源となる「方針の実体」を提供する。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-07（自動生成・対話的確定・確定前は不適用）、FR-06（月報→週報→日報の階層方針）、FR-16（テンプレート準拠・
  数値はコード集計）、FR-17（適用した前提条件バージョンを記録）、FR-09（確定の Discord 通知）
- ユースケース（UC）: UC-03〜05（報告書の確定）
- 技術検討: `04_report-templates.md`（frontmatter＝report_type/period/based_on/assumptions_version/confirmed_at、確定した日報の
  「翌営業日の目標」が取引方針として有効化）、`04_workflows/03_reporting-cycle.md`（確定で方針有効化）
- ADR: ADR-0001（新規サービス）、ADR-0007（確定は利用者のみ）、ADR-0003（確定前方針は不適用）
- 関連 IADR: 本作業で新規 [IADR-0024](../adr/IADR-0024_report-confirmation-and-policy.md)。版番号確定は [IADR-0012](../adr/IADR-0012_risk-settings-persistence.md) 踏襲
- 対象 Issue: #14（Slice A）

## 目的・背景

取引判断は「確定済み日報の方針」の範囲内でのみ判断する（ADR-0003）。現状その供給元が無く、`IDailyPolicyProvider` は
プレースホルダ（方針なし＝取引しない）でパイプラインが実際に発注へ進めない。本スライスは報告書の確定管理と確定済み日報
方針の照会を実装し、パイプラインを機能させる土台（方針の実体）を提供する。数値集計（FR-16）・LLM ドラフト生成・対話的確定・
KB 保存は後続スライスに切り分ける。

## 対象範囲

### 新規サービス `ReportService`（Domain + Application + Worker）

- **Domain**:
  - `TradingReport`（PeriodKey＝"daily-2026-07-10" 等の自然キー、Kind＝Daily/Weekly/Monthly、PeriodStart、State＝Draft/Confirmed、
    BasedOn＝上位方針の PeriodKey?、AssumptionsVersion、PolicySummary＝翌期間の方針、ConfirmedAt?）。
  - `ReportKind` / `ReportState`。
- **Application**: `IReportStore`（PeriodKey ごとの upsert・Version 楽観排他・確定）、`ReportService`（ドラフト upsert、確定＝
  利用者のみ・版番号付き冪等・Draft→Confirmed の遷移時のみ確定確定、確定済み日報方針の照会）、`ConfirmedDailyPolicy`
  （Date・Summary・AssumptionsVersion）、InMemory 実装。
- **Worker**: EF 永続化（reports テーブル・Version 楽観排他・専有 DB `report_svc`・Migration・IADR-0012 踏襲）、OwnerOnly
  エンドポイント（`GET /reports`、`GET /reports/{periodKey}`、`PUT /reports/{periodKey}`＝ドラフト upsert、`POST /reports/{periodKey}/confirm`、
  `GET /reports/daily-policy`＝確定済み日報方針）。確定の遷移時に `ReportConfirmed` イベントを発行。実行時基盤は test-support shim（IADR-0013）。

### 共有契約（`AiStockTrading.Shared.Contracts`）

- 新規イベント `ReportConfirmed(PeriodKey, Kind, AssumptionsVersion, ConfirmedAt)`。

### 通知サービス（`NotificationService`）

- `ReportConfirmed` を購読し Discord 通知する（FR-09「報告書の確定を Discord に通知」を満たす）。

## 受け入れ基準

CI で緑にする範囲（ユニット＋MassTransit テストハーネス＋EF InMemory＋WebApplicationFactory）:
- [ ] ドラフトを upsert でき、確定（Draft→Confirmed）で ConfirmedAt が記録され Version が上がる。
- [ ] 確定は OwnerOnly（未認証 401・ロール無し 403）。AI・自動処理は確定できない（ADR-0007）。
- [ ] 版番号付き冪等確定: 既に確定済みの再確定は冪等（状態変化なし・イベント重複発行なし）。版不一致は 409。
- [ ] `GET /reports/daily-policy` が最新の確定済み日報方針（Date・Summary・AssumptionsVersion）を返す。未確定なら 404。
- [ ] 確定（遷移時）に `ReportConfirmed` が発行され、`NotificationService` が通知する。
- [ ] Worker が起動しヘルスが応答する。既存テストを緑に保つ。

実 API/実コンテナ前提（CI 既定では実行しない）:
- [ ] PostgreSQL 経由の永続化・確定の E2E。

## 対象外（後続）

- 損益・費用・税の集計（FR-16。取引台帳 #63・前提条件 #19 を参照するコード集計）。本スライスは方針テキストと確定管理に限定。
- LLM によるドラフト文章生成・テンプレート整形。対話的確定（基盤チャットUI・Discord 多層認証・多ターン）。
- 確定報告書の KB 保存・RAG 索引化（FR-08・#18）。
- 無応答時の既定動作（翌営業日まで直近確定日報を継続）・初回月報ブートストラップ・階層（月報→週報→日報）参照の強制。
- 取引判断の `IDailyPolicyProvider` を本サービス照会へ結線（サービス間連携・#22）。本スライスは方針の所有・照会 API を提供するに留める。

## テスト方針

- `TradingReport`／`ReportService` は InMemory ストアで upsert・確定遷移・冪等・版排他・確定済み方針照会を単体検証。
- `EfReportStore` は EF InMemory で永続化・確定・版排他を検証。
- エンドポイントは `ReportWorkerWebApplicationFactory`（InMemory DB・TestAuthHandler）で OwnerOnly・確定・冪等・409・daily-policy・
  `ReportConfirmed` 発行を検証。通知購読は NotificationService 側テストで検証。

## 関連仕様

- 連携先: [20260710_notification-outbound](20260710_notification-outbound.md)（`ReportConfirmed` 購読）、[20260710_configuration-assumptions](20260710_configuration-assumptions.md)（AssumptionsVersion）
- 実装ADR: [IADR-0024](../adr/IADR-0024_report-confirmation-and-policy.md)、踏襲 [IADR-0012](../adr/IADR-0012_risk-settings-persistence.md)

## 未決事項

- 数値集計（FR-16）・LLM ドラフト・対話的確定・KB 保存・無応答既定・取引判断への結線は後続スライスで確定する。
