---
title: Finnhub の監視銘柄数を分次の予算と「1 巡回が巡回間隔に収まること」で統制する（300 回/日の撤回・開場中の見積り・分次で説明できない 429）（#1030）
type: spec
status: accepted
related_ids: [FR-03, FR-01, FR-13, SC-02, ADR-0043, ADR-0031, ADR-0042, IADR-0434, IADR-0437, IADR-0275, IADR-0224, IADR-0294, IADR-0433]
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

> ADR-0043 は着手時点で宣言レンジ（`ADR-0001..0042`）の外だったため、コミット件名には入れず本文で引いた。**［2026-09-26 追記 / #1031］** レンジが `ADR-0001..0043` へ引き直されたので、PR 2 で frontmatter の `related_ids` と docs の trace ブロックに足した。

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

### PR 2（着手時に引き直した結果）

- **監視銘柄を増やす経路**（誤りの側＝「予算を見ずに監視銘柄を保存する箇所」から引く。規則 1）:
  `git grep -n "store.Save(\|MonitoredSymbols = " -- backend/Services/MarketMonitorService` →
  `MonitorWatchlistService.Add`（SC-02）・`MonitorWatchlistService.ApplyProposal`（Discord）・**`MonitorSettingsService.Replace`（全置換 `PUT /monitor/settings`。
  issue 本文に無かった 3 本目。画面は使わないが監視銘柄を置き換える）**・`MonitorSettingsService.UpdateMovementThreshold / UpdateCooldown`（監視銘柄を変えない＝除外）・
  `MonitorDefaults` のシード（構成 `Monitor:SeedSymbols`。初回だけ・運用者の構成＝除外。変更の口ではない）・`Remove`（除外は止めない）。
- **見積りの呼び出し元**: `git grep -n "CyclesPerDay\|ProvisionalDailyLimit\|WatchlistVolumeEstimator\|EvaluateDailyVolume\|EstimateDailyVolume"` →
  共有の `FinnhubDailyVolumeEstimator` / `FinnhubDailyVolumeGuardOptions` / `MarketDataSourceFactory`、市場監視・リスク管理・報告書・取引判断の `Program.cs`、
  情報収集の `InformationSourceFactory`（並行作業 #1015 の領域。**本番コードは警告文の 1 行だけ直し、試験は上限を明示する 1 行だけ直した**）、
  市場監視の `WatchlistVolumeEstimator`・入れ替え案の応答型、通知サービスの受け手 2 型と表示（`PolicyApprovalCommandHandler.Breakdown`）、
  業務メトリクス・Grafana のパネル、chart README・values（注記）。
  - 24 時間で数えるのが正しい呼び出し元（**除外**）: リスク管理の `QuoteRefreshService`（開場に関係なく巡回）・情報収集（同）・報告書と取引判断（巡回しない。保守的な仮定の間隔のまま）。
- **429 の扱い**: `git grep -n "429\|TooManyRequests\|X-Ratelimit\|IsSuccessStatusCode" -- backend/Shared backend/Services/*/Infrastructure` →
  Finnhub の quote は共有の `FinnhubQuoteClient` 1 箇所（市場監視・リスク管理・報告書・取引判断・情報収集が共有）。
  情報収集のニュース（`finnhub-news`）は別のクライアントで、稼働構成は provider に含めていない（**除外**。有効化するときに同じ判別を足す）。
- **「300 回/日」を前提にした記述**（規則 10）: `git grep -n -E "300 ?回/日|暫定上限|暫定日次|理論上限"`（`.ai-context/`・CHANGELOG を除く）→
  共有の見積り・既定値・メトリクスの説明・chart README・values の注記（情報収集と本番既定）・Grafana のパネル・情報収集の警告文・通知の表示を直した。
  `docs/blocked-tasks.md:507` は IADR-0275 の実測の記録（当時の事実）なので直さない。
- **試験の母集合**: `git grep -n "暫定上限\|FinnhubEstimateView\|ProvisionalDailyLimit\|4320" -- '*Tests*'` → 共有 3 ファイル・情報収集 1・市場監視 1・通知 2 を直した。

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

- [x] 決定 2(b)・4: SC-02 の追加（400）・全置換（400）・入れ替え案の適用（その追加だけ適用せず `skipReason`）で、(b) を破る追加を適用しない。
  保有＋監視銘柄で数える。除外は止めない（入れ替え案は除外を先に当てる）。Finnhub を使わない構成では検査しない。— T-10-1436〜T-10-1443
- [x] 決定 3: 市場監視の見積りは開場中の巡回（米国 390 分・東証 330 分 ÷ 間隔）で数え、既定では何とも比べない（`ProvisionalDailyLimit` の既定を未設定へ）。
  見積りのメトリクスは残し、上限を実測して設定したときだけ警告と比率。Discord の表示も追随。— T-10-1430・T-10-1431・T-10-1433〜T-10-1435・T-10-1444・T-10-1445・T-10-1450・T-10-1451（T-10-1432 は既存の読み取り試験の改訂）
- [x] 決定 1: 分次で説明できない 429 を EventId 4301 で区別して記録する。— T-10-1446〜T-10-1449
- [x] 試験 ID は T-10-1430..T-10-1451（1452〜1459 は欠番）。変異の実測は `docs/tests/FR-10_risk-controls-tests.md` の同名の節。

### 決定する挙動（PR 2）

IADR-0437 決定 1〜4 のとおり。要点:

| 口 | (b) を満たさない追加 | 除外 |
| --- | --- | --- |
| `POST /monitor/watchlist`（SC-02） | 400（`error` に「Finnhub の巡回に収まりません（1 巡回 N 要求（保有 h ＋ 監視銘柄 w）が、自制 r 回/分・巡回間隔 i 秒で収まる c 要求を超えます）」） | — |
| `PUT /monitor/settings`（全置換） | 今は無い銘柄を含み収まらなければ 400 | 止めない |
| `POST /monitor/watchlist/proposal-apply` | その追加だけ適用せず、内訳の `skipReason` に同じ理由 | 止めない（先に当てる） |

## 配備（coordinator）

- `values-local.yaml` を反映して market-monitor の Deployment を更新する（env の追加なので Pod の再起動だけでは入らない。#1022）。
- 反映後の確認: market-monitor の Pod の env に `MarketData__Finnhub__RequestsPerMinute=12` があること。
