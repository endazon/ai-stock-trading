---
title: IADR-0477 実市況 4 サービスの Finnhub 日次要求見積りを運用者の申告銘柄数から外し、巡回するサービス（市場監視・リスク管理）は巡回ごとに保有・監視銘柄の実数から導出し、事象ごとに引くサービス（取引判断・報告書）は見積らない
type: impl-adr
status: Accepted
related_ids: [FR-01, FR-03, FR-10, ADR-0031, ADR-0043, IADR-0294, IADR-0433, IADR-0437, IADR-0473]
author: claude (Claude Code)
created: 2026-10-02
updated: 2026-10-02
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0031 (決定 2〜4: 日次総量の見積り・同一鍵の合算)
  - planning:projects/ai-stock-trading/07_adr/ADR-0043 (決定 1: 日次上限は未実測で比べない／決定 3: 開場中の巡回で数え、見積りは 429 のときに総量を読む材料)
---

# IADR-0477: Finnhub 日次要求見積りを申告ではなく巡回の対象の実数から数える（#1132）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-02
- 決定者: Claude Code（実装）。統制の値（日次上限の既定＝未設定・分次の自制レート・送出を止めないこと）は変えない

## 起点・関連

- 関連する計画書 ID: FR-01（Finnhub の要求量）・FR-03（市場監視の巡回）・FR-10（リスク管理の現在値の補充）
- 計画 ADR: ADR-0031 決定 2〜4・ADR-0043 決定 1・3
- 起票: [#1132](https://github.com/endazon/ai-stock-trading/issues/1132)（監査 2026-10-01 F4）
- 作業仕様書: [`.ai-context/specs/20261001_1132_finnhub-request-estimate.md`](../specs/20261001_1132_finnhub-request-estimate.md)（母集合・各サービスの要求経路・窓の表）
- 前提・改める先:
  - [IADR-0294](IADR-0294_finnhub-daily-volume-estimate-and-provisional-limit-warning.md): 実市況 4 サービスの見積りを運用者申告（`MarketData:Finnhub:EstimatedSymbolCount`、既定 0）で数え、introspection にも載せる。**本 IADR が実市況 4 サービスぶんを改める**（情報収集ぶんは不変）
  - [IADR-0437](IADR-0437_finnhub-cycle-fit-control-and-daily-premise-withdrawal.md): 開場中の巡回で数える・上限は既定で比べない（不変。数え方の形を本 IADR が共有の純関数へ寄せる）
  - [IADR-0433](IADR-0433_policy-watchlist-proposal-apply-from-discord.md) 決定 4: 入れ替え案の適用時の見積り（`WatchlistVolumeEstimator`。保有＋監視銘柄の市場から）。値は不変
  - [IADR-0473](IADR-0473_quote-refresh-closed-market-and-account-log-demotion.md) 決定 3: リスク管理は米国の場中 390 分で数え、閉場ごとの 1 回は数えない（不変。渡し方を本 IADR が改める）

## 背景

| サービス | 申告（起動時） | 実測（2026-09-30 夜） |
| --- | --- | --- |
| market-monitor | 390 回/日（申告銘柄数 1 × 390 巡回） | 約 9 要求/分（≈ 3,510/場中） |
| risk-management | 1,440 回/日 | 約 3 回/分（24 時間で ≈ 4,320。#1147 の前） |
| trade-decision | 1,440 回/日 | 約 470 |
| report | 1,440 回/日 | 0 |

- 申告は `values-local.yaml` で 4 サービスとも 1 に固定されていた。実数（保有・監視銘柄）は台帳・DB の値で、IADR-0294 は「起動時には確定しない」ため申告に依った。
- 取引判断・報告書は固定間隔で巡回しないのに、`MarketData:RefreshIntervalSeconds`（60 秒）× 24 時間で数えていた（模型そのものが無い）。
- ADR-0043 決定 3 は見積りを「429 が出たときに総量を読む材料」とする。桁で外れた値はその材料にならない。

## 検討した選択肢

| 案 | 内容 | 乖離は消えるか | 費用 | 判定 |
| --- | --- | --- | --- | --- |
| A. 申告のまま名前と手順で「申告値」と明示する | メトリクス名・ログ・README に「申告」を足す | **消えない**（申告を正しく保つ運用が要り、銘柄の増減に追随しない。取引判断・報告書の模型の誤りも残る） | 小（ただし計器名の変更はダッシュボード・検査器へ波及） | 不採用 |
| B. 起動時に台帳・DB を読んで実数で数える | 起動時に保有・監視銘柄を照会 | 起動時点だけ消える（以後の増減に追随しない。起動時の照会が他サービスの起動順に依存） | 中 | 不採用 |
| **C. 巡回するサービスは巡回ごとに、その巡回の対象の実数から数える。事象ごとのサービスは見積らない** | 巡回は銘柄の集合を毎回手元に持つ。その市場から「要求数 × 場中の巡回回数」を足す | **消える**（市場監視 3,510・リスク管理 1,170 は実測／#1147 後の期待と一致） | 小（共有の純関数と記録器 1 つ。巡回 2 か所から呼ぶ） | **採用** |
| D. 全サービスの実際の送出数を数える計器を足す | 送出のたびに数える | 実測そのもの | 中（現在値ソースの生成に計器を通す変更が 4 サービスへ波及）。HTTP クライアントの計装が既に送出を数えている | 不採用（重複） |
| E. 取引判断・報告書も事象の頻度から模型を作る | 判断の頻度・報告書の生成頻度の上限で数える | 上限としてなら出せるが実測からは離れる | 中（頻度は価格変動・定時サイクル・承認の数に依る） | 不採用（過剰） |

## 決定

### 決定 1: 申告をやめ、巡回するサービスは巡回ごとに実数から数える

- 共有の純関数 `FinnhubDailyVolumeEstimator.EstimateForSymbols(symbolMarkets, pollIntervalSeconds, sessionMinutes)` ＝ Σ `RequestsPerSymbol(市場)` × `CyclesPerDay(間隔, 場中の分(市場))`。`RequestsPerSymbol` は米国 1・それ以外 0（`FinnhubMarketDataSource` は米国以外で送らない）。場中の分は呼び出し側が共有カーネルの `MarketSessions.RegularSessionMinutes` を渡す（`Shared.Infrastructure` はカーネルを参照しない）。
- 共有の記録器 `FinnhubDailyVolumeRecorder`（singleton）: `MarketDataSourceFactory.SendsToFinnhub`（Provider が finnhub で鍵がある）のときだけ記録する。メトリクスは毎回、ログは値が変わったときだけ（上限未設定＝Information、超過＝Warning、以内＝出さない）。
- 市場監視: `MonitorRoundResult.QuotedSymbolMarkets`（保有＋監視銘柄の市場。同じ銘柄でも 2 件。**開場に関係なく**）を `MonitorPollingService` が発行の後に記録器へ渡す（失敗は Warning で握り、巡回を止めない）。全市場が閉じた巡回は従来どおり評価しないので記録もしない（ゲージは最後の値）。
- リスク管理: `QuoteRefreshService.RunOnceAsync` が保有建玉の市場を記録器へ渡す（閉場中の巡回でも同じ値）。記録器は `EnableMarkToMarket=true` のとき（補充を起動するとき）だけ登録する。`QuoteRefreshService.ActiveMinutesPerDay` は呼び出し元が無くなったので撤去した（場中の分は市場ごとに渡す）。
- 市場監視の `WatchlistVolumeEstimator`（入れ替え案の適用時）も同じ純関数を使う（数え方を 1 つにする。値は不変）。

### 決定 2: 事象ごとに引くサービス（取引判断・報告書）は見積らない

固定間隔の模型が無い。是正前の 1,440 は根拠の無い値だった。両サービスの起動時の評価と `Finnhub__ProvisionalDailyLimit` の設定点（読む者が無くなる）を撤去した。実際に送った数は HTTP クライアントの計装（`http.client.request.duration` の件数。送信先 `finnhub.io`）で読む。

### 決定 3: 申告の設定点・起動時の見積り・introspection の自己申告（実市況 4 サービス）を撤去する

- `FinnhubMarketDataOptions.EstimatedSymbolCount`・`MarketDataSourceFactory.EstimateDailyVolume` / `EvaluateDailyVolume` を撤去し、Helm の 8 か所（values・values-local × 4 サービス）を消した。旧キーを構成に残しても読まれない（束縛は未知のキーを無視する）。
- 実市況 4 サービスの introspection の `finnhub-daily-request-estimate` は撤去した（introspection は起動時に値を固定する形で、実数は巡回の中にしか無い）。情報収集のものは不変。
- 計器名（`ast.finnhub.daily_request_estimate`）は変えない（意味は「プロセスの日次見積り」のまま、数え方が正しくなっただけ。名前を変えるとダッシュボード・検査器・過去の系列が切れる）。

### 窓（規則 11）

時刻の差を扱う窓は無いが、「どの銘柄を数えるか」で 3 形を比べた（表は作業仕様書）。照会した数だけで数える形は、米国が閉じて東証だけ開いた巡回で米国の銘柄を 0 にする（T-10-2016 が赤にする）。起動時だけ数える形は増減に追随しない。**巡回の対象の全銘柄を市場に関係なく数える形**を採った。

## 結果

- 2026-09-30 夜の構成での値: 市場監視 9 銘柄 × 390 ＝ 3,510（実測 ≈ 3,510/場中）、リスク管理 3 × 390 ＝ 1,170（#1147 後の期待。閉場ごとの 3 回は数えない）、取引判断・報告書は系列なし。
- 試験 T-10-2010〜T-10-2019（新規）、T-10-1434・T-10-1969 を書き直した。
- 自己変異 9 個はすべて赤（表は下）。

| 変異 | 赤になった試験 |
| --- | --- |
| 1 日の巡回回数を 24 時間で数える | T-10-2010・2012・2013・1434・2015・2016・1969・2017（入れ替え案の既存 3 件も） |
| 1 銘柄の要求数を市場に関係なく 1 にする | T-10-2010・2016・2017（既存 2 件も） |
| 市場監視が開場中の銘柄だけで数える | T-10-2016 |
| 市場監視が保有を数えない | T-10-2015・2016 |
| ログを巡回ごとに出す | T-10-2012 |
| 鍵を見ずに Provider だけで記録する | T-10-2011（2 件）・2014 |
| リスク管理が引いた銘柄だけで数える | T-10-1969 |
| 申告の設定点を戻す | T-10-2018 |
| 上限超過で警告しない | T-10-2013 |

### 残余リスク

- **見積りは同じ鍵の総量ではない。** 取引判断・報告書の要求と、リスク管理の閉場ごとの 1 回を含まない。ADR-0031 決定 4 の合算は引き続き人手（IADR-0294 決定 4）で、そのときは HTTP クライアントの計装の実数を併せて読む。Prometheus 上の系列名は collector の変換に依り、本件では実測していない。
- 市場監視は全市場が閉じている間は巡回しないため、閉場中の銘柄の増減は次の開場の最初の巡回まで見積りに出ない（見積りは開場中の量なので実害は小さい）。
- 起動の直後、最初の巡回まで系列が無い（是正前は起動時に申告値が出ていた）。
- 半日取引日・臨時休場は数えない（場中の分は通常の 390 分）。
