---
title: 取引判断への現在値（価格文脈）供給とサイジング権威価格アンカリング（既定オフ）
type: spec
status: review
related_ids: [FR-02, FR-04, FR-10, FR-16, UC-01, UC-02, ADR-0003, ADR-0004]
author: endazon (with Claude Code)
created: 2026-07-24
updated: 2026-07-24
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
---

# 仕様書: 取引判断への現在値供給とサイジング権威価格アンカリング（既定オフ）

> Issue [#158](https://github.com/endazon/ai-stock-trading/issues/158)（実市況実装）／親 [#81](https://github.com/endazon/ai-stock-trading/issues/81)
> の残スコープ。**判断ロジックの拡張**であって実弾化ではない。実弾 triple-latch（`Broker__Provider=paper` /
> `Broker:Moomoo:TrdEnv=simulate` / 起動時 real 拒否・[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)）
> には一切触れない。目的は **SIMULATE(paper) で実際に売買が成立する dogfood 経路を通すこと**。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-02（取引サイクル・判断トリガー）、FR-04（AI 判断）、FR-10（リスク統制）、FR-16（報告書の数値定義）
- ユースケース（UC）: UC-01（定時サイクル）、UC-02（価格変動サイクル）
- ADR: ADR-0003（AI 判断は確定日報の方針・リスク制約の範囲内）、ADR-0004（情報源＝案A+／現在値ソース）
- 関連 IADR: [IADR-0068](../adr/IADR-0068_live-quote-feed-finnhub-extraction.md)（実市況＝Finnhub・共有 `IMarketDataSource`・既定 no-op）、
  [IADR-0066](../adr/IADR-0066_market-valuation-supply-and-gate.md)（現在値供給・前回値フォールバック・鮮度期限）、
  [IADR-0023](../adr/IADR-0023_trading-cycle-scheduling-and-merge.md)（定時・価格変動の合流と `DecisionTrigger`）、
  [IADR-0017](../adr/IADR-0017_trade-decision-structure.md)（判断コア・安全既定＝取引しない）、
  [IADR-0035](../adr/IADR-0035_stop-loss-authoritative.md)（損切り価格の権威化）、
  [IADR-0076](../adr/IADR-0076_trade-decision-profitability-gate.md)（採算ゲート・参照価格由来の notional）。本作業で新規
  [IADR-0099](../adr/IADR-0099_current-price-context-for-decision.md)
- 対象 Issue: #158（残スコープ＝判断への現在値供給）／#81（受け入れ条件 1 の消費者拡張）

## 目的・背景

定時取引サイクルは `InformationCollected` を購読し、監視銘柄を巡回して `DecisionTrigger.Scheduled(symbol, market)` で
判断する（`InformationCollectedConsumer`）。この定時トリガーは **`Price=null`** を作る。その結果:

1. `TradeDecisionPromptBuilder.Build` は `Kind==Scheduled` のとき「定時サイクル（価格変動トリガーなし）」節を出し、
   **銘柄・市場のみで現在値を一切載せない**。LLM は ADR-0003 に従い「方針の範囲外・不確実な場合は必ず Hold」であり、
   価格文脈が無いため Buy/Sell の根拠を持てず常に Hold に倒れる → 発注意図が生成されない → 約定・含み損益が起きない。
2. `TradeDecisionService` のサイジング・OrderIntent・損切りの参照価格は `decision.ReferencePrice`（LLM が出力した数値）で
   あり、**権威ある市場価格ではない**。仮に Buy が出ても、サイジングとリスク統制（1取引%・損切り幅）が LLM の
   幻覚価格に対して計算される。

本作業は、`#158`（IADR-0068）で共有物へ抽出済みの現在値ソース `IMarketDataSource` を**判断サービスへ供給**し、
(1) 判断プロンプトに現在値を載せて LLM が判断できるようにし、(2) サイジング・OrderIntent・損切り・採算の参照価格を
**権威ある現在値へアンカリング**する。既定は現在値ソース no-op のため**現行挙動をバイト等価に維持**する。

## 対象外（後続へ分離する）

- **実 Finnhub API・実基盤依存の live 検証**（実接続・レート枠 429 の確認）。CI 対象外の手動 opt-in（IADR-0049）。
- **`MarketData:EnableMarkToMarket`（リスク管理の時価評価ゲート）**。本作業は判断サービスのみに現在値を供給し、
  リスク管理の含み・DD 評価（IADR-0066）は変更しない。
- **日本株の現在値**（Finnhub 無料枠は US のみ・IADR-0068 決定5）。取得不可のまま。
- 実 LLM のモデル選定・プロンプトの多数決/二段（IADR-0039）は既存のまま。プロンプト**文言の追記**のみを行う。

## 設計

### 1. 現在値供給の Application ポート（既定 no-op・fail-safe）

新ポート `ICurrentPriceProvider`（`Application/Ports`）を判断文脈の現在値の唯一の供給口とする。RAG 取得
（`IRetrievalContextProvider`・IADR-0072）・採算見積り（`IProfitabilityAssumptionsProvider`・IADR-0076）と同じ
「ポート／アダプタ」分離を採り、Application/Domain を `Shared.Infrastructure` の DTO へ直結させない。

```csharp
public interface ICurrentPriceProvider
{
    // 現在値ソースが実結線されているか（有効化されているか）。既定 no-op は false。
    bool IsEnabled { get; }
    // トリガー銘柄の現在値。取得不可・鮮度切れ・無効化時は null。
    Task<decimal?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken cancellationToken = default);
}
```

- 既定実装 `NoOpCurrentPriceProvider`（`Application/Adapters`）: `IsEnabled=false`・常に `null`（＝現在値なし＝現行動作）。
- Worker アダプタ `MarketDataCurrentPriceProvider`（`Worker/Composable/Adapters`）: 共有 `IMarketDataSource`（IADR-0068）を
  包み、`Quote` を取得して**鮮度**（`AsOf` と `IClock.UtcNow` の差が `MarketData:MaxQuoteStalenessSeconds` 以内）を検査。
  取得成功かつ鮮度内のときだけ `Quote.Price` を返す。取得不可（null）・鮮度切れ・例外は `null` へ縮退（fail-safe）。
  `IsEnabled` は現在値ソースが実結線（`MarketData:Provider` が既知の live provider）のときのみ true。

### 2. 判断サービスの現在値供給と権威価格アンカリング

`TradeDecisionService.DecideAsync` に以下を組み込む（既定 no-op のため現行挙動は不変）。

1. **現在値取得（fail-safe）**: 日報方針あり確認後、`ICurrentPriceProvider` で現在値を引く。取得の例外は握って
   `null` に縮退する（キャンセルは伝播）。RAG 取得の `RetrieveContextSafeAsync` と同じラッパ規約。
2. **fail-safe ゲート（有効化時のみ）**: `IsEnabled=true`（現在値ソースが結線されている）かつ現在値が `null`
   （取得不可・鮮度切れ）のとき → **見送り（発注抑止・null 返却）**。「有効化したのに現在値が取れない」ときに
   古い/無い価格で発注しないための安全側。**`IsEnabled=false`（既定）のときはこのゲートを一切適用せず現行挙動を保つ**。
3. **プロンプト供給**: `TradeDecisionPromptBuilder.Build` に `currentPrice`（`decimal?`）を渡す。定時（Scheduled）節に
   現在値が非 null のとき「現在値」行を追記する。価格変動（PriceMovement）節は既に `trigger.Price` を出しているため
   変更しない（現在値供給の有無で PriceMovement 経路のプロンプト文言を変えない）。**`currentPrice=null`（既定）なら
   定時節も追記せず現行と完全一致**。
4. **権威価格アンカリング**: LLM 判断後、発注に用いる参照価格を決める。
   `var referencePrice = currentPrice ?? decision.ReferencePrice;`
   - 現在値ありのとき: 権威ある現在値をサイジング・OrderIntent・損切り価格・採算 notional の参照価格に用いる。
     LLM の `ReferencePrice`（幻覚しうる値）ではなく実市場価格で 1取引% と損切り幅を効かせる。
   - 現在値なし（既定）のとき: 従来どおり `decision.ReferencePrice`（現行挙動）。
   - アンカリング後の再検証（IADR-0035 の不変量を権威価格に対して担保）: `referencePrice<=0` または
     `StopLossDistancePerShare<=0` または `StopLossDistancePerShare>=referencePrice` なら**見送り**。既定
     （`referencePrice==decision.ReferencePrice`）では Parser が既に保証済みのため挙動不変。

損切り価格（IADR-0035）・採算 notional（IADR-0076）はいずれも `referencePrice` から導出する（従来 `decision.ReferencePrice`
だった箇所を `referencePrice` に置換）。既定では両者一致のため現行挙動を保つ。

### 3. 有効化（opt-in）と経路B結線

- 判断サービス（`TradeDecisionService.Worker`）で `MarketData:Provider=finnhub` ＋ `MarketData:Finnhub:ApiKey` が
  設定されたとき、`MarketDataSourceFactory`（IADR-0068）が `FinnhubMarketDataSource` を返し、
  `MarketDataCurrentPriceProvider` の `IsEnabled=true`・実現在値を供給する。未設定・不備は no-op（`IsEnabled=false`）。
- `MaxQuoteStalenessSeconds`（既定 300s・IADR-0066）を鮮度判定に流用する（現在値を使う全サービスで共通概念）。
- `MarketData` は base `appsettings.json` に置かない（IADR-0048・`validate-runtime-scaffold.js` の
  `FORBIDDEN_BASE_KEYS` に既登録済み）。設定点は `appsettings.Development.json`／環境変数／経路B(values-local/overlay)。

## fail-safe と安全既定の対応

| 状態 | 現在値 | 挙動 |
| --- | --- | --- |
| 既定（`Provider` 未設定・no-op・`IsEnabled=false`） | 常に null | 価格ゲート適用せず**現行挙動**（プロンプト不変・参照価格＝LLM 値）。 |
| 有効化（`IsEnabled=true`）＋取得成功＋鮮度内 | 実価格 | プロンプトに現在値注入・参照価格を権威価格へアンカリング。 |
| 有効化＋取得不可/鮮度切れ/例外 | null | **見送り（発注抑止・Hold）**。古い/無い価格で発注しない。 |

## 受け入れ基準（issue #158 / #81 との対応）

| # | 受け入れ条件 | 本作業での扱い |
| --- | --- | --- |
| 1 | 定時サイクルの判断に現在値が供給され、paper で売買が成立し得る | 充足（有効化時。プロンプトに現在値注入・LLM が Buy/Sell 可能に） |
| 2 | サイジング・リスク統制が権威ある現在値に整合 | 充足（参照価格アンカリング・1取引%/損切り幅を実価格に対して適用） |
| 3 | 価格未取得・鮮度切れは安全側（Hold・発注抑止） | 充足（有効化時の fail-safe ゲート・鮮度期限） |
| 4 | 既定オフで現行挙動をバイト等価に維持 | 充足（no-op 既定・プロンプト不変・参照価格＝LLM 値・既存テスト不変） |
| 5 | 実弾 triple-latch 不変 | 充足（Broker/TrdEnv に一切触れない） |

実 Finnhub の live 検証（実接続・429 確認）を残すため、PR は **`Refs #158`**（`Closes` にしない）。#81 も併記する。

## テスト方針（TDD）

- `NoOpCurrentPriceProvider`: `IsEnabled=false`・常に null。
- `MarketDataCurrentPriceProvider`: 取得成功かつ鮮度内で価格返却／鮮度切れで null／取得不可(null)で null／
  例外で null（fail-safe）／`IsEnabled` は provider 結線に一致。
- `TradeDecisionPromptBuilder`: 定時トリガー＋現在値ありで「現在値」行を含む／現在値なしで含まない（現行）／
  価格変動トリガーは現在値供給の有無でプロンプト不変。
- `TradeDecisionService`:
  - 既定（現在値 provider 未指定）で定時・価格変動とも現行挙動（プロンプト不変・参照価格＝LLM 値）。
  - 現在値ありでプロンプトに現在値注入・サイジング/損切り/OrderIntent 参照価格が権威価格にアンカリング。
  - `IsEnabled=true` かつ現在値 null で見送り（fail-safe ゲート）。
  - `IsEnabled=false`（既定）で現在値 null でも見送りゲートを適用しない（現行の Buy 継続）。
  - 現在値取得の例外で判断を止めず縮退（`IsEnabled=false` は継続／`IsEnabled=true` は見送り）。
  - アンカリング後に損切り幅≥権威価格なら見送り。
  - 採算ゲート有効時の notional が権威価格由来。
- 既存 `TradeDecisionServiceTests`・`DecisionOrchestratorTests`・SIMULATE 統合系（情報収集→判断）は不変で緑。

## 影響範囲

- `TradeDecisionService.Application`: `ICurrentPriceProvider`（Ports）・`NoOpCurrentPriceProvider`（Adapters）追加。
  `TradeDecisionService.DecideAsync` の現在値取得・fail-safe ゲート・アンカリング（隣接行）。
  `TradeDecisionPromptBuilder.Build` に `currentPrice` 引数追加（既定 null＝現行）。
- `TradeDecisionService.Worker`: `MarketDataCurrentPriceProvider`（Composable/Adapters）追加、`Program.cs` の DI
  （共有 `MarketDataSourceFactory`・`MarketDataOptions` バインド・provider 登録・introspection ポート自己申告）。
- `Shared.Contracts` / `Shared.Contracts/Events` / `TradingDefaults` / 実弾 triple-latch は**触れない**。
- `TradeDecisionService.Worker` の `appsettings.Development.json`: `MarketData` の設定点（安全既定つき・空既定）を明記。
- 新イベント・スキーマ変更なし（`event-schemas.baseline` 再生成不要）。
