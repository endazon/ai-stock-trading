---
title: 実市況（現在値）フィードの供給（Finnhub 抽出・API キーで opt-in・既定 no-op）
type: spec
status: review
related_ids: [FR-10, FR-01, FR-03, FR-16, UC-06, ADR-0004, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-17
updated: 2026-07-17
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 仕様書: 実市況（現在値）フィードの供給（Finnhub・既定 no-op）

> Issue [#158](https://github.com/endazon/ai-stock-trading/issues/158)（[#81](https://github.com/endazon/ai-stock-trading/issues/81) から分離された残作業）。
> 供給経路（ポート `IMarketDataSource` ＋既定 no-op ＋ DI 結線＋前回値フォールバック＋ゲート）は
> [IADR-0066](../adr/IADR-0066_market-valuation-supply-and-gate.md)（PR #159）で develop へマージ済み。
> **本作業はその唯一の空欄＝実市況の実装のみ**を埋める。供給経路・純関数コアの再設計は行わない。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制・日次損失上限・最大DD）、FR-01（情報収集＝Finnhub 既存コネクタの抽出元）、
  FR-03（市場監視）、FR-16（報告書の数値定義）
- ユースケース（UC）: UC-06（リスク統制の操作・照会）
- ADR: ADR-0004（情報源＝案A+）、ADR-0008（段階ゲート）
- 関連 IADR: [IADR-0066](../adr/IADR-0066_market-valuation-supply-and-gate.md)（本作業の前提＝供給経路とゲート）、
  [IADR-0064](../adr/IADR-0064_official-source-connectors.md)（公式ソースコネクタ＝ソース単位レート制限の元）、
  [IADR-0036](../adr/IADR-0036_unrealized-pnl-valuation.md)（純関数コア）、
  [IADR-0049](../adr/IADR-0049_integration-e2e-foundation.md)（CI と実基盤依存テストの切り分け）、
  [IADR-0048](../adr/IADR-0048_runtime-scaffold.md)（base appsettings の挙動中立）。
  本作業で新規 [IADR-0068](../adr/IADR-0068_live-quote-feed-finnhub-extraction.md)
- 対象 Issue: #158（実市況実装）／親 #81（受け入れ条件 1）

## 目的・背景

`IMarketDataSource` の実装は現在 `NoOpMarketDataSource`（常に取得不可）のみで、**実市況ソースが 1 つも無い**。
そのため既定でも構成変更後でも含み損益・DD は 0 のままであり、#81 の受け入れ条件 1（実市場データ源からの供給）が
未充足のまま `Refs` に留まっている。

実市況の候補は IADR-0066 決定 1 が 2 つ挙げている。本作業は **Finnhub `/quote`** を採る（IADR-0068 決定 1）。
既に `FinnhubInformationSource`（#9・IADR-0064）が同じ API を叩いており、無料枠・API キーで opt-in できるため、
実接続を伴う新規実装を最小化できる（OpenD 市況は実 OpenD 接続＝IADR-0060 のゲート下で、切り分けが重い）。

## スコープ

- Finnhub `/quote` の呼び出しを共有物（`Shared.Infrastructure`）へ抽出し、`IMarketDataSource` の実装を与える。
- 既存 `FinnhubInformationSource`（FR-01）は抽出後の共有クライアントを使う形へ寄せる（**出力は不変**）。
- リスク管理・市場監視・報告書の現在値供給を、構成で実フィードへ差し替えられるようにする（既定は no-op のまま）。
- 設定サーフェス（compose / helm / appsettings.Development / `.env.example`）を空既定で開ける。

## 対象外（後続へ分離する）

- **OpenD 市況サブスクリプション**（moomoo 本線・実 OpenD 接続・IADR-0060 のゲート下）。本作業では採らない（IADR-0068 決定 1）。
- **`MarketData:EnableMarkToMarket=true` への切替**。有効化は最大DD の取引ゲート（IADR-0008）の判定入力を変えるため、
  手動 opt-in の live 検証を経た**人手の判断**に残す（IADR-0066 決定 4）。本 PR は既定を変えない。
- **手動 opt-in の live 検証そのもの**（実 Finnhub API・CI 対象外・IADR-0049 の切り分け）。手順のみ本仕様書に定義する。
- 実観測ピークの永続化要否（IADR-0066 決定 3 のトレードオフ）。live 検証後の判断事項として #158 に残す。
- 米国株以外（日本株）の現在値。Finnhub 無料枠は US のみ（下記「市場の限界」）。
- 市場別の取引日境界（JST 固定・IADR-0018 からの既知の残課題）、`HttpPositionStore` の損切り価格 3% 近似（IADR-0030）。

## 設計

### 1. Finnhub 呼び出しの抽出（共有クライアント＋2 つの薄い写像）

現状の `FinnhubInformationSource`（`InformationCollection.Worker` 内・`internal`）は「HTTP 呼び出し＋応答解析」と
「`RawInformationItem` への写像」を 1 クラスで担っている。**前者だけ**を共有物へ抽出する。

| 層 | 置き場所 | 責務 |
| --- | --- | --- |
| `FinnhubQuoteClient` | `Shared.Infrastructure/Composable/Adapters/MarketData/` | `/quote` を 1 銘柄ぶん呼び、`FinnhubQuoteSnapshot`（`Current`/`High`/`Low`/`PreviousClose`/`AsOf`）へ解析する。レート制限の自制・失敗時の警告ログ・取得不可（null）はここ |
| `FinnhubMarketDataSource : IMarketDataSource` | 同上 | スナップショット → `Quote`（`Price=Current`）へ写像（#158 の本体） |
| `FinnhubInformationSource : IInformationSource` | `InformationCollection.Worker`（現状のまま） | スナップショット → `RawInformationItem`（`high`/`low`/`prevClose` を含む現行の文字列）へ写像（FR-01・**出力不変**） |

`IMarketDataSource` を実装するため**新しい契約は増やさない**。`Quote` は `Price` しか持たないため、
情報収集側を `IMarketDataSource` 経由に付け替えると `high`/`low`/`prevClose` が落ちる（FR-01 の収集内容の劣化）。
これを避けるため、抽出の境界を「Quote への写像」ではなく「HTTP＋解析」に置く。

### 2. レート制限の共有物への移動

抽出した共有クライアントも、要求前の自制（IADR-0064）を要する。しかしレート制限一式は情報収集サービス内にあり、
共有物から参照できない（`Shared.*` → サービスの依存は方向違反）。よって以下を
`Shared.Infrastructure/Composable/RateLimiting/` へ移す（**ロジックは不変**）。

| 移動するもの | 移動元 | 備考 |
| --- | --- | --- |
| `TokenBucket` | `InformationCollection.Domain/RateLimiting/` | 純粋な状態機械。変更なし |
| `IRateLimiter` | `InformationCollection.Application/Ports/` | 実装者は `DelayingRateLimiter` のみ・利用者は Worker のみ |
| `DelayingRateLimiter` | `InformationCollection.Worker/Composable/RateLimiting/` | `internal` → `public`。時刻源を `IClock` から `TimeProvider` へ（`Shared.Infrastructure` の既存慣行＝`PaperBrokerAdapter`・`QuoteCache` 周辺と同じ） |

情報収集の `IClock` は**残す**（`EdinetInformationSource` の日付計算等で使う）。レート制限だけが `TimeProvider` になる。

### 3. 実フィードの選択（既定 no-op・API キーで opt-in）

`MarketDataSourceFactory`（`Shared.Infrastructure`）が構成 `MarketData:Provider` から実装を選ぶ。
`InformationSourceFactory`（IADR-0022/0064）と同じ fail-safe の形にする。

- `none`（既定・未設定・空）→ `NoOpMarketDataSource`
- `finnhub` ＋ `MarketData:Finnhub:ApiKey` あり → `FinnhubMarketDataSource`
- `finnhub` ＋ **キー無し** → 警告ログを出して `NoOpMarketDataSource`（起動は失敗させない）
- 未知の provider → 警告ログを出して `NoOpMarketDataSource`

### 4. 市場の限界（US のみ）

Finnhub 無料枠の `/quote` は米国株のみ。`FinnhubMarketDataSource` は `Market.UnitedStates` 以外を
**取得不可（null）として即返す**（要求を出さない＝レート枠も消費しない）。銘柄ごとに毎回警告すると巡回のたびに
ログが溢れるため、市場ごとに 1 回だけ警告する。日本株の現在値は依然として取得不可＝含み 0 に倒れる。

### 5. 結線（3 サービス・既定の挙動は不変）

| サービス | 現状 | 本作業後 |
| --- | --- | --- |
| リスク管理 | `IMarketDataSource` → `NoOpMarketDataSource` | → `MarketDataSourceFactory`（`QuoteRefreshService` の補充元。`EnableMarkToMarket` のゲートは不変） |
| 報告書 | `LastKnownQuoteSource(NoOpMarketDataSource, ...)` | → `LastKnownQuoteSource(factory の実装, ...)`（デコレータ構造は不変） |
| 市場監視 | `IMarketDataSource` → `NoOpMarketDataSource` | → `MarketDataSourceFactory`（前回値フォールバックは引き続き**かけない**・IADR-0066 決定 2） |

`Provider` 未設定＝既定では 3 サービスとも `NoOpMarketDataSource` が注入され、**現行と完全に同一**（実接続なし・含み 0・DD 0）。

### 6. レート制限の重複（#158 のチェック項目）

実フィードが入ると、同じ Finnhub の枠を 4 プロセスが独立に消費する（情報収集の巡回・リスク管理の補充・
市場監視の巡回・報告書のドラフト生成時）。プロセスをまたぐ協調は無いため、**合計が無料枠 60 回/分を超えないよう
サービスごとの予算を構成で配る**（`MarketData:Finnhub:RequestsPerMinute`・既定 10）。根拠と再評価条件は IADR-0068 決定 4。

## 設定サーフェス（空既定・PR 末尾の単一コミット）

| キー | 既定 | 置き場所 |
| --- | --- | --- |
| `MarketData:Provider` | `none`（＝実接続しない） | 環境変数 `MarketData__Provider` / appsettings.Development.json |
| `MarketData:Finnhub:ApiKey` | 空（＝no-op へフォールバック） | 環境変数 `MARKETDATA_FINNHUB_API_KEY` 経由。**appsettings には実値を置かない** |
| `MarketData:Finnhub:BaseUrl` | `https://finnhub.io/api/v1` | 環境変数（既定で足りるため通常は設定不要。テスト・将来の移行用） |
| `MarketData:Finnhub:RequestsPerMinute` | `10` | 環境変数 |
| `MarketData:EnableMarkToMarket` | `false`（**本作業でも変えない**） | 既存（IADR-0066） |

- `MarketData` は `validate-runtime-scaffold.js` の `FORBIDDEN_BASE_KEYS` にあるため、base `appsettings.json` には置かない（IADR-0048）。
- `MARKETDATA_FINNHUB_API_KEY` を同スクリプトの `SECRET_ENV_KEYS` へ足し、`.env.example` で空既定を機械的に守る。

## 受け入れ基準（テストへの写像）

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | 構成 `Provider=finnhub` ＋キーありで、`/quote` の応答から `Quote`（現在値）を返す | `FinnhubMarketDataSourceTests`（スタブ `HttpMessageHandler`） |
| 2 | API キーはヘッダー（`X-Finnhub-Token`）で送る（URL クエリに入れない＝OTel へ漏らさない） | 同上（要求 URL とヘッダーを検証） |
| 3 | 非成功応答・解析不能・現在値 0（＝Finnhub の未知銘柄応答）は取得不可（null） | 同上 |
| 4 | 米国以外の市場は要求を出さずに null | 同上（ハンドラ呼び出し回数 0） |
| 5 | `Provider` 未設定/`none`/未知/キー無しは `NoOpMarketDataSource`（実接続しない） | `MarketDataSourceFactoryTests` |
| 6 | 要求前にレート制限を消費する（自制・IADR-0064） | `FinnhubQuoteClientTests`（偽の待機） |
| 7 | 情報収集の `FinnhubInformationSource` の出力が抽出前と同一（`high`/`low`/`prevClose` を保つ） | 既存 `FinnhubInformationSourceTests`（無改修で緑） |
| 8 | 移動したレート制限（`TokenBucket`/`DelayingRateLimiter`）の挙動が不変 | 既存テストを `Shared.Infrastructure.Tests` へ移設し無改修で緑 |
| 9 | 既定（構成なし）で 3 サービスとも no-op が注入され、実接続しない | `MarketDataWiringTests`（既存・リスク管理）＋ 各 Worker の DI 解決テスト |
| 10 | `.env.example` / compose / helm / appsettings.Development に空既定の設定点がある | `validate-runtime-scaffold.js`（CI） |

**実 Finnhub API を叩くテストは書かない**（CI 緑と実基盤依存の切り分け・IADR-0049）。HTTP はすべてスタブする。

## 手動 opt-in の live 検証（CI 対象外・本 PR のマージ後に人手で実施）

1. Finnhub の無料 API キーを取得し `.env` の `MARKETDATA_FINNHUB_API_KEY` に置く（`.env.example` は空のまま）。
2. `MARKETDATA_PROVIDER=finnhub` とし、`MARKETDATA_SYMBOLS` 相当は不要（保有建玉・監視銘柄から自動で決まる）。
3. `docker compose up` でリスク管理・市場監視・報告書を起動し、米国株の建玉がある状態でログを確認する
   （`NoOpMarketDataSource` の警告が**出ない**こと・取得失敗の警告が出ないこと）。
4. 報告書のドラフトで評価損益が非 0 になることを確認する（`EnableMarkToMarket` に依存しない）。
5. レート制限の実測（4 プロセス合計が 60 回/分以内・429 が出ない）を確認する。
6. 以上を経てから、**人手で** `MARKETDATA_ENABLEMARKTOMARKET=true` を切り替え、最大DD ゲートの判定入力が
   変わることを承知のうえでリスク管理を再起動する（#158 の残チェック項目）。

## 完了の定義（DoD）との対応

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` が緑・`dotnet format` 差分なし。
- 既定の挙動が変わらないこと（実接続なし・含み 0・DD 0）を DI 解決テストで示す。
- 設計判断を [IADR-0068](../adr/IADR-0068_live-quote-feed-finnhub-extraction.md) に残す。
