---
title: 作業仕様書 — 存在しない IADR-0292 の引用を IADR-0294 へ直す
type: spec
status: done
related_ids: [NFR, FR-01, ADR-0031, IADR-0294]
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
plan_refs: []
---

# 作業仕様書 — 存在しない IADR-0292 の引用を IADR-0294 へ直す（NFR）

> 着手 2026-11-08（develop `0dcdf51ab`）。コード・設定のコメントだけを直す是正で、決定は変えない（新 IADR は起こさない）。

## 事実（実測）

- `.ai-context/adr/` に `IADR-0292_*` は無い（`IADR-0291` の次は `IADR-0293`）。欠番は IADR-0280 により許される。
- `IADR-0294_finnhub-daily-volume-estimate-and-provisional-limit-warning.md` は実在する。主題は Finnhub の日次要求量を
  見積もる純関数 `FinnhubDailyVolumeEstimator`（決定1）、`FinnhubDailyVolumeGuardOptions` の日次上限（決定1・3）、
  業務メトリクス `ast.finnhub.daily_request_estimate` / `..._limit_ratio_percent`（決定3）、introspection の
  `Metrics` 枠（`IntrospectionBuilder.AddMetric`。決定4）、情報収集は静的構成の実数から見積もる（決定2）。
- 置換対象の 19 行（コード・設定）はすべて #668（`229a413b4`）で入った（除外の 1 行は #680 `ce7185b27` の作業仕様書）。#668 は IADR-0294 を新設し、IADR-0292 は新設していない。
  起票時の番号の書き違いと判断する。

## 母集合（規則 9: 誤りの側の文字列で全リポジトリを走査）

`git grep -n -E 'IADR[- _]?0?292\b' origin/develop` → 20 行＝置換 19 行＋除外 1 行（表記揺れ `IADR 292` 等は 0 件）。依頼文の「約 16 件」は使わない（規則 10）。

| # | ファイル:行 | 主題 | 処置 |
| --- | --- | --- | --- |
| 1 | `backend/Services/InformationCollectionService/Infrastructure/ExternalServices/InformationSourceFactory.cs:136` | 情報収集ぶんの日次要求見積り（決定2） | → IADR-0294 |
| 2 | 同 `:179` | 同上 | → IADR-0294 |
| 3 | `backend/Services/InformationCollectionService/Program.cs:60` | 見積りの introspection 自己申告（決定4） | → IADR-0294 |
| 4 | 同 `:188` | 情報収集ぶんの見積り（決定2） | → IADR-0294 |
| 5 | `backend/Services/InformationCollectionService/Tests/Infrastructure/ExternalServices/InformationSourceFactoryDailyVolumeTests.cs:11` | 見積りの配線 | → IADR-0294 |
| 6 | `backend/Shared/AiStockTrading.Shared.Contracts.Tests/BusinessMetricsTests.cs:293` | 見積りと比率のメトリクス（決定3） | → IADR-0294 |
| 7 | `backend/Shared/AiStockTrading.Shared.Contracts/Observability/BusinessMetricNames.cs:144` | 見積りメトリクス名（決定3） | → IADR-0294 |
| 8 | 同 `:151` | 比率メトリクス名（決定3） | → IADR-0294 |
| 9 | `backend/Shared/AiStockTrading.Shared.Contracts/Observability/BusinessMetrics.cs:624` | 見積り・比率の記録（決定3） | → IADR-0294 |
| 10 | `backend/Shared/AiStockTrading.Shared.Infrastructure.Tests/MarketData/FinnhubDailyVolumeEstimatorTests.cs:7` | 純関数の境界（決定1） | → IADR-0294 |
| 11 | 同 `:15` | 情報収集の既定間隔での巡回回数（決定1 の `floor(86400/間隔)`） | → IADR-0294 |
| 12 | `backend/Shared/AiStockTrading.Shared.Infrastructure/Composable/Adapters/MarketData/FinnhubDailyVolumeEstimator.cs:5` | 純関数（決定1） | → IADR-0294 |
| 13 | `backend/Shared/AiStockTrading.Shared.Infrastructure/Composable/Adapters/MarketData/FinnhubDailyVolumeGuardOptions.cs:6` | 日次上限（決定1・3） | → IADR-0294 |
| 14 | `backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Introspection/IntrospectionDtos.cs:8` | `Metrics` 枠（決定4） | → IADR-0294 |
| 15 | 同 `:30` | 自己申告値 DTO（決定4） | → IADR-0294 |
| 16 | `backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Introspection/IntrospectionExtensions.cs:100` | `AddMetric`（決定4） | → IADR-0294 |
| 17 | `deploy/helm/ai-stock-trading/README.md:426` | 見積りの節見出し | → IADR-0294 |
| 18 | `deploy/helm/ai-stock-trading/values-local.yaml:276` | 日次上限（決定3） | → IADR-0294 |
| 19 | `deploy/helm/ai-stock-trading/values.yaml:805` | 日次上限（決定3） | → IADR-0294 |
| 20 | `.ai-context/specs/20260903_679_finnhub-daily-limit-empty-config.md:8`（frontmatter `adr_refs`） | — | **除外** |

（#1〜#19 のうち C# は 16 行・設定/README は 3 行。依頼文の「約 16 件」は C# の行数と一致するが、母集合は 19 行である。）
0294 以外を指すべき引用、意図が決められない引用はいずれも 0 件。

## 除外と理由

- **`.ai-context/specs/20260903_679_finnhub-daily-limit-empty-config.md:8`**: 作業仕様書は point-in-time の凍結記録
  （CLAUDE.md・traceability.repo.md §除外）。同じ `adr_refs` に正しい `IADR-0294` が併記されており、追跡は切れない。書き換えない。
- **`.ai-context/adr/` の本文**: 走査で 0 件。直す対象が無い（日付つき追記の要否も生じない）。
- `CHANGELOG.md`: 0 件。

## 自分の記述の引き直し（規則 10）

- 変更後に `git grep -n 'IADR-0292'` を引き、残るのは除外 1 行と本仕様書だけであることを確かめる。
- 導出値（19 行・C# 16 行）は表から数え直した。

## 受け入れ基準

- [x] #1〜#19 の `IADR-0292` が `IADR-0294` になり、それ以外の文字は変わらない。
- [x] `dotnet build backend/backend.slnx` と変更範囲の `dotnet format --verify-no-changes` が通る。
- [x] 文書系検査（check-trace-blocks / gen-knowledge-graph --check / check-commit-messages / check-cross-repo-refs / check-plan-id-qualification）が通る。
