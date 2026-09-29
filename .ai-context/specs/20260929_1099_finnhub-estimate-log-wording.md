---
title: 情報収集の Finnhub 日次見積りの起動ログで、1 巡回の上限を「銘柄数」と表示しない（#1099）
type: spec
status: accepted
related_ids: [FR-01, FR-13, ADR-0031, ADR-0043, IADR-0437]
author: claude (Claude Code)
created: 2026-09-29
updated: 2026-09-29
plan_refs: []
---

# Finnhub 日次見積りの起動ログの文言（#1099）

## 背景

PoC の起動ログに `Finnhub の日次要求見積り … 回/日（銘柄数 75 × 1 巡回 2 要求 × …）` が出た。一方、監視銘柄の実数は 6 である（#1099）。
監視銘柄に追随する構成では、起動時の見積りを 1 巡回の上限で数える。これは IADR-0437 の設計どおりである（自己申告は起動時に 1 回だけ決まるため）。
ただし、ログの文言が構成の固定リストのときと同じ「銘柄数 N」だった。そのため、上限を実数と読み違えさせていた。
起点は FR-01 である。計画 ADR の新たな制約は無い。見積りの値・メトリクス・自己申告は変えない。

## 母集合（規則 9）

`git grep -n "銘柄数 {Symbols}\|銘柄数 6\|\"銘柄数" -- backend docs` で走査した。

| 箇所 | 扱い |
| --- | --- |
| `InformationSourceFactory.EvaluateDailyVolumeEstimate` の 2 つのログ（超過の警告・比べない情報） | **変更**（`symbolCount` を渡したときは上限の文言） |
| `FinnhubDailyEstimateFollowsWatchlistTests`（T-10-1452）の `"銘柄数 6"` の期待 | **変更**（上限の文言を期待し、固定リストの文言は出ないことも試す） |
| `MarketDataSourceFactory`（共有物）の「申告銘柄数」 | 変えない（市場データ側は構成の固定リストだけで数える。上限を渡す経路が無い） |
| `docs/tests/FR-10_risk-controls-tests.md` の T-10-1452 の行 | **変更**（期待に文言を足す） |

## 設計

- `symbolCount` が null なら（構成の固定リスト）、従来の「銘柄数 {Symbols}」のまま。
- `symbolCount` を渡したら（監視銘柄に追随）、次の文言にする。
  「1 巡回の対象の上限 {Symbols} 銘柄（監視銘柄に追随。実数は巡回ごとにメトリクスへ記録）」
  - 構造化ログの名前つきの値（`{Symbols}` ほか）は変えない。テンプレートを 2 つに分ける。

## 受け入れ基準 → 試験

- T-10-1452: `symbolCount: 6` の情報ログは「1 巡回の対象の上限 6 銘柄」を含み、「銘柄数 6」を含まない。
- T-10-1740（追加）: 固定リスト（`symbolCount` 省略）の情報ログは、従来どおり「銘柄数 1」を含み、上限の文言を含まない。
- T-10-1741（追加）: 上限を超える警告も、`symbolCount` を渡したときは上限の文言になる。

## 検証

- `dotnet test backend/Services/InformationCollectionService/Tests`: 629/629 成功。
- `dotnet format --verify-no-changes`（InformationCollectionService）: 差分なし。
- 変異の確認:
  - 常に固定リストの文言にする変異は、2 件落ちる（T-10-1452・T-10-1741）。
  - 常に上限の文言にする変異は、1 件落ちる（T-10-1740）。
- `check-test-traceability` / `check-trace-blocks` / `check-doc-links` / `gen-knowledge-graph --check`: OK。
