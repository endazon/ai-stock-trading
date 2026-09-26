---
title: IADR-0434 市場監視の Finnhub 自制レートを 5→12 回/分へ引き上げ、1 巡回（保有＋監視銘柄）が巡回間隔 60 秒に収まる要求数を 12 にする（同一鍵の合計 57 回/分）
type: impl-adr
status: Accepted
related_ids: [FR-03, FR-01, FR-13, SC-02, ADR-0031, ADR-0042, IADR-0275, IADR-0224, IADR-0294, IADR-0433, IADR-0068]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0043_finnhub-daily-premise-withdrawn-and-cycle-fit-control.md (決定 2・5)
  - planning:projects/ai-stock-trading/07_adr/ADR-0031_finnhub-rate-limit-minute-confirmed-daily-open.md (決定 4)
related_specs:
  - ../specs/20260926_1030_finnhub-cycle-fit-control.md
---

# IADR-0434: 市場監視の Finnhub 自制レートを 12 回/分へ引き上げ、1 巡回が巡回間隔に収まるようにする

- 状態: Accepted
- 日付: 2026-09-26
- 決定者: claude（起票 [#1030](https://github.com/endazon/ai-stock-trading/issues/1030)。計画 ADR-0043 決定 2・5〔planning#667 の利用者裁定 2026-09-26〕への追随。
  利用者レビューは PR で受ける）

> 計画 ADR-0043 は本 IADR の作成時点で本リポジトリの宣言レンジ（`ADR-0001..0042`）の外にあるため、frontmatter の `related_ids` には入れず
> `plan_refs` と本文で引く（レンジの引き直しは別の PR）。

## 起点・関連

- 対象 Issue: #1030（PR 1＝本 IADR。構成と文書だけ）
- 関連する実装仕様書: [20260926_1030_finnhub-cycle-fit-control](../specs/20260926_1030_finnhub-cycle-fit-control.md)
- 関連 IADR: [IADR-0275](IADR-0275_finnhub-effective-rate-limit-measurement.md)（分次 60 回/60 秒の固定ウィンドウの実測・**同一鍵の実測**・既定 5 への是正）、
  [IADR-0224](IADR-0224_rate-limits-as-settings-and-unmeasured-daily-quota.md)（推測値を焼き込まない）、
  [IADR-0294](IADR-0294_finnhub-daily-volume-estimate-and-provisional-limit-warning.md)（日次の見積り。数え方は後続の PR で改める）、
  [IADR-0433](IADR-0433_policy-watchlist-proposal-apply-from-discord.md)（Discord からの入れ替えの適用）

## コンテキスト

計画 ADR-0043 決定 2 は、監視銘柄を増やしてよい範囲を次の 2 条件で定めた。

- **(a)** 同じ鍵を使うすべてのプロセスの自制レートの合計 ≤ 60 回/分。
- **(b)** 銘柄を巡回するプロセスごとに、1 巡回の要求数 ÷ 自制レート（回/分）≤ 巡回間隔（分）。市場監視は保有銘柄と監視銘柄を合わせて数える。

稼働中の PoC（経路 B・`values-local.yaml`）は監視銘柄 6 件（AAPL・MSFT・NVDA・AMZN・GOOGL・META）と保有 AAPL を持つ。市場監視
（`MarketMonitorAppService.EvaluateRoundAsync`）は保有と監視銘柄を**別々のループで**照会し、重複を除かない。したがって 1 巡回は **7 要求**である。
市場監視の自制レートは既定 5 回/分（helm に上書きなし）、巡回間隔は既定 60 秒（同）なので、7 要求は 84 秒かかり **(b) を満たさない**
（ADR-0043 実測 7 の 6 銘柄・72 秒より悪い）。米国市場は 2026-09-28 22:30 JST に開くため、コードの統制（後続の PR）より先に構成で (b) を回復する。

### 予算表（着手時点の実測。`git grep` と chart・appsettings の読み取りによる）

稼働構成は `ASPNETCORE_ENVIRONMENT=Development`（`templates/deployment.yaml`）なので、`appsettings.Development.json` の値が効き、helm の env がその上に乗る。

| プロセス | 鍵（`ast-secrets`） | 自制レート（回/分） | 1 巡回の要求数（現況） | 巡回間隔 | 1 巡回に収まる要求数（レート × 間隔） | (b) |
| --- | --- | ---: | --- | --- | ---: | --- |
| information-collection | `finnhub-api-key` | 30（`RateLimitPerMinute` 既定） | 1（`Symbols__0=AAPL`。provider は `finnhub` のみで `finnhub-news` 無し＝1 銘柄 1 要求） | 300 秒（`appsettings.Development.json`。費用統制の Throttled で延びる） | 150 | 満たす |
| **market-monitor** | `marketdata-finnhub-api-key` | **5 → 12** | 7（監視 6 ＋ 保有 1） | 60 秒（`MonitorOptions` 既定） | **5 → 12** | **満たさない → 満たす** |
| risk-management | `marketdata-finnhub-api-key` | 5 | 1（保有 AAPL。`QuoteRefreshService` は開場に関係なく 24 時間巡回） | 60 秒（`MarketData:RefreshIntervalSeconds`） | 5 | 満たす |
| report | `marketdata-finnhub-api-key` | 5 | 報告書の作成ごとに保有銘柄の数 | 巡回しない | — | 対象外 |
| trade-decision | `marketdata-finnhub-api-key` | 5 | 判断 1 件ごとに 1（事象駆動） | 巡回しない | — | 対象外 |
| **合計 (a)** | | **50 → 57**（≤ 60） | | | | |

- **鍵は同一として数える。** 計画 ADR-0043 実測 9 は values.yaml の注記（「鍵は情報収集とは別枠」）から「別の鍵を使う」としたが、
  IADR-0275 は経路 B の `finnhub-api-key` と `marketdata-finnhub-api-key` が**バイト一致する同一鍵**であることを実測している（値は SHA-256 で比較）。
  本 IADR は保守側（同一鍵）で (a) を数える。計画側の記述との食い違いは planning へ環流する。
- 表の「1 巡回の要求数」は本 IADR の作成時点の稼働構成（監視銘柄 6・保有 1）である。

## 決定

### 決定 1: 市場監視の自制レートを 12 回/分にする（巡回間隔は 60 秒のまま）

- `values-local.yaml`（稼働リリース）と `values.yaml`（本番既定。Provider が空の間は効かない）の `market-monitor` に
  `MarketData__Finnhub__RequestsPerMinute=12` を足す。
- (b): 1 巡回に収まる要求数は 12 × 60 ÷ 60 ＝ **12**。現況 7 要求に 5 要求の余裕（監視銘柄だけなら 11 件・保有と重なる 10 件＋保有 2 件まで）。
- (a): 30 ＋ 12 ＋ 5 × 3 ＝ **57 ≤ 60**。

### 決定 2: 他のプロセスのレートと巡回間隔は変えない

- 情報収集の設定は並行作業（#1015。情報収集の Finnhub 銘柄）の領域であり、本 IADR は触らない。情報収集の銘柄が監視銘柄に追随して
  6 件になっても、300 秒の巡回に収まる要求数は 150 で (b) は崩れない（レートは変わらないので (a) も崩れない）。
- `appsettings.Development.json` の既定（市場監視 5）は変えない。コードの既定の予算（30 ＋ 5 × 4 ＝ 50）は IADR-0275 のまま正しく、
  chart の env が市場監視だけを 12 に上書きする。

### 決定 3: README に (a)(b) の確かめ方を置く

- chart README に「巡回が間隔に収まること」の節を足し、**追加の拒否が配備されるまでは、監視銘柄を増やす前に運用者が (b) を確かめる**
  （ADR-0043 決定 5 の暫定手段）と書く。

## 検討した選択肢

| 案 | (a) | (b) で収まる要求数 | 不採用の理由 |
| --- | ---: | ---: | --- |
| **市場監視 12・60 秒（採用）** | 57 | 12 | — |
| 市場監視 10・60 秒 | 55 | 10 | 「10 銘柄以上の余裕」が保有と重なると足りない（監視 10 ＋ 保有 1 ＝ 11 要求） |
| 市場監視 15・60 秒 | 60 | 15 | (a) の余裕が 0。情報収集が 1 回/分でも上げれば即座に破れる |
| 市場監視 5・120 秒 | 50 | 10 | 巡回間隔を延ばすと損切りの判定が最大 2 倍遅れる（ADR-0043 決定 2 が (b) を置いた理由そのもの） |
| 情報収集を 30 → 23 に下げて市場監視 12 | 50 | 12 | 並行作業（#1015）の領域。情報収集の実消費は小さく、下げる必要が無い |

## 結果・残余リスク

- **(a) の余裕は 3 回/分に減る**（IADR-0275 の 10 回/分から）。トークンバケットは連続補充で満杯から始まるため、Finnhub の固定ウィンドウ 1 つの中では
  最悪で自制レートの 2 倍まで送り得る（IADR-0275 と同じ性質。合計の実消費は情報収集の実消費が小さいので、上限より十分低い）。
- 監視銘柄を 12 要求を超えて増やせば (b) は再び破れる。**拒否は後続の PR（IADR-0437）が入れる**。それまでは運用者の確認だけである。
- `MarketData__Finnhub__EstimatedSymbolCount=1`（市場監視）は現況の 7 と食い違う。見積り（警告のみ）の入力であり (a)(b) の遵守には効かないため、
  本 PR では変えない（見積りの数え方は後続の PR で改める）。
- 配備: coordinator が `values-local.yaml` を反映して market-monitor を再作成する必要がある（Pod の再起動だけでは env は変わらない。#1022）。
