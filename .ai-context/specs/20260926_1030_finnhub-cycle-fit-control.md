---
title: Finnhub の監視銘柄数を分次の予算と「1 巡回が巡回間隔に収まること」で統制する（300 回/日の撤回・開場中の見積り・分次で説明できない 429）（#1030）
type: spec
status: accepted
related_ids: [FR-03, FR-01, FR-13, SC-02, ADR-0031, ADR-0042, IADR-0434, IADR-0275, IADR-0224, IADR-0294, IADR-0433]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0043_finnhub-daily-premise-withdrawn-and-cycle-fit-control.md (決定 1〜5)
  - planning:projects/ai-stock-trading/07_adr/ADR-0031_finnhub-rate-limit-minute-confirmed-daily-open.md (決定 2〜4)
  - planning:projects/ai-stock-trading/07_adr/ADR-0042_discord-apply-ai-watchlist-proposal-and-revision-limit.md (決定 1)
---

# 仕様書: Finnhub の監視銘柄数を分次の予算と「1 巡回が巡回間隔に収まること」で統制する（#1030）

## 起点となる計画書（トレーサビリティ）

- 関連 ADR: **ADR-0043**（決定 1: 300 回/日の撤回と 429 の見張り／決定 2: (a)(b)／決定 3: 開場中の巡回で数える／決定 4: 追加の拒否／決定 5: 暫定手段）、
  ADR-0031 決定 3（暫定手段を ADR-0043 が部分改定）・決定 4（同一鍵の合計）、ADR-0042 決定 1（条件の 1 つを ADR-0043 が部分改定）
- 機能要求: FR-03（監視銘柄の価格変動の監視）、FR-13（監視銘柄の変更）、FR-01（情報収集。見積りの共有部品）
- 画面: SC-02（監視銘柄の追加）
- 関連 IADR: IADR-0434（PR 1）、IADR-0437（PR 2・予定）、IADR-0275・IADR-0224（前提）、IADR-0294（見積りを改める）、IADR-0433（Discord の適用）

> ADR-0043 は着手時点で宣言レンジ（`ADR-0001..0042`）の外。frontmatter の `related_ids` とコミット件名には入れず、`plan_refs`・本文・コミット本文で引く。

## 分割（2 PR）

| PR | 範囲 | リスク |
| --- | --- | --- |
| **PR 1（至急）** | 構成と文書だけ。稼働リリースで (a)(b) を満たす（IADR-0434） | 低（env 1 本） |
| PR 2 | 決定 1・3・4 のコード（追加の拒否・見積りの数え方・429 の判別）と試験 | 中 |

## 母集合（規則 1〜6・9・10）

### PR 1: 同一鍵を使うプロセスと自制レート

- 引き方 1（設定名）: `git grep -n -i "RequestsPerMinute\|PollIntervalSeconds"`（テストと `.ai-context/` を除く）→ 市場監視・報告書・リスク管理・取引判断の
  `appsettings.Development.json`（`MarketData:Finnhub:RequestsPerMinute=5`）、`MarketDataOptions`（既定 5）、`MonitorOptions`（60）、`CollectionOptions`（1800）、
  情報収集の `appsettings.Development.json`（`PollIntervalSeconds=300`）、バックテスト（Stooq・moomoo。Finnhub ではない）、為替（BOJ・FRED。Finnhub ではない）。
- 引き方 2（別名。規則 2）: `git grep -n "PerMinute\|TokenBucket("` → 情報収集は `RateLimitPerMinute`（Finnhub 30・GoogleNews 1・EDINET 1・BOJ 1・FRED 60・FINRA 5）。
  Finnhub の鍵を使うのは `Collection:Source:Finnhub`（30）だけ。
- 引き方 3（鍵の側から。規則 5）: `git grep -n "finnhub-api-key\|marketdata-finnhub-api-key" -- deploy` → values.yaml・values-local.yaml の
  information-collection（`finnhub-api-key`）と market-monitor・risk-management・report・trade-decision（`marketdata-finnhub-api-key`）。他に無い。
- 引き方 4（上書きの側から。規則 4）: `git grep -n "RequestsPerMinute\|RateLimitPerMinute\|PollInterval\|RefreshInterval" -- deploy` → **helm の上書きは 0 件**（本 PR の前）。
  `ASPNETCORE_ENVIRONMENT=Development`（`templates/deployment.yaml`）なので `appsettings.Development.json` が効く。
- 除外: バックテストの `BarData`（Stooq／moomoo）と為替の `Fx`（BOJ／FRED）は Finnhub の鍵を使わない。

### PR 1: 「50 回/分」の記述（規則 10。この変更で誤りになる自分の記述）

- `git grep -n -E "50 ?/ ?分|5 ?× ?4|合計 ?50"` → IADR-0275 本文と索引（凍結・コードの既定の記述として正しい）、4 サービスの `appsettings.Development.json` の注記
  （コードの既定の記述として正しい）、`MarketDataOptions` の注記（同）、`docs/blocked-tasks.md`（IADR-0275 の是正の記録。正しい）、
  **chart README の秘密鍵の表（「既定のレート予算」を chart の値と読める）→ 直した**。

### PR 2（予定。着手時に引き直す）

- 追加の経路: `git grep -n "MonitorWatchlistService\|WatchlistProposalPlan"`（SC-02 の `Add`・`ApplyProposal`）。
- 見積りの呼び出し元: `git grep -n "CyclesPerDay\|ProvisionalDailyLimit\|WatchlistVolumeEstimator"`。
- 429 の扱い: `git grep -n "429\|TooManyRequests\|X-Ratelimit"`。

## 予算表（PR 1。IADR-0434 と同じ）

| プロセス | 自制レート（回/分） | 1 巡回の要求数（現況） | 巡回間隔 | 収まる要求数 | (b) |
| --- | ---: | --- | --- | ---: | --- |
| information-collection | 30 | 1（AAPL） | 300 秒 | 150 | 満たす |
| **market-monitor** | **5 → 12** | 7（監視 6 ＋ 保有 1） | 60 秒 | **5 → 12** | **満たさない → 満たす** |
| risk-management | 5 | 1（保有） | 60 秒 | 5 | 満たす |
| report | 5 | 報告書ごと | 巡回しない | — | 対象外 |
| trade-decision | 5 | 判断ごとに 1 | 巡回しない | — | 対象外 |
| **合計 (a)** | **50 → 57 ≤ 60** | | | | |

## 受け入れ基準

### PR 1

- [x] values-local.yaml の market-monitor に `MarketData__Finnhub__RequestsPerMinute=12`（values.yaml にも同値。helm の「values-local が本番の env を落とさない」検査を満たす）。
- [x] (a) 57 ≤ 60、(b) 7 ≤ 12。予算表を IADR-0434 と本書に置く。
- [x] chart README に (a)(b) の確かめ方と暫定手段（運用者が確かめる）を置く。
- [x] `helm template -f values-local.yaml` の描画で market-monitor に env が出ることを確かめる（CI の Helm ジョブ）。

### PR 2（IADR-0437）

- [ ] 決定 2(b)・4: SC-02 の追加と入れ替え案の適用で、(b) を破る追加を適用しない。除外は止めない。適用しなかった追加は内訳に理由。
- [ ] 決定 3: 市場監視の見積りは開場中の巡回（米国 390 分 ÷ 間隔）で数え、300 回/日と比べない。警告とメトリクスは残す。
- [ ] 決定 1: 分次で説明できない 429 を区別してログする。
- [ ] 試験 ID は T-10-1430..T-10-1459。変異を掛けて落ちることを確かめる。

## 配備（coordinator）

- `values-local.yaml` を反映して market-monitor の Deployment を更新する（env の追加なので Pod の再起動だけでは入らない。#1022）。
- 反映後の確認: market-monitor の Pod の env に `MarketData__Finnhub__RequestsPerMinute=12` があること。
