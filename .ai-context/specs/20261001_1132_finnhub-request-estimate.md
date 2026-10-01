---
title: 実市況 4 サービスの Finnhub 日次要求見積りを運用者の申告銘柄数から外し、巡回するサービス（市場監視・リスク管理）は巡回ごとに保有・監視銘柄の実数から導出し、事象ごとに引くサービス（取引判断・報告書）は見積らない（#1132）
type: spec
status: accepted
related_ids: [FR-01, FR-03, FR-10, ADR-0031, ADR-0043, IADR-0477, IADR-0294, IADR-0437, IADR-0473, IADR-0433]
author: claude (Claude Code)
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0031 (決定 2〜4: Finnhub の日次総量の見積りと同一鍵の合算)
  - planning:projects/ai-stock-trading/07_adr/ADR-0043 (決定 1・3: 日次上限は未実測／1 日の巡回回数は開場中の巡回で数える。見積りは 429 のときに総量を読む材料)
---

# Finnhub 日次要求見積りを実数から導出する（#1132）

## 背景

- **#1132（FR-01・低。監査 2026-10-01 F4）**: 実市況 4 サービスの日次要求見積りの自己申告が、実測と桁で乖離する。

| サービス | 申告（起動時） | 実測（2026-09-30 夜） |
| --- | --- | --- |
| market-monitor | 390 回/日（申告銘柄数 1 × 390 巡回） | 約 9 要求/分（≈ 3,510/場中） |
| risk-management | 1,440 回/日 | 約 3 回/分（24 時間で ≈ 4,320） |
| trade-decision | 1,440 回/日 | 約 470 |
| report | 1,440 回/日 | 0 |

- 原因: 見積りの銘柄数は運用者申告 `MarketData:Finnhub:EstimatedSymbolCount`（IADR-0294）で、`values-local.yaml` は 4 サービスとも `1` に固定。さらに取引判断・報告書は固定間隔で巡回しないのに `RefreshIntervalSeconds`（60 秒）× 24 時間で数えていた。
- 計画 ADR-0043 決定 3 は見積りを「429 が出たときに総量を読むための材料」とする。桁で外れた見積りはその材料にならない。

## 範囲

1. 巡回するサービス（市場監視・リスク管理）の見積りを、**巡回ごとに**その巡回で問い合わせる銘柄（保有＋監視銘柄／保有）の市場から導出して業務メトリクスへ記録する（申告に依らない）。
2. 事象ごとに引くサービス（取引判断・報告書）は見積らない（固定間隔の模型が無い。実数は HTTP クライアントの計装で読む）。
3. 申告の設定点 `EstimatedSymbolCount` と、それを読む起動時の評価・introspection の自己申告（実市況 4 サービスぶん）を撤去する。
4. Helm の values・README、ダッシュボードの説明、テスト仕様書を追随する。IADR-0477、試験 T-10-2010〜、自己変異。

範囲外: 情報収集の見積り（#1100 で文言を直した・自前の銘柄数で数える）・日次上限の実測・同一鍵の合算の自動化（IADR-0294 決定 4 のまま人手）・閉場ごとの 1 回（IADR-0473 決定 3 のまま数えない）・計画 ADR の改訂。

## 母集合（規則 9。origin/develop 58730639）

### 見積り・申告・メトリクスの全出所

`git grep -n -E "EstimatedSymbolCount|EstimateDailyVolume|EvaluateDailyVolume|RecordFinnhubDailyVolumeEstimate|finnhub-daily-request-estimate|daily_request_estimate" -- ':!.ai-context/specs'`

| 出所 | 何をする | 本件 |
| --- | --- | --- |
| `Shared.Infrastructure/.../MarketDataOptions.cs` `EstimatedSymbolCount` | 申告値（既定 0） | **撤去** |
| `MarketDataSourceFactory.EstimateDailyVolume` / `EvaluateDailyVolume` | 申告 × 巡回回数。起動時にメトリクス・ログ | **撤去**（呼び出し元が 0 になる） |
| `MarketMonitorService/Program.cs` 2 か所（起動時評価・introspection） | 申告 1 × 390 | **撤去**。巡回ごとの記録へ |
| `RiskManagementService/Program.cs` 2 か所 | 申告 1 × 390（#1147 後） | **撤去**。巡回ごとの記録へ |
| `TradeDecisionService/Program.cs` 2 か所 | 申告 1 × 1,440 | **撤去**（見積らない） |
| `ReportService/Program.cs` 2 か所 | 申告 1 × 1,440 | **撤去**（見積らない） |
| `InformationCollectionService`（`InformationSourceFactory`・`Program.cs`） | 構成／監視銘柄の実数 × 要求数 × 巡回 | 範囲外（不変） |
| `MarketMonitorService/.../WatchlistVolumeEstimator.cs` | 入れ替え案の適用時の見積り（保有＋監視銘柄の市場から） | 数え方を共有の純関数へ寄せる（値は不変） |
| `BusinessMetrics.RecordFinnhubDailyVolumeEstimate`・`BusinessMetricNames` | 計器 | 不変（名前も変えない） |
| ダッシュボード `ai-stock-trading-business.json` パネル 10・11 | 系列の表示 | 説明だけ直す |
| `deploy/helm/.../values.yaml`（4）・`values-local.yaml`（4） | `EstimatedSymbolCount` の設定点 | **撤去**。取引判断・報告書の `Finnhub__ProvisionalDailyLimit` も読む者が無くなるので撤去 |
| `deploy/helm/.../README.md` §Finnhub の日次要求量の見積り | 運用手順 | 書き直す |
| `.gitleaks.toml`・`.gitleaksignore` | 設定キー名の誤検知の除外 | 残す（凍結記録・本仕様書がキー名を書く） |
| `docs/tests/FR-10_risk-controls-tests.md` T-10-1434・T-10-1969 | 申告を前提にした行 | 書き直す |
| 試験: `MarketDataSourceFactoryDailyVolumeTests`・`FinnhubDailyPremiseWithdrawnTests`（T-10-1434）・`QuoteRefreshClosedMarketTests`（T-10-1969） | 撤去する API を使う | 書き直す |

### 各サービスの実際の要求経路（何をきっかけに何回引くか）

`git grep -n "GetLatestQuoteAsync" -- backend/Services ':!*/Tests/*'`

| サービス | きっかけ | 1 回あたりの要求 | 抑制 | 導出できるか |
| --- | --- | --- | --- | --- |
| 市場監視 `MarketMonitorAppService.EvaluateRoundAsync` | `MonitorPollingService` の巡回（`Monitor:PollIntervalSeconds` 既定 60 秒）。全市場が閉じていれば巡回しない | 開場中の市場の保有 1 件ずつ＋監視銘柄 1 件ずつ（同じ銘柄でも 2 要求）。米国以外は Finnhub へ送らない（`FinnhubMarketDataSource`） | 閉場中の市場の銘柄は照会しない | **できる**: Σ_銘柄 要求数(市場) × (場中の分 × 60 ÷ 間隔) |
| リスク管理 `QuoteRefreshService.RunOnceAsync` | 補充の巡回（`MarketData:RefreshIntervalSeconds` 既定 60 秒） | 保有建玉 1 件ずつ | 閉場中は閉場ごとに 1 回だけ（`QuoteSessionFreshness`。IADR-0473） | **できる**: 同上（保有のみ）。閉場ごとの 1 回は数えない（IADR-0473 決定 3） |
| 取引判断 `MarketDataCurrentPriceProvider` | 判断 1 回（価格変動・定時サイクル等の事象） | 1 件 | なし | **できない**（事象の数で決まる） |
| 報告書 `ReportDraftService.ResolveCurrentPricesAsync` | 報告書ドラフトの生成 1 回 | 期間末に保有中の銘柄 1 件ずつ | `LastKnownQuoteSource`（取れないときの前回値） | **できない**（生成の数で決まる） |

### 導出値の計算し直し（規則 10）

実測の夜（2026-09-30）の構成で、本件の式が出す値:

- 市場監視: 実測 9 要求/分 ＝ 1 巡回 9 要求（間隔 60 秒）。米国 9 銘柄（保有＋監視銘柄）× 390 巡回 ＝ **3,510**（実測 ≈ 3,510/場中と一致）。
- リスク管理: 保有 3（米国）× 390 ＝ **1,170**。実測の 4,320 は #1147 より前（閉場中も 60 秒ごと＝24 時間）。#1147 後は 1,170 ＋ 閉場ごと 3。
- 取引判断・報告書: 記録しない（是正前の 1,440 は根拠の無い値）。

**この変更で新たに誤りになる自分の記述**（規則 10）: `git grep -n -E "申告銘柄数|運用者申告|EstimatedSymbolCount|finnhub-daily-request-estimate" -- deploy docs backend ':!*.ai-context*'` で是正後に、申告を**現役の仕組みとして**述べる記述が 0 件であることを確かめる（残るのは情報収集の自己申告・凍結記録・gitleaks の除外・是正の経緯を述べるコメントだけ）。実施結果: `BusinessMetricNames.FinnhubDailyVolumeEstimate` の説明「銘柄数の運用者申告（既定 0）が無ければ計上しない」が漏れていたので直した。

### 窓（規則 11）

本件の見積りは「1 巡回の銘柄の集合 × 1 日の巡回回数」の積で、時刻の差を扱う窓は無い。ただし記録の時点に偏りが 1 つある:

| 形 | 増える側（保有・監視銘柄が増えた） | 減る側（減った） | 閉場中 |
| --- | --- | --- | --- |
| 開場中の銘柄だけで数える（照会した数） | 次の巡回で上がる | 次の巡回で下がる | 🔴 市場監視は閉場の市場を 0 に数える（米国の閉場中に東証だけ開いた巡回で 0 になる） |
| **市場に関係なく、巡回の対象の全銘柄で数える（採用）** | 次の巡回で上がる | 次の巡回で下がる | 値を保つ（ゲージは最後の値） |
| 起動時だけ数える（是正前） | 🔴 上がらない | 🔴 下がらない | 値を保つ |

採用形の残り: 市場監視は全市場が閉じている間は巡回しないため、閉場中の銘柄の増減は次の開場の最初の巡回まで反映されない（見積りは開場中の量なので実害は無い）。

## 設計（IADR-0477）

- 共有の純関数 `FinnhubDailyVolumeEstimator.EstimateForSymbols(symbolMarkets, pollIntervalSeconds)` ＝ Σ `RequestsPerSymbol(市場)` × `CyclesPerDay(間隔, MarketSessions.RegularSessionMinutes(市場))`。`RequestsPerSymbol` は米国 1・それ以外 0。市場監視の `WatchlistVolumeEstimator` も同じ関数を使う（数え方を 1 つにする）。
- 共有の記録器 `FinnhubDailyVolumeRecorder`: Provider が finnhub で鍵があるときだけ記録する（それ以外は Finnhub へ送らない＝記録しない）。毎回メトリクスへ記録し、値が変わったときだけログ（上限未設定は Information、上限超過は Warning、以内は出さない）。
- 市場監視: `MonitorRoundResult.QuotedSymbolMarkets`（保有＋監視銘柄の市場。開場に関係なく）を `MonitorPollingService` が記録器へ渡す。
- リスク管理: `QuoteRefreshService.RunOnceAsync` が保有建玉の市場を記録器へ渡す。
- 取引判断・報告書・introspection: 撤去。

## 受け入れ基準

1. 市場監視の巡回 1 回で、保有＋監視銘柄（米国）の数 × 390（60 秒巡回）が記録される（例: 9 銘柄 → 3,510）。東証の銘柄は 0。閉場中の市場の銘柄も数える。
2. リスク管理の補充 1 回で、保有建玉（米国）の数 × 390 が記録される（例: 3 → 1,170）。閉場中の巡回でも同じ値。
3. Provider が finnhub でない・鍵が無いときは記録しない（挙動中立）。
4. 上限未設定なら比率を記録せず警告も出さない。設定して超えたら警告と比率。ログは値が変わったときだけ。
5. 取引判断・報告書は起動時にも見積りを記録しない。
6. 申告の設定点は helm・コードから消え、運用手順は「実数から導出・事象ごとのサービスは見積らない」と書く。
7. ビルド・試験・文書検査が緑。

## 試験

T-10-2010〜T-10-2019（xUnit v3 ＋ AwesomeAssertions）。テスト仕様書 FR-10 に節を足し、T-10-1434・T-10-1969 の行を書き直す。

## 自己変異（5 個以上）

結果は IADR-0477 とテスト仕様書の表に記録する。
