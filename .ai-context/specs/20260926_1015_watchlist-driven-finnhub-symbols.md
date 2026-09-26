---
title: 情報収集の Finnhub の対象銘柄を市場監視の監視銘柄から導き、不明は空と区別し、1 巡回を巡回間隔に収める（#1015）
type: spec
status: accepted
related_ids: [FR-01, FR-02, FR-13, UC-01, UC-06, SC-02, ADR-0043, ADR-0031, ADR-0020, IADR-0435, IADR-0434, IADR-0095, IADR-0275, IADR-0294, IADR-0420, IADR-0064]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0043_finnhub-daily-premise-withdrawn-and-cycle-fit-control.md (決定 2 (a)(b)・決定 3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0031_finnhub-rate-limit-minute-confirmed-daily-open.md (決定 2・4)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-01・FR-02・FR-13)
---

# 仕様書: 情報収集の Finnhub の対象銘柄を監視銘柄から導く（#1015）

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-01（情報収集）、FR-02（定時の取引サイクル）、FR-13（監視銘柄の変更。SC-02・`/policy` の変更が収集にも届くこと）
- ユースケース（UC）: UC-01（定時サイクル）、UC-06（設定の変更）
- 画面（SC）: SC-02（監視銘柄）
- 関連 ADR: **ADR-0043**（planning#667 の利用者裁定 2026-09-26。決定 2 (a) 同一鍵の分次予算・(b) 1 巡回が巡回間隔に収まること・決定 3）、
  ADR-0031 決定 2・4（決定 3 の暫定手段は ADR-0043 が撤回）、ADR-0020（情報源の区分）
- 関連 IADR: IADR-0435（本件）、IADR-0095（取引判断の監視銘柄の s2s 照会。同じ口を使う）、IADR-0275（分次の実測）、IADR-0294（日次の見積り）、
  IADR-0420（越境の読み取り契約）、IADR-0064（情報源の自制）、IADR-0434（同じ ADR-0043 の市場監視側。同一鍵の予算表は情報収集を 30 回/分と数える）

> 着手時は計画 ADR-0043 が本リポの宣言レンジ（`ADR-0001..0042`）の外だった。#1031 でレンジが `ADR-0001..0043` へ引き直されたため、
> rebase 後に `related_ids`・trace ブロック・コミット件名へ ADR-0043 を置いた。

## 背景（#1015 の観測）

監視銘柄を AAPL から 6 件へ増やしても、情報収集の Finnhub の対象は配備の固定値 `Collection__Source__Finnhub__Symbols__0=AAPL` のままで、
新しい銘柄は判断材料（現在値・ニュース）が無いまま一次スクリーニングで見送られた。取引判断の定時サイクルは市場監視の
`GET /monitor/watchlist` を照会して判断対象を決めている（IADR-0095）ため、**判断対象と収集対象がずれる。**

## 受け入れ基準

1. 市場監視の結線（`MarketMonitor:BaseUrl`）があれば、情報収集は**毎巡回の最初に** `GET /monitor/watchlist` を s2s（`trading-service`）で照会し、
   Finnhub の対象銘柄を**監視銘柄のうち米国市場の銘柄**にする（Finnhub Free が扱うのは米国株。市場監視の Finnhub 実装も米国以外を飛ばす）。
   並び順は監視銘柄の順（先に監視していた銘柄が先）。重複は最初の 1 件を残す。
2. **原則 A（不明は空ではない）**:
   - 照会に成功して監視銘柄が 0 件（または米国の銘柄が 0 件）なら、Finnhub では何も収集しない（それが事実）。
   - 照会できない（非 2xx・タイムアウト・例外・応答が読めない・項目の欠けた行がある）なら、**直前に読めた対象を使い続け、警告する。**
   - 直前に読めた対象が無ければ（起動直後から読めない）、**構成の固定リスト（`Collection:Source:Finnhub:Symbols`）へ倒し、警告する。**
3. **ADR-0043 決定 2 (b)**: 1 巡回の Finnhub の要求数（銘柄数 × 1 銘柄あたりの要求数〔`finnhub`・`finnhub-news` の有効数〕）÷ 自制レート
   （`Collection:Source:Finnhub:RateLimitPerMinute`）が巡回間隔（`Collection:PollIntervalSeconds`）を超えるなら、**収まる数だけを並び順の先頭から**
   収集し、後回しにした銘柄を警告ログとメトリクスで見せる。自制レートは超えない。
4. **自制レートを 1 つにする**: 同じ鍵で叩く `finnhub`（現在値）と `finnhub-news`（企業ニュース）は **1 つのトークンバケットを共有する**
   （従来はソースごとに別のバケットで、両方有効なら同じ鍵へ自制値の 2 倍を送り得た。ADR-0043 決定 2 (a) の合計に数えるのは 1 プロセス 1 レート）。
5. 市場監視の結線が無ければ（既定）、従来どおり構成の固定リストを使う（(b) の上限は同じく掛かる）。
6. 固定リストは結線時にはフォールバックだけになることを values（本番既定・経路 B）と helm README に書く。経路 B（values-local）は
   `MarketMonitor__BaseUrl` を結線する。
7. 越境の読み取りは IADR-0420 の規約に従い、送り手の本物の型（`MonitoredSymbol`）を送り手の JSON 設定で直列化した応答で契約テストを持つ。

## 設計

- `IWatchlistReader`（Features）: `ReadAsync` → 監視銘柄の一覧、または **null（不明）**。実装 `HttpMarketMonitorWatchlistReader`（ExternalServices）。
  応答の行の型は `Symbol`・`Market` とも nullable で受け、欠けた行・値域外の市場が 1 つでもあれば一覧ごと不明（既定値へ黙って倒さない）。
- `FinnhubCycleFit.MaxSymbolsPerCycle(rate, intervalSeconds, requestsPerSymbol)`（Domain・純関数）: `floor(rate × interval / (60 × requestsPerSymbol))`。
- `FinnhubSymbolSelector`（Features・singleton）: `IFinnhubSymbolSet.Current` を Finnhub の 2 ソースへ供給し、`RefreshAsync` が毎巡回で
  照会 → 出所（監視銘柄／直前の値／構成へのフォールバック）の決定 → (b) の上限の適用 → `Current` の差し替えを行う。直前の値は照会に成功したときだけ更新する。
- `WatchlistFollowingSourceFetcher`（ExternalServices）: `ISourceFetcher` の装飾。取得の前に `RefreshAsync` を 1 回呼ぶ（1 巡回 1 照会・2 ソースが同じ集合を見る）。
  市場監視の結線があり、かつ Finnhub 系のソースが有効なときだけ挟む。
- 業務メトリクス: `ast.information_collection.finnhub_symbol_set_resolutions`（Counter・`outcome`=watchlist / last-known / configured-fallback）と
  `ast.information_collection.finnhub_symbols_deferred`（Gauge・後回しにした銘柄数。平常 0）。ダッシュボードにパネルを 1 枚足す。

## 母集合（規則 1〜6・9・10）

着手時に引き直した（issue 本文の「やること」は母集合として使っていない）。

| 軸 | 引き方 | 結果と扱い |
| --- | --- | --- |
| 固定リストを前提にする箇所（誤りの側） | `git grep -n -i "Finnhub:Symbols\|Finnhub__Symbols\|Finnhub\.Symbols\|FINNHUB_SYMBOLS" -- . ':!CHANGELOG.md' ':!.ai-context/specs'` | `InformationSourceFactory.cs`（有効化の条件・ソースへの受け渡し・見積り）→ 改める。`values.yaml:334`・`values-local.yaml:158`・helm `README.md:170` → 改める。`InformationSourceSelectionTests`・`InformationSourceFactoryTests` → 既存の挙動（未結線）の試験なので残す。`docker-compose.yml:212`・`.env.example:61` → **除外**（compose は取引判断も `MarketMonitor__BaseUrl` を結線しておらず未結線＝固定リストの挙動のまま。`.env.example` は秘密ガードで読めず、値の枠だけ）。`IADR-0068`・`IADR-0294`・`.ai-context/adr/README.md` → **除外**（凍結記録。IADR-0294 の「情報収集は実配列長を使う」は本 IADR が改めると IADR-0435 に書く） |
| 「watchlist と揃える」の手合わせ | `git grep -n "watchlist と揃える\|収集銘柄"` | `values-local.yaml:158`（改める）・`values-local.yaml:177` SEC EDGAR の CIK（**除外**: CIK は銘柄コードから導けず本件の射程外。コメントの「AAPL と揃える」は固定のまま正しい） |
| 監視銘柄の読み口（あり得る形の列挙） | `git grep -n "monitor/watchlist" -- backend` | 送り手 `GetWatchlist/Endpoint.cs`（`read`＝OwnerOrService）。受け手は取引判断 `HttpWatchlistProvider`・通知 `HttpMarketMonitorWatchlistController`・BFF。**新しい端点は作らない**（既存の読み口を使う） |
| 越境の契約（IADR-0420） | `CrossServiceReadContractTests` の判定 | 新しい受け手の単位 `InformationCollectionService/HttpMarketMonitorWatchlistReader.ReadAsync -> MarketMonitorService /monitor/watchlist` が生じる → 契約テストを足す。送り手側の `ReadContractWireFormatTests`（T-10-931）は既存 |
| Finnhub の自制レートの生成点 | `git grep -n "Limiter(options.Finnhub" -- backend` | `InformationSourceFactory.CreateSingle` の 2 箇所 → 1 つを共有する |
| 日次の見積り（静的な銘柄数） | `git grep -n "Finnhub.Symbols.Length" -- backend` | `EstimateDailyVolume` / `EvaluateDailyVolumeEstimate` / `LogFinnhubQuota` → **除外**（見積りの数え方は ADR-0043 決定 3 で改まり、後続の PR〔IADR-0434 が予告〕で改める。結線時に起動時の見積りが固定リストの数で数える旨を helm README に書く） |
| 取引判断のプロンプトの銘柄一覧（META の誤読） | `git grep -n "policy.Summary\|Watchlist" -- backend/Services/TradeDecisionService/Features` | プロンプトへ渡るのは確定済み日報の方針の本文（`DailyPolicy.Summary`）と判断対象の 1 銘柄だけで、**固定・古い銘柄一覧は渡していない**。誤読は自由文の方針の読み違いであり、構造化して渡す改善（#1015 やること 3 → #1034）と値動きの材料（やること 2 → #1035）は別 issue へ切り出した |
| 自分の変更で新たに誤りになる記述（規則 10） | 上の各軸を変更後に引き直す | helm README の「情報収集は実銘柄数から厳密に算出」→ 結線時は固定リストの数である旨へ改める。`CollectionSourceOptions` のコメント（`Symbols`）→ フォールバックである旨を足す |

## テスト（T-10-1460〜T-10-1479 を予約）

| ID | 内容 |
| --- | --- |
| T-10-1460 | (b) の上限の純関数（境界: ちょうど収まる／1 つ超える／要求数 2／下限） |
| T-10-1461 | 照会に成功 → 米国の銘柄だけ・監視銘柄の順・重複除去 |
| T-10-1462 | 照会に成功して 0 件 → 空（構成へ倒さない） |
| T-10-1463 | 照会に失敗・直前の値あり → 直前の値を使い続け警告・メトリクス last-known |
| T-10-1464 | 照会に失敗・直前の値なし → 構成へ倒し警告・メトリクス configured-fallback |
| T-10-1465 | 上限超え → 先頭から収まる数だけ・後回しの数を警告とメトリクスで |
| T-10-1466 | 未結線 → 構成（上限つき）・照会しない |
| T-10-1467 | 受け手のアダプタ: 非 2xx・例外・タイムアウト・null・欠けた行・値域外の市場 → 不明（null） |
| T-10-1468 | 越境の契約: 送り手の本物の `MonitoredSymbol` を web 既定で直列化した応答を読める |
| T-10-1469 | 装飾: 取得の前に 1 回だけ更新し、Finnhub のソースはその集合を取る |
| T-10-1470 | `finnhub` と `finnhub-news` が 1 つのバケットを共有する |
| T-10-1471 | 本番の組み立て: 結線時は装飾が挟まり、固定リストが空でも Finnhub ソースが有効／未結線は従来どおり |
| T-10-1472 | 照会は `trading-service` のトークン付き・パス `/monitor/watchlist` |
