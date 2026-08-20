---
title: 時価評価の供給アダプタ結線（含み損益・ドローダウンの現在値連携。既定オフ）
type: spec
status: review
related_ids: [FR-10, FR-05, FR-16, FR-03, UC-06, ADR-0002, ADR-0003, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-17
updated: 2026-07-17
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 時価評価の供給アダプタ結線（既定オフ）

> Issue [#81](https://github.com/endazon/ai-stock-trading/issues/81) の残スコープ。純関数側（`PortfolioValuation.UnrealizedPnl` /
> `DrawdownRatio`・[IADR-0036](../adr/IADR-0036_unrealized-pnl-valuation.md)）は実装済みで、本作業は
> **相場データ供給アダプタと結線のみ**を担う。ドメインロジックの再設計は行わない。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制・日次損失上限・最大DD）、FR-05（発注）、FR-16（報告書の数値定義）、FR-03（市場監視）
- ユースケース（UC）: UC-06（リスク統制の操作・照会）
- ADR: ADR-0002（証券会社=moomoo）、ADR-0003（リスク統制はリスク管理サービスが強制する）、ADR-0008（段階ゲート）
- 関連 IADR: [IADR-0008](../adr/IADR-0008_daily-loss-limit-basis.md)（日次損失上限の基準）、
  [IADR-0018](../adr/IADR-0018_portfolio-ledger-projection.md)（台帳射影・含みは 0 のまま）、
  [IADR-0025](../adr/IADR-0025_pnl-aggregation.md)（報告書の評価損益は現在値入力依存）、
  [IADR-0030](../adr/IADR-0030_position-store-sync-api.md)（保有ポジションの同期照会）、
  [IADR-0036](../adr/IADR-0036_unrealized-pnl-valuation.md)（純関数コア）。本作業で新規 [IADR-0066](../adr/IADR-0066_market-valuation-supply-and-gate.md)
- 対象 Issue: #81（残スコープ＝供給アダプタ＋結線）

## 目的・背景

含み損益・ドローダウン率の**純関数コアは完成している**が、現在値の供給経路が無いため呼び出し側が常に
`currentPrices=null` / `equityHighWaterMark=null` を渡しており、`UnrealizedPnl` と `DrawdownRatio` は 0 のままである。
その結果、日次損失上限の判定は実現損益のみで含み分を取りこぼし（IADR-0008 の残タスク）、報告書の評価損益も 0 になる。

本作業は、この**供給経路（ポート＋アダプタ＋DI 結線）**を通し、実際の相場データ実装が入った時点で
リスク管理・報告書の双方へ現在値が流れる状態にする。

## 対象外（後続へ分離する）

- **実 OpenD 市況サブスクリプション**（実接続・実基盤依存）。moomoo の取引ポート `IMoomooTradeClient` は
  quote（市況）API を持たない（`PlaceOrder`/`QueryOrder`/`CancelOrder` のみ）ため、現在値を moomoo に依存させない。
  実接続と live 検証は**後続 issue＋手動 opt-in**へ分離する（CI 緑と実基盤依存テストの切り分け）。
- 市場別の取引日境界（現状どおり JST 固定オフセット。下記「タイムゾーン境界」参照）。
- 市場監視 `HttpPositionStore` の損切り価格 3% 近似（IADR-0030）。本 issue の現在値フィードとは別系統。

## 設計

### 1. 現在値ソースの seam（既定 no-op）

`IMarketDataSource`（`Shared.Contracts/Ports`・`GetLatestQuoteAsync` → `Quote?`）を現在値の唯一のポートとする。
既定実装は**常に取得不可（null）**の `NoOpMarketDataSource` とし、`Shared.Infrastructure` へ置いて全サービスから使えるようにする
（従来 `MarketMonitorService.Worker` 内の `PlaceholderMarketDataSource` を置き換える。挙動は同一＝初回 1 回だけ警告）。

### 2. 未取得時のフォールバック（受け入れ条件「0 もしくは前回値」の定義）

`QuoteCache`（共有）が `(Symbol, Market)` ごとに直近の取得値と**取得時刻**を保持し、保持期限
（`MarketData:MaxQuoteStalenessSeconds`・既定 300s）の判定も担う唯一の実装とする。

- 取得成功 → 値を保持して返す。
- 取得不可（`null`）→ 保持中の前回値が**期限以内**なら前回値、無い／期限超過なら `null`＝**その建玉の含みは 0**
  （`PortfolioValuation` の既存フォールバック）。

期限を設ける理由: 期限なしの前回値は、市況断で古い価格に基づく含み・DD を無期限に信じ込ませる（＝安全側でない）。
古すぎる値は「取得不可」に落として 0 へ倒す方が保守的である。

経過時間は `Quote.AsOf` ではなく**取得時刻（受信）**を基準とする: 取得成功パスは `AsOf` を評価せずソースの値をそのまま
信じるため、フォールバックも同じ規約に揃える（`AsOf` の妥当性は当該ソースの責務）。

同期に読み出す経路（リスク管理）は `QuoteCache` を直接読み、都度取得する経路（報告書）は `LastKnownQuoteSource`
デコレータ（内側のソース＋同じ `QuoteCache`）を用いる。期限の実装は `QuoteCache` の 1 か所に集約される。

### 3. リスク管理への結線（同期経路のため補充と読み出しを分離する）

`IPortfolioStateProvider.GetCurrent()` は**同期**であり、発注判断の同期経路
（`OrderScreeningService.Screen` → `PortfolioSnapshotBuilder.Build` → `GetCurrent`）から呼ばれる。ここで非同期の市況取得は
行えず、また**発注判断のレイテンシにネットワーク往復を持ち込むべきでない**ため、取得（非同期・背景）と読み出し
（同期・判定時）を分離する。

- 新ポート `ICurrentPriceSource`（`Application/Ports`・**同期**）: 建玉列 → `(Symbol, Market) → 現在値` の辞書。
- `QuoteRefreshService`（`Worker/Composable/MarketData`・`BackgroundService`）: `RefreshIntervalSeconds` ごとに
  保有建玉ぶんの現在値を `IMarketDataSource` から引き、取得できたものだけ `QuoteCache` へ入れる。
  1 銘柄の失敗・巡回の例外で判定を止めない（fail-safe）。
- `CachedCurrentPriceSource`（`Worker/Composable/MarketData`）: `QuoteCache` を同期に読むだけ（I/O なし）。
  期限超過・未取得は**キーを入れない**＝当該建玉の含みは 0。
- `LedgerPortfolioStateProvider`（`Application/Adapters`）がポート経由で現在値を受け、`PortfolioProjection.Project` へ
  `currentPrices` を渡し、台帳から再計算したピークで `DrawdownRatio` を差し替える。

> `IPortfolioStateProvider` の非同期化は採らない: 発注経路（`OrderScreeningService`・コンシューマ・各テスト）へ広く波及して
> 並行作業との衝突面が大きい割に、上記の分離で同じ結果が得られ、レイテンシ面ではむしろ劣るため。
>
> 実装の配置: `QuoteCache` は `Shared.Infrastructure` にあり、`*.Application` は同アセンブリを参照しない既存の層構成
> （参照は Worker＝合成ルートのみ）に従って、キャッシュに依存するアダプタは `Worker/Composable` へ置く。

### 4. エクイティピーク（DrawdownRatio の入力）

新規の純関数 `PortfolioValuation.EquityHighWaterMark(fills, initialCapital, currentEquity)` で台帳から求める。

- 約定を時系列に畳み込み、`初期資金 + 累積実現損益` の**走査最大**を採り、最後に現在エクイティとの最大を採る。
- 台帳から**再計算可能**（永続化不要・再起動後も同値・新規テーブル/マイグレーション不要）。
- 実現ベースのため含みだけで生じたピークは捉えないが、ピークを過小に見積もる＝DD を**過小**に出す側であるため、
  次項のゲート既定オフと併せて現行挙動を変えない。実観測ピークの永続化は後続（live 検証後）に判断する。

### 5. ゲート（既定オフ・fail-safe）

`MarketData:EnableMarkToMarket`（既定 **false**・リスク管理のみ）で時価評価を明示的に有効化する。

- false（既定）: 現在値ソースを**注入しない**＝`currentPrices=null` / `equityHighWaterMark=null`＝**現行と同一挙動**
  （含み 0・DD 0）。補充（`QuoteRefreshService`）も登録しない＝巡回による台帳アクセスも発生しない。
- true: 上記の供給を有効化する（それでも現在値ソースが no-op のうちは含み 0・DD 0 のまま）。

ゲートを置く理由: 時価評価の有効化は `DrawdownRatio` を史上初めて非 0 にし、**最大DD の取引ゲート**（IADR-0008）の
判定入力を変える。実相場データの live 検証を経ずに取引可否の挙動が変わることを避け、切替を人手の判断に残す。

このゲートは「全環境の既定」である base `appsettings.json` に置いてはならないため、`validate-runtime-scaffold.js` の
`FORBIDDEN_BASE_KEYS` に `MarketData` を加えて機械的に守る（IADR-0048 決定 1/2 に準拠）。設定点は各サービスの
`appsettings.Development.json`／環境変数に限る。

### 6. 報告書への結線

`ReportDraftService` は要求に `CurrentPrices` が**無いときだけ**現在値ソースから補完する（要求指定は上書きしない＝既存 API 互換）。
報告書の生成経路は非同期のため、リスク管理のような補充・読み出しの分離は要らず `IMarketDataSource` を直接引く。
引くのは**期間末に建玉が残る銘柄のみ**（全決済済みは評価に不要＝無駄な市況取得でレート制限を消費しない）。

報告書は**発注判断を行わない**（評価損益の提示のみ）ため、リスク管理のような有効化ゲートは持たない。既定の no-op
ソースが取得不可を返すことがそのまま安全既定であり、実市況ソースへの差し替えがそのまま有効化になる。

`PnlAggregator` の現在値辞書は**銘柄コードのみ**をキーとする（市場を持たない）。したがって同一銘柄コードが
複数市場に現れる場合は**曖昧としてキーを落とす**（＝評価損益 0）。誤った市場の価格で評価するより 0 に倒す（fail-safe）。

### 7. タイムゾーン境界（受け入れ条件）

単一取引日境界は現状どおり `PortfolioProjection.TradingDayOffset`（JST=+9・DST なし）を唯一の定義とし、本作業では変更しない。
市場別境界（米国市場の取引日が JST 境界と一致しない）は IADR-0018 からの既知の残課題として後続へ送る（IADR-0066 に明記）。

## 受け入れ基準（issue #81 との対応）

| # | 受け入れ条件 | 本作業での扱い |
| --- | --- | --- |
| 1 | 市場データソースから建玉の時価評価を供給 | **部分**: ポート＋アダプタ＋結線は完成。実市況実装は後続（既定 no-op） |
| 2 | `UnrealizedPnl`/`DrawdownRatio` を時価算出し日次損失上限・最大DD へ反映 | 充足（ゲート ON 時。OFF 既定では現行どおり 0） |
| 3 | 報告書の評価損益を時価入力で算出 | 充足（要求未指定時に補完） |
| 4 | 時価未取得時のフォールバック定義 | 充足（前回値＋TTL→ 超過は 0） |
| 5 | タイムゾーン境界の扱いを明確化 | 充足（JST 固定を唯一定義と明記・市場別は後続として記録） |

条件 1 が実市況実装を残すため、PR は **`Refs #81`**（`Closes` にしない）とし、残作業は [#158](https://github.com/endazon/ai-stock-trading/issues/158) へ分離した。

## テスト方針（TDD）

- `PortfolioValuation.EquityHighWaterMark`: 空列/下落後のピーク保持/現在エクイティが最大/入力順に依存しない。
- `NoOpMarketDataSource`: 常に取得不可。
- `LastKnownQuoteSource`: 成功時そのまま/失敗時に期限内の前回値/期限超過で null/前回値なしで null/
  (銘柄, 市場) ごとに分離/成功のたびに保持時刻を更新。
- `CachedCurrentPriceSource`: 建玉ぶんだけ読む/未取得はキーなし/期限超過は落とす/期限内は読む。
- `QuoteRefreshService`: 建玉ぶんだけ引いて保持/no-op ソースでは何も補充しない/建玉が無ければ引かない。
- `LedgerPortfolioStateProvider`: ソース未注入で含み 0・DD 0（現行挙動）/現在値ありで時価算出/含み損で DD/
  含み益で DD 0/取得不可で含み 0。
- `ReportDraftService`: 要求に現在値がなければ補完/あれば上書きせず引きもしない/未注入で 0/取得不可で 0/
  同一銘柄が複数市場なら落とす/曖昧でも他銘柄は供給/全決済済みは引かない。
- 実市況（実 OpenD・実 HTTP）依存テストは CI 対象外（後続の手動 opt-in）。

## 影響範囲

- `Shared.Infrastructure`: `NoOpMarketDataSource`・`QuoteCache`・`LastKnownQuoteSource`・`MarketDataOptions` 追加（新規のみ）。
  ログ抽象（`Microsoft.Extensions.Logging.Abstractions`）の参照を追加する。
- `MarketMonitorService.Worker`: `PlaceholderMarketDataSource` 削除、共有 no-op の登録へ差し替え（挙動同一）。
  監視の巡回には**前回値フォールバックをかけない**（後述）。
- `RiskManagementService`: `ICurrentPriceSource`（Application/Ports）＋`CachedCurrentPriceSource`・`QuoteRefreshService`
  （Worker/Composable/MarketData）追加、`LedgerPortfolioStateProvider` 変更、`PortfolioValuation` に純関数 1 つ追加、
  `Program.cs` の DI 追加（隣接行に限定）。`TradingDefaults`・`Shared.Contracts/Events` は触れない。
- `ReportService`: `ReportDraftService` の補完、Worker の DI 追加。
- `scripts/validate-runtime-scaffold.js`: `FORBIDDEN_BASE_KEYS` に `MarketData` を追加。
- 各 Worker の `appsettings.Development.json`: 設定点（安全既定つき）を明記。

## 損切り検知には前回値フォールバックをかけない（安全側の非対称）

前回値フォールバックは**発注を伴わない時価評価**（リスク管理の判定入力・報告書の提示）にのみ適用し、市場監視の巡回には
適用しない。市場監視は損切りライン到達で `StopLossTriggered`＝**実際の決済発注**を引き起こすため、市況断のあいだ古い価格で
判定すると、既に回復した価格に対して誤発火しうる。取得不可はスキップ（＝発注しない）が安全側であり、現行挙動でもある。

同じ「古い価格」でも、評価（表示・ゲート入力）では前回値の方が 0 より実態に近く、発注では前回値が新たな誤動作を生む。
非対称なのは意図的である。
