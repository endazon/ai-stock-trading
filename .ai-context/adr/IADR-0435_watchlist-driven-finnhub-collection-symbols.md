---
title: IADR-0435 情報収集の Finnhub の対象銘柄は市場監視の監視銘柄（米国の銘柄）から巡回ごとに決め、読めなければ直前の値・構成の固定リストへ倒し（不明を空と扱わない）、1 巡回を巡回間隔に収める
type: impl-adr
status: Accepted
related_ids: [FR-01, FR-02, FR-13, UC-01, UC-06, SC-02, ADR-0043, ADR-0031, ADR-0020, IADR-0434, IADR-0095, IADR-0275, IADR-0294, IADR-0420, IADR-0064, IADR-0068]
author: claude (Claude Code)
created: 2026-09-26
updated: 2026-09-26
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0043_finnhub-daily-premise-withdrawn-and-cycle-fit-control.md (決定 2 (a)(b)・決定 3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0031_finnhub-rate-limit-minute-confirmed-daily-open.md (決定 2・4)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-01・FR-02・FR-13)
---

# IADR-0435: 情報収集の Finnhub の対象銘柄を監視銘柄から決める

- 状態: Accepted
- 日付: 2026-09-26
- 決定者: claude（起票 [#1015](https://github.com/endazon/ai-stock-trading/issues/1015)。計画 ADR-0043〔planning#667 の利用者裁定 2026-09-26〕を前提にする。
  利用者レビューは PR で受ける）

## 起点・関連

- 対象 Issue: #1015
- 関連する実装仕様書: [20260926_1015_watchlist-driven-finnhub-symbols](../specs/20260926_1015_watchlist-driven-finnhub-symbols.md)
- 関連 IADR: [IADR-0095](IADR-0095_watchlist-authoritative-wiring.md)（取引判断の監視銘柄の s2s 照会。同じ口・同じ作法）、
  [IADR-0275](IADR-0275_finnhub-effective-rate-limit-measurement.md)（分次の実測・自制レートの既定）、
  [IADR-0294](IADR-0294_finnhub-daily-volume-estimate-and-provisional-limit-warning.md)（日次の見積り。決定2 の「情報収集は実配列長を厳密に使う」を本 IADR が結線時について改める）、
  [IADR-0420](IADR-0420_cross-service-read-contract-convention-and-guard.md)（越境の読み取り契約）、IADR-0064（情報源の自制）、IADR-0068（Finnhub の共有クライアント）、
  [IADR-0434](IADR-0434_finnhub-cycle-fit-budget-market-monitor-rate.md)（同じ計画 ADR-0043 の市場監視側。同一鍵の予算表は情報収集を 30 回/分と数える。
  本 IADR 決定 4 で、`finnhub` と `finnhub-news` を両方有効にしてもこの 30 を超えない）

## コンテキスト

#1015: 監視銘柄を AAPL から 6 件（AAPL・MSFT・NVDA・AMZN・GOOGL・META）へ増やしても、情報収集の Finnhub の対象は配備の固定値
`Collection__Source__Finnhub__Symbols__0=AAPL` のままだった。取引判断の定時サイクルは市場監視の `GET /monitor/watchlist` から判断対象を
決める（IADR-0095）ので、新しい 5 銘柄は判断材料（現在値・ニュース）が無いまま一次スクリーニングで見送られた。

追随を妨げていたのは Finnhub の日次の前提値（300 回/日）の扱いで、planning#667 で裁定待ちだった。計画 ADR-0043 は前提値を撤回し、
監視銘柄数を (a) 同一鍵の自制レートの合計 ≤ 60 回/分、(b) プロセスごとに「1 巡回の要求数 ÷ 自制レート ≤ 巡回間隔」で統制すると決めた。

## 決定

### 決定 1: 対象は市場監視の監視銘柄のうち米国の銘柄とし、巡回の最初に 1 回だけ読む

- `MarketMonitor:BaseUrl` があれば、巡回ごとに `GET /monitor/watchlist`（既存の読み口・OwnerOrService）を `trading-service` のトークンで照会する
  （名前付き `HttpClient` "monitor"・タイムアウト 5 秒。取引判断と同じ作法。**新しい端点は作らない**）。
- 対象は **米国市場の銘柄**を**監視銘柄の順**で並べたもの（重複は最初の 1 件・前後空白を除く）。Finnhub Free は米国株を扱い、市場監視の
  Finnhub 実装も米国以外を飛ばす。保有銘柄は数えない —— 定時サイクルが判断するのは監視銘柄であり（`InformationCollectedHandler`）、
  計画 ADR-0043 決定2 (b) の「保有銘柄と監視銘柄を合わせて数える」は監視サービスの巡回の数え方である。
- 照会は `ISourceFetcher` の装飾（`WatchlistFollowingSourceFetcher`）が取得の前に 1 回だけ行い、現在値・企業ニュースの 2 ソースは同じ集合
  （`IFinnhubSymbolSet`）を見る。市場監視の結線があり、Finnhub 系のソースが有効なときだけ挟む。

### 決定 2: 原則 A —— 不明は空ではない

| 読んだ結果 | 対象 | 記録 |
| --- | --- | --- |
| 読めた（0 件を含む） | その監視銘柄の米国の銘柄（0 件なら Finnhub では何も収集しない＝事実としての空） | `outcome=watchlist` |
| 読めない・直前に読めた集合あり | 直前に読めた集合 | 警告・`outcome=last-known` |
| 読めない・一度も読めていない | 構成の固定リスト（`Collection:Source:Finnhub:Symbols`） | 警告・`outcome=configured-fallback` |

- 「読めない」は非 2xx・タイムアウト・例外・読めない本文に加え、**項目の欠けた行・値域外の市場が 1 つでもある応答**である
  （行の型は nullable で受ける。既定値〔市場 0＝日本〕で読むと米国の銘柄が黙って外れ、しかも「読めた」ことになる）。
- 直前の値は**読めたときだけ**更新する。

### 決定 3: 1 巡回を巡回間隔に収める（計画 ADR-0043 決定2 (b)）

- 収まる銘柄数 ＝ `floor(RateLimitPerMinute × PollIntervalSeconds ÷ (60 × 1 銘柄あたりの要求数))`（`FinnhubCycleFit`）。1 銘柄あたりの要求数は
  Provider に列挙された `finnhub`・`finnhub-news` の数。
- 超えたら**並びの先頭から**収まる数だけを採り、後回しにした銘柄を警告ログに名指しし、`ast.information_collection.finnhub_symbols_deferred` に
  数を記録する（0 も記録する＝回復が見える）。並びは監視銘柄の順なので、後から足した銘柄が後回しになる（監視サービスが収まらない追加を拒否する
  計画 ADR-0043 決定4 と同じ向き）。出所を問わず（固定リストにも）掛ける。
- 巡回間隔は構成の基準値を使う（費用統制の間隔延長は巡回を長くするだけなので、基準値で収まれば延長時も収まる）。

### 決定 4: 現在値と企業ニュースは 1 つのバケットを共有する（計画 ADR-0043 決定2 (a)・ADR-0031 決定 4）

従来は `finnhub` と `finnhub-news` がソースごとに `RateLimitPerMinute` のバケットを作っており、両方を有効にすると同じ鍵へ自制値の 2 倍を送り得た
（既定 30 回/分 × 2 ＝ 60 回/分）。**1 プロセスの自制レートを 1 つに定める**ため、ファクトリが 1 つだけ作って両者へ渡す。決定 3 の「自制レート」は
この 1 つの値である。

### 決定 5: 見える化

- 業務メトリクス 2 つ（`ast.information_collection.finnhub_symbol_set_resolutions`〔Counter・`outcome`〕／`…finnhub_symbols_deferred`〔Gauge〕）と
  業務ダッシュボードのパネル 1 枚。introspection に `watchlist` ポート（`http` / `configuration`。取引判断と同じ語彙）。
- 配備: 本番既定 `values.yaml` に `MarketMonitor__BaseUrl=""`（未結線＝従来どおり）、経路 B `values-local.yaml` に
  `http://market-monitor-service:8080`。固定リストはフォールバック専用と両方に書く。

## 検討した選択肢

- **監視銘柄の変更イベントを購読する**: 却下。市場監視は監視銘柄の変更をイベントとして発行しておらず、足すと送り手の変更（計画外）になる。
  既存の読み口の照会で足り、取引判断も同じ照会で判断対象を決めている（判断対象と収集対象が同じ時点の監視銘柄になる）。
- **読めなければ空（収集しない）**: 却下（原則 A）。#1015 の症状（材料なしの見送り）を黙って全銘柄へ広げる。
- **読めなければ毎回構成の固定リスト**: 却下。一度読めた後の一時的な不達で、収集対象が古い固定値へ巻き戻る（#1015 の状態へ戻る）。
- **収まらない分を切り捨てず巡回を伸ばす**: 却下（計画 ADR-0043 決定2 (b)）。
- **収まらない分を巡回ごとに輪番で回す**: 却下。決定的な並びでないと、どの銘柄が材料なしで判断されるかが巡回ごとに変わって追えない。
  後回しの銘柄は警告とメトリクスで名指しし、運用者が自制レートか巡回間隔を見直す（計画 ADR-0043 決定5 の暫定手段と同じ）。
- **起動時の日次見積り（IADR-0294）を実際の対象数で数え直す**: 本件では行わない。見積りの数え方は計画 ADR-0043 決定3 で改まり、後続の PR
  （IADR-0434 が予告）で改める。結線時の起動時の見積りは固定リストの数で数えている旨を helm README に書いた。

## 結果

- 良い影響: SC-02・`/policy` で監視銘柄を変えると、次の巡回から情報収集の対象も変わる。監視銘柄を読めないときも収集は止まらず、
  止まっていないことと追随していないことがメトリクスで見える。1 巡回が巡回間隔を超えない。
- 残余リスク:
  - 🔴 **本 IADR が変えるのは Finnhub の現在値・企業ニュースの対象だけである。** 経路 B は `finnhub-news` を有効にしていない（ニュースは
    未構成）。SEC EDGAR の CIK・Google News のクエリは銘柄コードから導けず、固定のままである。
  - #1015 の「やること 2」（定時の判断に各銘柄の直近の値動きを材料として渡すか）と「やること 3」（方針の銘柄一覧を構造化してプロンプトへ渡す。
    META の誤読）は本件の射程外。取引判断のプロンプトへ渡るのは確定済み日報の方針の本文と判断対象の 1 銘柄だけで、固定・古い銘柄一覧は渡していない
    （誤読は自由文の方針の読み違い）。#1034（誤読）・#1035（値動きの材料）へ切り出した。
  - 市場監視が監視銘柄を返す順は設定の保存順である。順が変わる変更が入れば、収まらないときに後回しになる銘柄も変わる。
  - 結線時の起動時の日次見積りは固定リストの数で数える（上記）。
