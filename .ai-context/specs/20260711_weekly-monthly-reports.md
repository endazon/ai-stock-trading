---
title: 週報・月報テンプレートの生成（ReportRenderer を ReportKind 拡張）
type: spec
status: review
related_ids: [FR-06, FR-16, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: 週報・月報テンプレートの生成（ReportRenderer を ReportKind 拡張）

> Issue [#14](https://github.com/endazon/ai-stock-trading/issues/14)（`Refs #14`）。日報生成（PR #86・IADR-0032）を **週報・月報**へ拡張する。
> `ReportRenderer` を `ReportKind`（Daily/Weekly/Monthly）でディスパッチし、週報=ISO 週・月報=年月の集計期間に対応する。
> 数値はコード集計（FR-16）、散文は LLM ドラフト（ポート抽象）という IADR-0032 の責務分離を維持する。

## 起点となる計画書・課題（トレーサビリティ）

- FR-06（報告書の自動生成）/ FR-16（数値はコード集計・LLM に計算させない）。テンプレート定義: 04_report-templates（週報・月報）。
- 関連 IADR: [IADR-0032](../adr/IADR-0032_report-generation.md)（数値=コード/散文=LLM/テンプレート化=純関数。週報/月報は本 IADR の後続として明記済み）。新規 IADR は不要（既存決定の適用）。
- 対象 Issue: #14。

## 対象範囲

### 報告書ドメイン `ReportService.Domain`

- `PnlSummary` に `WinningTradeCount`（勝ち決済件数）を追加。`PnlAggregator` で実現損益 > 0 の決済を数える（勝率＝勝ち/決済・FR-16）。
- `ReportPeriod`（純関数）: `Label(kind, date)`（daily=`yyyy-MM-dd`／weekly=`yyyy-Www`(ISO 週)／monthly=`yyyy-MM`）・`ExpectedKey(kind, date)`（`{kind}-{label}`）。
- `DailyReportView` → `ReportView`（`Kind`・`PeriodLabel` を追加、`Date` は撤去）。
- `ReportRenderer.RenderMarkdown(ReportView)`（純関数）: `Kind` で report_type・タイトル・サマリ表の項目・セクション見出し・方針見出しを切り替える。週報/月報は勝率行を含む。数値はすべて `PnlSummary`。

### 報告書アプリ `ReportService.Application`

- `IReportNarrativeDrafter` を汎用化（`DraftNarrativeAsync(ReportNarrativeContext)`・`ReportNarrativeContext` に `Kind`/`PeriodLabel`/`Markets`/`Pnl`/`PolicySummary`）。
- `ReportDraftService.BuildDraftAsync(DraftRequest)`（`Kind` 対応）: PeriodLabel を算出し PeriodKey を `ReportPeriod.ExpectedKey` と厳密照合（不整合は 400）→ 数値集計 → 散文ドラフト → `ReportRenderer` で組み立て。

### 報告書ワーカー `ReportService.Worker`

- `PlaceholderReportNarrativeDrafter` を汎用メソッドに更新（安全既定）。
- `POST /reports/{periodKey}/draft` の要求に `Kind` を追加（日報/週報/月報を生成）。

## 受け入れ基準

CI で緑にする範囲（ユニット＋WebApplicationFactory）:
- [ ] `ReportPeriod.Label/ExpectedKey` が daily/weekly(ISO 週)/monthly の各形式を返す。
- [ ] `PnlAggregator` が `WinningTradeCount`（勝ち決済数）を算出する。
- [ ] `ReportRenderer` が週報（`report_type: weekly`・週間サマリ・勝率・翌週方針）を生成する。
- [ ] `ReportRenderer` が月報（`report_type: monthly`・月間サマリ・翌月方針）を生成する。
- [ ] 日報の出力は従来どおり（回帰・#86 のテスト維持）。
- [ ] `ReportDraftService` が週報/月報の PeriodKey 整合検証と Markdown 生成を行う（fake drafter）。
- [ ] `POST /{periodKey}/draft` が Kind=Weekly の週報を返す（OwnerOnly）。

実 API/実コンテナ前提（CI 既定では実行しない）:
- [ ] 実 LLM ドラフト・#63 台帳/#19 前提条件の実データ連携。

## テンプレート対応・差異（04_report-templates §サマリ）

fixed 済み計画テンプレート（04_report-templates）の**サマリ行構成に一致**させる。現スライスでコード集計できる数値は値を入れ、
市場データ/台帳連携（#22/#63/#12/#81）が必要な行は `（データ連携後）` プレースホルダで**形式を保つ**（FR-16「決まった形式」）。

| 種別 | 計画サマリ行 | 本スライスの扱い |
| --- | --- | --- |
| 週報 | 週間実現損益 | ✅ 集計値（税引後・費用込み） |
| 週報 | 勝率（勝ち取引/全決済取引） | ✅ 集計値・計画の `<n%（n/n）>` 形式（`WinningTradeCount`） |
| 週報 | 取引回数（うち変動トリガー・損切り） | ⚠ 代替表記 `取引回数（買/売/決済）`（トリガー/損切り内訳は #63 台帳連携＝後続） |
| 週報 | 費用合計 | ✅ 集計値 |
| 週報 | 週次目標に対する達成 | ⏳ `（データ連携後）`（目標入力・評価は #22/確定フロー後続） |
| 月報 | 月間実現損益 | ✅ 集計値 |
| 月報 | 総資産（月初 → 月末） | ⏳ `（データ連携後）`（資産推移＝#81/#22） |
| 月報 | 年初来累計損益 | ⏳ `（データ連携後）`（YTD＝#22） |
| 月報 | 費用合計 / 費用率 | ✅ 費用合計は集計値／費用率は `（データ連携後）`（資金連携＝#22） |
| 月報 | 月次目標に対する達成 | ⏳ `（データ連携後）` |
| 日報 | 取引回数（買/売/見送り） | ⚠ 代替表記 `取引回数（買/売/決済）`（見送り件数は取引判断の見送りデータ＝後続。#86 由来） |
| 日報 | 日次目標に対する達成 | ⏳ `（データ連携後）` 行を追加（形式整合） |

「決まった形式」を保ちつつ、データ依存の値のみを後続連携（#22 ほか）で埋める方針。テンプレートに無い独自行は追加しない。

## 対象外（後続）

- 実 LLM ドラフト（platform ゲートウェイ）。日別/週別推移・市場別内訳・ハイライト取引・税金/NISA/資産推移などデータ依存セクション（#63 台帳・#12・#81 連携＝#22）。本文永続化・KB 保存（#18）。対話的確定（#15）。#19 バージョン付き前提条件。
- 上表の `（データ連携後）` 行の値埋め（トリガー/損切り内訳・総資産・年初来・費用率・目標達成評価）は #22/#81 の後続で行う。

## テスト方針

- `ReportPeriod`・`ReportRenderer`（週報/月報/日報回帰）・`PnlAggregator`（勝ち数）は純関数で検証。
- `ReportDraftService` は fake drafter で週報/月報生成と PeriodKey 検証を確認。
- エンドポイントは WebApplicationFactory で Kind=Weekly の生成を検証。

## 関連仕様

- 連携元: [20260711_report-generation](20260711_report-generation.md)（日報生成）
- 実装ADR: [IADR-0032](../adr/IADR-0032_report-generation.md)（週報/月報は本 IADR の適用）／[IADR-0025](../adr/IADR-0025_pnl-aggregation.md)

## 未決事項

- データ依存セクション・実 LLM・本文永続化は #14/#22/#18 の後続で確定する。
