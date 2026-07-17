---
title: 時価評価の供給アダプタ結線（含み損益・ドローダウンの現在値連携。既定オフ）
type: spec
status: review
related_ids: [FR-10, FR-05, FR-16, FR-03, UC-06, ADR-0002, ADR-0007, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-17
updated: 2026-07-17
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0002_broker-selection.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
---

# 仕様書: 時価評価の供給アダプタ結線（既定オフ）

> Issue [#81](https://github.com/endazon/ai-stock-trading/issues/81) の残スコープ。純関数側（`PortfolioValuation.UnrealizedPnl` /
> `DrawdownRatio`・[IADR-0036](../adr/IADR-0036_unrealized-pnl-valuation.md)）は実装済みで、本作業は
> **相場データ供給アダプタと結線のみ**を担う。ドメインロジックの再設計は行わない。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制・日次損失上限・最大DD）、FR-05（発注）、FR-16（報告書の数値定義）、FR-03（市場監視）
- ユースケース（UC）: UC-06（リスク統制の操作・照会）
- ADR: ADR-0002（証券会社=moomoo）、ADR-0007（取引ガード・信用）、ADR-0008（段階ゲート）
- 関連 IADR: [IADR-0008](../adr/IADR-0008_daily-loss-limit-basis.md)（日次損失上限の基準）、
  [IADR-0018](../adr/IADR-0018_portfolio-ledger-projection.md)（台帳射影・含みは 0 のまま）、
  [IADR-0025](../adr/IADR-0025_pnl-aggregation.md)（報告書の評価損益は現在値入力依存）、
  [IADR-0030](../adr/IADR-0030_position-store-sync-api.md)（保有ポジションの同期照会）、
  [IADR-0036](../adr/IADR-0036_unrealized-pnl-valuation.md)（純関数コア）。本作業で新規 [IADR-0065](../adr/IADR-0065_market-valuation-supply-and-gate.md)
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

`LastKnownQuoteSource` デコレータで**前回値フォールバック**を定義する。

- 内側のソースが `Quote` を返す → そのまま返し、`(Symbol, Market)` ごとに最後の値として保持する。
- 内側が `null`（取得失敗）→ 保持中の前回値が **TTL 以内**なら前回値を返す（`MarketData:MaxQuoteStaleness`・既定 5 分）。
- 前回値が無い、または TTL 超過 → `null` を返す＝**その建玉の含みは 0**（`PortfolioValuation` の既存フォールバック）。

TTL を設ける理由: 期限なしの前回値は、市況断で古い価格に基づく含み・DD を無期限に信じ込ませる（＝安全側でない）。
古すぎる値は「取得不可」に落として 0 へ倒す方が保守的である。

### 3. リスク管理への結線

- 新ポート `ICurrentPriceSource`（`RiskManagement.Application/Ports`）: 建玉列 → `(Symbol, Market) → 現在値` の辞書。
- 実装 `MarketDataCurrentPriceSource`（`Application/Adapters`）: 建玉ごとに `IMarketDataSource` を引き、取得できたものだけを辞書へ入れる
  （取得不可は**キーを入れない**＝当該建玉の含みは 0）。1 銘柄の失敗で全体を落とさない。
- `LedgerPortfolioStateProvider` が `PortfolioProjection.Project` へ `currentPrices` と `equityHighWaterMark` を渡す。

### 4. エクイティピーク（DrawdownRatio の入力）

新規の純関数 `PortfolioValuation.EquityHighWaterMark(fills, initialCapital, currentEquity)` で台帳から求める。

- 約定を時系列に畳み込み、`初期資金 + 累積実現損益` の**走査最大**を採り、最後に現在エクイティとの最大を採る。
- 台帳から**再計算可能**（永続化不要・再起動後も同値・新規テーブル/マイグレーション不要）。
- 実現ベースのため含みだけで生じたピークは捉えないが、ピークを過小に見積もる＝DD を**過小**に出す側であるため、
  次項のゲート既定オフと併せて現行挙動を変えない。実観測ピークの永続化は後続（live 検証後）に判断する。

### 5. ゲート（既定オフ・fail-safe）

`RiskOptions:EnableMarkToMarket`（既定 **false**）で時価評価を明示的に有効化する。

- false（既定）: `currentPrices=null` / `equityHighWaterMark=null` を渡す＝**現行と同一挙動**（含み 0・DD 0）。
- true: 上記の供給を有効化する。

ゲートを置く理由: 時価評価の有効化は `DrawdownRatio` を史上初めて非 0 にし、**最大DD の取引ゲート**（IADR-0008）の
判定入力を変える。実相場データの live 検証を経ずに取引可否の挙動が変わることを避け、切替を人手の判断に残す。

### 6. 報告書への結線

`ReportDraftService` は要求に `CurrentPrices` が**無いときだけ**現在値ソースから補完する（要求指定は上書きしない＝既存 API 互換）。

`PnlAggregator` の現在値辞書は**銘柄コードのみ**をキーとする（市場を持たない）。したがって同一銘柄コードが
複数市場に現れる場合は**曖昧としてキーを落とす**（＝評価損益 0）。誤った市場の価格で評価するより 0 に倒す（fail-safe）。

### 7. タイムゾーン境界（受け入れ条件）

単一取引日境界は現状どおり `PortfolioProjection.TradingDayOffset`（JST=+9・DST なし）を唯一の定義とし、本作業では変更しない。
市場別境界（米国市場の取引日が JST 境界と一致しない）は IADR-0018 からの既知の残課題として後続へ送る（IADR-0065 に明記）。

## 受け入れ基準（issue #81 との対応）

| # | 受け入れ条件 | 本作業での扱い |
| --- | --- | --- |
| 1 | 市場データソースから建玉の時価評価を供給 | **部分**: ポート＋アダプタ＋結線は完成。実市況実装は後続（既定 no-op） |
| 2 | `UnrealizedPnl`/`DrawdownRatio` を時価算出し日次損失上限・最大DD へ反映 | 充足（ゲート ON 時。OFF 既定では現行どおり 0） |
| 3 | 報告書の評価損益を時価入力で算出 | 充足（要求未指定時に補完） |
| 4 | 時価未取得時のフォールバック定義 | 充足（前回値＋TTL→ 超過は 0） |
| 5 | タイムゾーン境界の扱いを明確化 | 充足（JST 固定を唯一定義と明記・市場別は後続として記録） |

条件 1 が実市況実装を残すため、PR は **`Refs #81`**（`Closes` にしない）とし、残作業を後続 issue に起票する。

## テスト方針（TDD）

- `PortfolioValuation.EquityHighWaterMark`: 単調増加/下落後のピーク保持/空列/現在エクイティが最大、の純関数テスト。
- `LastKnownQuoteSource`: 成功時そのまま/失敗時に前回値/TTL 超過で null/前回値なしで null。
- `MarketDataCurrentPriceSource`: 取得できた銘柄だけが辞書に入る/全失敗で空辞書。
- `LedgerPortfolioStateProvider`: ゲート OFF で含み 0・DD 0（現行挙動）/ON かつ現在値ありで時価算出。
- `ReportDraftService`: 要求に現在値がなければ補完/あれば上書きしない/同一銘柄が複数市場なら落とす。
- 実市況（実 OpenD・実 HTTP）依存テストは CI 対象外（後続の手動 opt-in）。

## 影響範囲

- `Shared.Infrastructure`: `NoOpMarketDataSource`・`LastKnownQuoteSource` 追加（新規のみ）。
- `MarketMonitorService.Worker`: `PlaceholderMarketDataSource` 削除、共有 no-op の登録へ差し替え（挙動同一）。
- `RiskManagementService`: `ICurrentPriceSource`＋`MarketDataCurrentPriceSource` 追加、`LedgerPortfolioStateProvider` 変更、
  `PortfolioValuation` に純関数 1 つ追加、`Program.cs` の DI 追加（隣接行に限定）。`TradingDefaults`・`Shared.Contracts/Events` は触れない。
- `ReportService`: `ReportDraftService` の補完、Worker の DI 追加。
</content>
</invoke>
