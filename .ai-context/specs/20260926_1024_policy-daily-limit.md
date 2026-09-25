---
title: /policy に 1 日の回数上限を置き、月報 §7 に /policy の回数と費用を載せる（#1024）
type: spec
status: accepted
related_ids: [FR-14, FR-06, FR-07, ADR-0042, ADR-0037, IADR-0432, IADR-0431, IADR-0318]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0042_discord-apply-ai-watchlist-proposal-and-revision-limit.md (決定 3)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§6・§6.1)
  - planning:projects/ai-stock-trading/06_technical/04_report-templates.md (月報 §7)
---

# 仕様書: `/policy` の 1 日の回数上限と月報 §7 の実績（#1024）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-14（Discord の修正指示）、FR-06（月報）、FR-07
- 関連 ADR: ADR-0042 決定 3（1 日の回数上限・設定値・既定値と 1 回あたりの費用の見積りを IADR に残す・月次 LLM 上限に算入しない・月報 §7 に用途別の実績）
- 関連 IADR: IADR-0432（本件）、IADR-0431（`/policy`）、IADR-0318（計上の境界での付け替えの先例）

## 母集合（規則 1〜6・9）

- 計上区分の分別を持つ箇所: `git grep -n "IsStage0Recording\|LlmCostScope.IsGoverned"` → `LlmUsageAggregator`（月報 §7）と
  `LlmCostIncurredHandler`（費用統制）。後者は `IsGoverned` だけを見る（新しい区分は自動で対象外）ので変更不要。
- 月報 §7 の行を固定する試験: `ReportRendererReportingCycleTests`（行の文言）・`ReportTemplateGoldenTests`（`monthly-supplied.md`）。
- 用途キー `report-daily` の計上を前提にした試験: `LlmReportPolicyReviserTests`（T-10-1307）。
- 除外: `docs/observability/observability.md`（`category` タグ Llm / LlmUncapped の説明。新区分は LlmUncapped に入り記述は正しい）。

## 決定する挙動

| 状況 | 挙動 |
| --- | --- |
| 本日（JST）の試行数 < 上限 | 1 行書いてから LLM を呼び、結果で閉じる。応答に「本日の /policy: n/上限 回目」 |
| 本日（JST）の試行数 ≥ 上限 | LLM を呼ばず 429。上限・実行済みの回数・「方針は変わっていません」 |
| AI の失敗・保存の失敗 | 行は AiFailed / SaveFailed で閉じる（上限に数える） |
| 入力の検証・対象の決定で断った | 行を書かない（数えない） |

- 上限: 構成 `Reports:PolicyRevision:DailyLimit`（既定 10。不正・1 未満は既定）。1 回あたりの費用の見積りは IADR-0432 決定 1。
- 計上区分 `policy-revision`（用途キーは `report-daily` 等のまま）。月報 §7 に回数（計上の件数）と費用の行。

## 受け入れ基準 → テスト（T-10-1356〜T-10-1361）

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | 上限で LLM を呼ばない・失敗も数える・前日は数えない・断った要求は数えない | `ReportPolicyRevisionServiceTests`（T-10-1356） |
| 2 | 呼ぶ前に書き、結果・版・入れ替え案で閉じる。検証で断った要求は書かない | 同（T-10-1357） |
| 3 | 構成値の読み方 | 同（T-10-1358） |
| 4 | 計上区分は独立（上限・報告書・その他に入らない）・回数と費用 | `LlmUsageAggregatorTests`（T-10-1359）・`LlmReportPolicyReviserTests`（T-10-1307 の改訂） |
| 5 | 月報 §7 の行（あり／なし） | `ReportRendererReportingCycleTests`（T-10-1360）・ゴールデン |
| 6 | 本番の組み立てで 429・LLM を呼ばない・版が進まない | `PolicyRevisionWiringTests`（T-10-1361） |
