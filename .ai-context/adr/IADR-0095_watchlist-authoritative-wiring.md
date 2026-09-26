---
title: IADR-0095 TradeDecision の監視銘柄（watchlist）供給を権威源 MarketMonitor から s2s 同期照会に一本化し、構成ベースは fail-safe フォールバックへ降格する
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-13, UC-06, SC-02, ADR-0044, IADR-0051, IADR-0088, IADR-0090, IADR-0282, IADR-0433, IADR-0435]
author: endazon (with Claude Code)
created: 2026-07-20
updated: 2026-09-27
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
---

# IADR-0095: watchlist 供給を権威源 MarketMonitor の s2s 同期照会に一本化する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-20
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-02**（定時取引サイクル）、**FR-13**（利用者が設定を変更できる）、**UC-06**（設定変更）、**SC-02**（監視銘柄画面）。
- 対象 Issue: [#209](https://github.com/endazon/ai-stock-trading/issues/209)（実環境構築前監査 Medium・暫定 watchlist 結線の恒久化）。
- 関連する実装仕様書: [作業仕様](../specs/20260720_209_watchlist-authoritative-wiring.md)。
- 前提（develop マージ済み）:
  - [IADR-0088](IADR-0088_watchlist-settings-api.md)（watchlist の権威源は MarketMonitor の `MonitoredSymbols`・GET/POST/DELETE `/monitor/watchlist`）。
  - [IADR-0051](IADR-0051_service-to-service-auth.md)（s2s 認証・読み取り系 `sizing-context`/`open-positions` を `OwnerOrService` で trading-service に開放・呼び出し側トークン伝播）。
  - IADR-0029（TradeDecision が Risk `/risk-controls/sizing-context` を `HttpSizingContextProvider` で同期照会する precedent）。

## 背景・課題

FR-02 の定時サイクルは監視銘柄（watchlist）を巡回して判断する。実装当初（IADR-0023）は watchlist を
`TradeCycle:Watchlist` 構成から供給する **暫定実装（`ConfigurationWatchlistProvider`）** だった。その後 #191/#197 で
watchlist の権威データ源（MarketMonitor の `MonitoredSymbols`・SC-02 UI・`/monitor/watchlist` API）が完成したが、
TradeDecision は構成ファイルを読み続けており、**SC-02 で変更した監視銘柄が定時サイクルに反映されない**（二重の真実源）。
実環境構築前監査（2026-07-18）で Medium 指摘。権威源への一本化方式を決める。

## 決定

### 1. 供給方式は s2s 同期照会（イベント射影は採らない）

TradeDecision は毎サイクル、MarketMonitor の GET `/monitor/watchlist` を **s2s 同期照会**して監視銘柄を取得する。
既存の `HttpSizingContextProvider`（IADR-0029/0051）と同一の作法（`OwnerOrService` 読み取り・client_credentials トークン伝播・
BaseUrl 未設定/失敗は安全既定へ）をミラーする。

**イベント射影を採らない理由**:
- 新イベント（例: `MonitoredSymbolsChanged`）を足すと **中央監査 Consumer の追随**（`AuditConsumerCoverageTests`）と
  ローカル read model が必要になり、判断スキップ/通知経路を扱う **#210 のイベント経路と干渉**する。本 issue は取得経路に閉じる。
- s2s 同期照会は `Database per Service` を跨がず（TradeDecision は watchlist の複製を持たない）、毎サイクル最新を読むため
  「次回定時サイクルから反映」を自然に満たす。read 系 s2s の precedent（sizing-context/open-positions）が既にある。

### 2. MarketMonitor の GET `/monitor/watchlist` を `OwnerOrService` に開放する（変更系は OwnerOnly 据え置き）

RiskControlEndpoints（IADR-0051）と同型に、`/monitor` 配下を **`read` サブグループ（`OwnerOrService`）** と
**`owner` サブグループ（`OwnerOnly`）** に分ける。

- `read`（`OwnerOrService`）: GET `/watchlist`, GET `/settings`（読み取り系。trading-owner / trading-service の双方で照会可）。
- `owner`（`OwnerOnly`）: POST/DELETE `/watchlist`, GET `/watchlist/history`, PUT `/settings`（**変更は利用者のみ**・FR-13 維持。
  履歴は監査系のため owner 限定）。

認可は**サブグループに付与し親グループには付けない**（親は例外→HTTP 写像のみ・IADR-0088 の原則を踏襲）。
新規エンドポイントを増やさず既存 GET の認可を**広げる**のみ（owner は従来どおり通り、後方互換）。単一情報源を保つ。

### 3. 構成ベースは fail-safe フォールバックへ降格する（後方互換・安全側の定義）

`IWatchlistProvider` を非同期化（`GetWatchlistAsync(CancellationToken)`・`ISizingContextProvider` と同型）。供給の選択は:

- `MarketMonitor:BaseUrl` 未設定/不正 URI → **`ConfigurationWatchlistProvider`（構成ベース）** をそのまま使う
  ＝ **現行挙動・後方互換**（既定挙動を壊さない）。
- `MarketMonitor:BaseUrl` 設定時 → `HttpWatchlistProvider`。照会成功なら権威源の watchlist を返す。
  照会失敗（非 2xx・timeout・例外・不正応答）は **構成 watchlist（既定 watchlist）へ委譲**する
  ＝ 受け入れ基準 2「供給不達時のフォールバック（既定 watchlist）が fail-safe に定義される」。

「既定 watchlist（構成）」を採り「前回値キャッシュ」を採らない理由: 追加の状態（last-known-good キャッシュ）を持たず、
運用者が明示制御できる決定的な既定へ倒す方が単純で監査しやすい。空 watchlist（＝何も判断しない）も安全だが、
監査の受け入れ基準が「既定 watchlist または前回値」を求めるため、運用者定義の構成 watchlist を既定フォールバックとする。

### 4. `Shared.Contracts` は変更しない

`WatchedSymbol(Symbol, Market)`（TradeDecision）と `MonitoredSymbol(Symbol, Market)`（MarketMonitor.Domain）は同形。
s2s 境界は JSON（camelCase・列挙は数値で往復）で、`HttpWatchlistProvider` は応答を `WatchedSymbol` のリストへ直接逆直列化する。
`SizingContext`/`SizingContextView` と同じく、共有型を新設せず各サービスの型で往復する（追加のみの原則・不要な結合を避ける）。

## 影響

- MarketMonitor: `MonitorSettingsEndpoints` の認可グループ分割（GET は read へ・変更系は owner 据え置き）。ロジック不変。
- TradeDecision: `IWatchlistProvider` 非同期化、`HttpWatchlistProvider` 追加、`Program.cs` の DI 選択、`InformationCollectedConsumer` の `await` 化。
- 自己申告（IADR-0078 決定4）: `AddAiStockTradingIntrospection` に `watchlist` ポートを追加し、`MarketMonitor:BaseUrl` の有無で
  `http`（権威源へ結線）/`configuration`（構成フォールバック）を `GET /internal/introspection` から判別可能にする。
  他の外部ポート選択（sizing-context/daily-policy 等）と同じ規約。暫定残存を自己申告の面でも再発させない。
- 既定挙動: `MarketMonitor:BaseUrl` 未設定なら**不変**（構成ベース）。実環境では BaseUrl を設定して権威源に接続する。
- 監査 Consumer: **不変**（新イベント無し）。`Shared.Contracts`: **不変**。

## 追記（2026-09-27・#1050）: 本番既定の配備でも監視銘柄の権威源へ結線する

- 背景: 計画 ADR-0044「実装側の残作業」の実測 4。本 IADR の「影響」は「実環境では BaseUrl を設定して権威源に接続する」と書いたが、
  チャートの本番既定（`deploy/helm/ai-stock-trading/values.yaml`）は `MarketMonitor__BaseUrl` を空のまま持ち続け、結線していたのは
  経路B の `values-local.yaml`（IADR-0282）だけだった。同じ設定を後から足した情報収集（IADR-0435）と通知（IADR-0433）も
  本番既定は空に揃えていた。その結果、本番既定の配備では SC-02・Discord の `/policy` での監視銘柄の変更が判断サイクルと
  情報収集に届かず、ADR-0044 決定 2 の監視銘柄の節も常に「不明」になる。
- 決定 1: 本番既定で 3 つの消費側（trade-decision・information-collection・notification）の `MarketMonitor__BaseUrl` を
  `http://market-monitor-service:8080` にする。宛先は values の値からではなく、チャートの `templates/service.yaml` が描く
  `<サービスのキー>-service`・ポート 8080（同じ namespace の短名で届く）から導いた。`values-local.yaml` と同じ値になる。
- 決定 2: 資格情報は足さない。照会に要る鍵は結線の前から本番既定に在る。trade-decision・information-collection は
  `ServiceAuth__ClientId` / `__ClientSecret`（`ast-secrets` の `service-auth-client-id` / `-secret`。token エンドポイントは
  template が `global.authAuthority` から導出＝IADR-0324）。notification は、適用が OwnerOnly のため
  `Notifications__Discord__OwnerAuth__*`（`discord-owner-auth-client-id` / `-secret`。IADR-0098）。GET `/monitor/watchlist` は
  本 IADR 決定 2 のとおり `OwnerOrService` なので、どちらの主体でも読める。
- 決定 3: 固定リストの意味を values のコメントと chart README に書き分ける。取引判断の `TradeCycle:Watchlist` は
  **照会に失敗した巡回だけ**のフォールバック（本 IADR 決定 3。200 ＋空の一覧は倒さない）。情報収集の固定リストは
  **一度も読めていないときだけ**のフォールバック（IADR-0435）。issue 本文の「一度も読めていないときだけ」は情報収集の意味であり、
  取引判断には当たらない（取引判断は前回値を持たない。本 IADR 決定 3 の却下理由）。
- 本番既定で変わる挙動: 取引判断が毎巡回 `GET /monitor/watchlist` を照会する。本番既定は `TradeCycle:Watchlist` も
  market-monitor の初回シード（`Monitor:SeedSymbols`）も持たないので、監視銘柄は利用者が登録するまで空で、判断対象ゼロは
  結線前と同じである。情報収集は `Collection__Source__Provider` が空、通知は Bot が無効の間は照会しない。
  `values-local.yaml` の描画はバイト等価（3 つとも同じ値を既に持ち、Helm はリストを置換するため）。
- 検査: CI の `helm.yml` に描画の検査を足した（T-10-1650〜T-10-1652）。`MarketMonitor__BaseUrl` を持つ Deployment 全部が
  空でなく描画された Service と一致すること、各消費側の資格情報が揃うことを、既定と `values-local` の両方で見る。
- 本 IADR の「影響」の「既定挙動: `MarketMonitor:BaseUrl` 未設定なら不変」はアプリの構成既定（`appsettings`）の話として有効である。
  IADR-0282 決定 5・IADR-0435 決定 5（配備の行）の「本番既定は空」は各 PR の時点の記録であり、本追記が改める。作業仕様書 `20260927_1050_prod-market-monitor-wiring`。

## 却下した代替案

- **イベント射影**（MarketMonitor が watchlist 変更イベントを発行 → TradeDecision がローカル read model へ射影）:
  新イベント＋監査 Consumer 追随＋ローカル複製が必要で、#210 のイベント/通知経路と干渉。決定 1 の理由により却下。
- **既存 GET を OwnerOnly のまま別の internal 専用 GET を新設**: エンドポイントの二重化で単一情報源を崩す。決定 2 のとおり
  既存 GET の認可を広げる方が単純（sizing-context precedent と一致）。
- **構成ベースを完全削除**: BaseUrl 未設定時の後方互換フォールバックと照会失敗時の fail-safe を失う。決定 3 により降格・存置。
