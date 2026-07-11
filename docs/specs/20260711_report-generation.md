---
title: 報告書生成（数値集計の組み立て＋テンプレート化・LLM ドラフトはポート抽象）
type: spec
status: review
related_ids: [FR-06, FR-07, FR-16, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/06_technical/04_report-templates.md
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: 報告書の生成（数値集計の組み立て＋テンプレート化＋LLM ドラフト抽象）

> Issue [#14](https://github.com/endazon/ai-stock-trading/issues/14)（`Refs #14`）。報告書サービス（#14）の**日報ドラフト生成**を実装する。
> `PnlAggregator`（FR-16・IADR-0025）で**コード集計した数値**を報告書テンプレート（04_report-templates）に**コードで組み立て**、
> 散文（市況・振り返り等）は **LLM ドラフト（ポート抽象・本スライスは fake/プレースホルダ）** で埋める。数値は LLM に計算させない。

## 起点となる計画書・課題（トレーサビリティ）

- FR-06（報告書の自動生成）/ FR-07（確定前方針は不適用）/ FR-16（数値はコードで集計・LLM に計算させない）
- テンプレート定義: 06_technical/04_report-templates.md（日報テンプレート・数値定義・YAML フロントマター）
- ADR-0003（数値集計と LLM 生成の分離）。関連 IADR: IADR-0025（PnlAggregator）。本作業で新規 [IADR-0032](../adr/IADR-0032_report-generation.md)。
- 対象 Issue: #14。

## コンテキストと課題

報告書サービスは `PnlAggregator`（数値集計）・版番号付き確定・確定済み日報方針の照会までは実装済み（PR #69/#70）。しかし
**報告書ドキュメントそのものの生成（テンプレートへの数値の組み立て＋散文ドラフト）が未実装**で、FR-06/FR-16 の中核が欠けている。

## 対象範囲

### 報告書ドメイン `ReportService.Domain`（テンプレート化・純関数）

- `DailyReportView`（レンダリング入力: PeriodKey・日付・市場・前提バージョン・BasedOn・確定日時・`PnlSummary`・買/売件数・方針・散文）。
- `ReportRenderer.RenderDailyMarkdown(view)`（純関数）: YAML フロントマター＋当日サマリ表（数値は `PnlSummary` 由来）＋
  散文セクション（LLM ドラフト）＋翌営業日方針を、04_report-templates の日報形式で Markdown 生成する。**数値はすべてコード集計値**。

### 報告書アプリ `ReportService.Application`（オーケストレーション・LLM ポート）

- ポート `IReportNarrativeDrafter`（`Task<string> DraftDailyNarrativeAsync(DailyNarrativeContext, CancellationToken)`）＝散文ドラフトの LLM 抽象。
- `ReportDraftService.BuildDailyDraftAsync(req, ct)`: 約定列＋前提条件から `PnlAggregator.Aggregate` で数値集計 → 買/売件数を集計 →
  `DailyNarrativeContext` を組み立てて drafter で散文取得 → `DailyReportView` を構築 → `ReportRenderer` で Markdown 生成。返り値は Markdown＋`PnlSummary`。

### 報告書ワーカー `ReportService.Worker`

- `PlaceholderReportNarrativeDrafter`（安全既定: LLM 未接続の旨の定型散文を返す・初回 1 回警告）。実 LLM ドラフト（platform ゲートウェイ）は後続。
- エンドポイント `POST /reports/{periodKey}/draft`（OwnerOnly）: 日報ドラフト（Markdown＋数値）を返す。**永続化しない**（本スライスは生成のみ・保存/KB は後続）。

## 受け入れ基準

CI で緑にする範囲（ユニット＋WebApplicationFactory）:
- [ ] `ReportRenderer`: 当日サマリ表に `PnlSummary` の数値（実現損益税引後・評価損益・費用・税・取引回数）が定義どおり反映される。
- [ ] `ReportRenderer`: YAML フロントマター（report_type/period/assumptions_version/based_on 等）を含む。
- [ ] `ReportRenderer`: 散文（LLM ドラフト）と翌営業日方針が本文に挿入される。
- [ ] `ReportDraftService`: 約定列から数値を集計し、drafter の散文を含む Markdown を生成する（fake drafter）。
- [ ] `POST /reports/{periodKey}/draft` が OwnerOnly（401）で日報ドラフトを返す。
- [ ] 既存テストを緑に保つ。

実 API/実コンテナ前提（CI 既定では実行しない）:
- [ ] 実 LLM ゲートウェイでの散文ドラフト生成・#63 台帳/#19 前提条件の実データ連携。

## 対象外（後続）

- 実 LLM ドラフト（platform ゲートウェイ）。週報・月報テンプレート。取引履歴明細/ポジション/リスク統制セクション（#63 台帳・#12 連携＝#22）。
- ドラフト本文の永続化・KB 保存（FR-08・#18）。対話的確定（FR-14・#15）。#19 バージョン付き前提条件の取得（現状は既定値）。

## テスト方針

- `ReportRenderer` は純関数で表・フロントマター・散文挿入を検証。
- `ReportDraftService` は fake drafter＋既定前提で数値集計と Markdown 生成を検証。
- エンドポイントは `ReportWorkerWebApplicationFactory`（TestAuthHandler）で OwnerOnly・生成結果を検証。

## 関連仕様

- 連携元: [20260710_report-confirmation](20260710_report-confirmation.md)（報告書確定・PnlAggregator）
- 実装ADR: [IADR-0032](../adr/IADR-0032_report-generation.md)（新規・数値=コード/散文=LLM の分離）／[IADR-0025](../adr/IADR-0025_pnl-aggregation.md)

## 未決事項

- 実 LLM ドラフト・週報/月報・明細セクションの実データ連携・本文永続化は #14/#22/#18 の後続で確定する。
