---
title: 通貨単位の一貫化（基準通貨 JPY への換算・統制上限の実効化）
type: spec
status: In progress
related_ids: [FR-10, FR-17, FR-04, FR-05, ADR-0003, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-27
updated: 2026-07-27
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0004_datasource-selection.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 仕様書: 通貨単位の一貫化（基準通貨 JPY への換算）

> Issue [#257](https://github.com/endazon/ai-stock-trading/issues/257)。フェーズ2 検証（ローカル SIMULATE）で、
> **外貨建て（USD）の現在値を円建ての統制上限へそのまま突き合わせている**ことが実測で判明した。
> リスク上限の実効値が意図から乖離し、**過大発注を招く向き**（安全側と逆）に約 150 倍緩む。
>
> **実弾には一切触れない。** 実弾 triple-latch（`Broker__Provider=paper` / `Broker:Moomoo:TrdEnv=simulate` /
> 起動時 real 拒否・[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)）は不変。本作業で追加する
> FX レート源は **provider 既定 `none`＝外部に一切接続しない**。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-10（リスク統制の上限）、FR-17（全体前提条件・費用/為替）、FR-04/FR-05（判断→発注の数量・金額）
- 計画書（技術検討）:
  05_trading-assumptions.md（計画リポ）
  §3 為替・通貨 —— **基準通貨 = JPY（円換算で統一、外貨併記）／実現損益 = 約定時レート・評価損益 = 日次終値／
  レート取得元 = 日銀API または FRED**。§2 は「為替スプレッドを**円換算コストとして**費用合計に含める」と定める。
- ADR: ADR-0003（計画リポ）（数値計算は
  コード側の責務）、ADR-0004（計画リポ）（案A+ の無料ソース＝FRED）
- 関連 IADR: [IADR-0003](../adr/IADR-0003_position-sizing-responsibility.md)（数量は `PositionSizer` の責務・**不変**）／
  [IADR-0018](../adr/IADR-0018_portfolio-ledger-projection.md)（台帳射影）／
  [IADR-0021](../adr/IADR-0021_trading-assumptions-configuration.md)（為替スプレッド近似）／
  [IADR-0064](../adr/IADR-0064_official-source-connectors.md)（FRED アダプタとレート制御の型）／
  [IADR-0068](../adr/IADR-0068_live-quote-feed-finnhub-extraction.md)（provider 選択・構成不備は no-op へ）／
  [IADR-0099](../adr/IADR-0099_current-price-context-for-decision.md)（現在値の判断文脈供給＝USD 生値の流入点）／
  本作業で新規 [IADR-0107](../adr/IADR-0107_base-currency-conversion.md)
- 対象 Issue: [#257](https://github.com/endazon/ai-stock-trading/issues/257)（`Refs #257`）

## 現状（develop `3447a7d` の実コードで確定した事実）

| # | 実態 | 位置 |
| --- | --- | --- |
| 1 | `OrderIntent` は「Price は**基準通貨（円換算）**の参照価格」と宣言し `Notional = Quantity × Price` を統制値にする | `Shared.Contracts/Trading/OrderIntent.cs` |
| 2 | **円換算を行うコードは存在しない**（`FxSpreadRatio` は費用率であって換算レートではない） | `Configuration.Domain/CostCalculator.cs` |
| 3 | 現在値は市況フィードの生値＝**銘柄のローカル通貨**（AAPL は USD）のまま参照価格になる | `TradeDecisionService.cs:128` |
| 4 | `PositionSizer` の doc は全入力を「基準通貨・円」と規定。USD 価格を渡すと金額キャップが桁で誤る | `RiskManagement.Domain/PositionSizer.cs` |
| 5 | `RiskEvaluator` は USD の `Notional` を円の上限（1注文/日次/段階資金）と比較する | `RiskEvaluator.cs:45,89,94` |
| 6 | 台帳（`PortfolioState`/`LedgerFill`）も「円」宣言だがローカル通貨が積み上がる。日次損失・DD 判定も同様 | `PortfolioProjection.cs` |
| 7 | プロンプトはリスク制約に「円」と明記する一方、現在値行に単位が無い（LLM が USD を円と解釈） | `TradeDecisionPromptBuilder.cs:54,59-60` |
| 8 | `MoomooBrokerAdapter` は `intent.Price` を**ブローカーの注文価格**として送る（＝ローカル通貨でなければならない） | `MMApiMoomooTradeClient.SetPrice` |

実効誤差（AAPL 336.77 USD・150 円/USD 想定）: 金額キャップ `35,000 ÷ 336.77 ≒ 103 株` に対し実所要額は約 520 万円。

**#1 と #8 は両立しない。** 「`Price` ＝円換算」という契約コメントと、`Price` を実発注価格として送る執行経路は矛盾しており、
評価用（基準通貨）と執行用（ローカル通貨）を分離しない限り解けない。本仕様はここを決着させる。

## 目的

1. 統制上限（1 注文金額・日次発注累計・段階資金上限・日次損失・最大DD）が**基準通貨（JPY）の実効値**で効く。
2. 発注執行へ渡す価格は**ローカル通貨のまま**（実発注価格を壊さない）。
3. レートが得られないときは、誤った実効上限で発注せず**見送り**へ倒す（fail-safe）。
4. 既定構成（JPY 市場のみ／FX provider 未設定）の挙動は現行と等価に保つ。

## スコープ

### 対象

1. **通貨の明示（`Shared.Contracts`）**
   - `Currency`（`Jpy`/`Usd`）と `MarketCurrency.Of(Market)`（純関数）、基準通貨 `MarketCurrency.Base = Jpy`。
   - `OrderIntent` に `FxRateToBase`（既定 `1m`＝JPY と等価）を追加し、`NotionalInBase = Quantity × Price × FxRateToBase`
     を新設する。`Price` は**ローカル通貨**（執行価格の権威）と定義し直し、陳腐化した契約コメントを実体に合わせる。
2. **FX レート源（`TradeDecisionService.Worker/Composable/Adapters/`）**
   - ポート `IFxRateSource`（`Shared.Contracts/Ports`）と `FxRate(Quote, Base, Rate, AsOf)`。実装（アダプタ）は唯一の消費者である判断ホストに置く（IADR-0064 の FRED アダプタと同じ配置）。
   - `FredFxRateSource`: FRED 系列（既定 `DEXJPUS`＝JPY per USD）の最新観測を取得。欠測（`.`）は採らない。
   - `CachingFxRateSource`: TTL キャッシュ＋**鮮度上限**（既定 7 日）。上限超過は `null`（＝レート無し）。
   - `NoOpFxRateSource`（常に `null`・初回 1 回だけ警告）と `FxRateSourceFactory`（`Fx:Provider` 既定 `none`）。
3. **判断側の通貨整合（`TradeDecisionService`）**
   - ポート `IFxRateProvider`（既定 NoOp）。市場が基準通貨なら常にレート 1（外部接続しない）。
   - 非基準通貨で**レートが取れなければ新規建てを見送る**（fail-safe・ログに理由を残す）。
   - サイジング（`PositionSizer`）・採算評価（`ProfitabilityGate`）へ渡す 1 株あたり金額（参照価格・損切り幅・想定利益）を
     基準通貨へ換算する。`OrderIntent` にはローカル通貨の価格とレートを載せる。
   - プロンプトの単位を明示する（現在値＝銘柄の通貨コード、リスク制約＝円）。
4. **統制・台帳の通貨整合（`RiskManagementService`）**
   - `RiskEvaluator` の 3 箇所を `NotionalInBase` に切り替える。
   - `LedgerFill` に `FxRateToBase` を追加（承認 Intent 由来）。`PortfolioProjection` は
     **建玉はローカル通貨のまま**畳み込み、金額集計（取得額・当日発注累計・実現損益・含み損益）を基準通貨で積む。
   - `OpenPosition` に加重平均の `FxRateToBase` を追加し、含み損益の換算に用いる。
   - 永続化（`ApprovedOrderRow.FxRateToBase`）と EF マイグレーション、InMemory 実装の追随。
   - 損切りの機械執行（`StopLossExecutionService`）が台帳の建玉レートを決済注文へ引き継ぐ
     （判断境界を通らない経路のため。照会失敗・建玉なしはレート 1 へ縮退し決済は止めない＝ADR-0003）。
5. **構成点**: `Fx:Provider`（既定空＝no-op）ほかを `appsettings.Development.json`・`docker-compose.yml`・
   helm `values.yaml`／`values-local.yaml`（経路B の有効化）に追加する。FRED の鍵は既存 `ast-secrets/fred-api-key` を再利用する。
6. **文書**: 本仕様書・[IADR-0107](../adr/IADR-0107_base-currency-conversion.md)・機能仕様書 FR-10・テスト仕様書 FR-10 の更新。

### 対象外（本 PR 外）

| 項目 | 残す先 | 理由 |
| --- | --- | --- |
| 含み損益（評価損益）を**日次終値レート**で評価する（計画 §3 の厳密な方式） | #257 に残置 | 本 PR は建玉の加重平均約定時レートで近似する（下記「設計」の逸脱記録）。厳密化は Risk へ FX 依存を持ち込む判断が要る |
| 日銀 API を FX provider として追加 | #257 に残置 | 計画 §3 は「日銀API **または** FRED」。FRED 単独で USD/JPY を満たす |
| 報告書の損益集計（`ReportService.Domain/PnlAggregator`）の通貨整合・外貨併記・為替損益（FX P&L）の分離計上 | 後続（FR-06/07/16）・#257 に残置 | 報告書は統制ゲートを持たない（段階ゲートの実績はリスク管理の台帳が権威）。`OrderExecuted` 経由でレートは届くため、損益計上方式（併記/分離）を決めてから当てる |
| バックテスト（FR-15）の通貨整合 | #208 に残置 | Stooq の米国株バーも USD。閾値較正と同時に扱うのが妥当 |
| 1 注文金額上限（35,000 円）で米国高価格株が数量 0 になる問題 | 利用者判断 | 換算後の**正しい**帰結。上限の見直し・銘柄選定は運用判断であり本 PR で勝手に変えない |

### 変更しないもの

- `PositionSizer` の計算式と「数量は `PositionSizer` の責務」という [IADR-0003](../adr/IADR-0003_position-sizing-responsibility.md) の決定。
- 建玉・損切り価格の**ローカル通貨**表現（市場監視の損切り検知は現在値と同一通貨で比較し続ける）。
- 実弾・SIMULATE 関連の設定、`Broker` 経路、既存イベントの意味。

## 設計

### 決定1: 基準通貨は JPY、`OrderIntent.Price` はローカル通貨、レートを同伴させる

`Price` は執行価格の権威（ブローカーへ送る値）であるためローカル通貨で確定する。統制が必要とする基準通貨の金額は
同伴レートから導出する（`NotionalInBase`）。既定 `FxRateToBase = 1m` により JPY 市場と既存データは挙動不変。

### 決定2: 換算点は判断境界の 1 点だけ

レート取得は `TradeDecisionService`（発注意図の生成点）でのみ行い、下流（リスク統制・台帳・報告）は**同伴レート**を使う。

- 計画 §3 の「実現損益 = 約定時レート」と一致する（意図生成時のレートが約定時レートの近似）。
- 同一注文に対する統制判定が、評価する時点によって変わらない（決定的）。
- Risk/Report サービスへ外部 FX 依存を増やさない。

### 決定3: レートが無ければ非基準通貨の新規建ては見送る（fail-safe）

`IsEnabled`（＝FX 源が実結線されているか）に関わらず、**非基準通貨の市場でレートが解決できなければ発注意図を作らない**。
古い/無いレートで発注するより見送る方が安全側（過大発注を招かない）。基準通貨（JPY）市場はレート 1 で従来どおり動く。

### 決定4: 含み損益は建玉の加重平均約定時レートで換算する（計画 §3 からの逸脱・記録）

計画 §3 は評価損益に日次終値レートを指定するが、本 PR は建玉に紐づく加重平均の約定時レートを用いる。

- 理由: Risk サービスへ外部 FX 依存（鮮度・障害・縮退）を持ち込まずに、桁違いの誤り（150 倍）を解消できる。
  残差は FX 変動分のみ（数%オーダー）であり、統制の実効性に対する影響は本件の主因と比べて小さい。
- 影響: 円安/円高による評価損益のずれは日次損失上限・DD 判定に残る。厳密化は #257 に残置し IADR-0107 に記録する。

### 決定5: FRED を FX レート源にする（`DEXJPUS`）

`ADR-0004`（案A+ の無料ソース）と計画 §3（日銀 または FRED）に従う。既存の FRED アダプタ（IADR-0064）と同じ型
（API キーはクエリ・OTel 計装抑止・レート制御）を踏襲する。`DEXJPUS` は営業日次・公表遅延があるため、
TTL キャッシュ（既定 6 時間）で叩く回数を抑え、**鮮度上限（既定 7 日）**を超えたら `null`（＝レート無し＝見送り）に倒す。

## 受け入れ基準 → テスト写像

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | 市場から通貨を導く（日本＝JPY／米国＝USD） | `MarketCurrencyTests` |
| 2 | `NotionalInBase` が同伴レートで基準通貨額になる（既定 1 は現行と等価） | `OrderIntentTests` |
| 3 | 統制上限（1注文・日次・段階資金）が基準通貨額で判定される | `RiskEvaluatorTests`（通貨換算ケース） |
| 4 | 非基準通貨でレートが無ければ新規建てを見送る | `TradeDecisionServiceTests` |
| 5 | 非基準通貨でレートがあれば、換算後の金額でサイジングされる（過大発注しない） | `TradeDecisionServiceTests` |
| 6 | 発注意図の価格・損切り価格はローカル通貨のまま（執行価格を壊さない） | `TradeDecisionServiceTests` |
| 7 | 基準通貨（JPY）市場は FX 源へ問い合わせず現行どおり動く | `TradeDecisionServiceTests` |
| 8 | 採算評価の notional・想定利益が基準通貨で評価される | `TradeDecisionServiceTests` |
| 9 | プロンプトの現在値に通貨が明示される | `TradeDecisionPromptBuilderTests` |
| 10 | 台帳の取得額・当日発注累計・実現損益・含み損益が基準通貨で積まれる | `PortfolioProjectionTests` |
| 11 | 建玉（平均取得単価・損切り価格）はローカル通貨のまま射影される | `PortfolioProjectionTests` |
| 12 | FRED から USD/JPY を取得する（スタブ HTTP・外部送信なし） | `FredFxRateSourceTests` |
| 13 | 欠測（`.`）・非成功応答はレート無しに縮退する | `FredFxRateSourceTests` |
| 14 | 鮮度上限を超えたレートは採らない／TTL 内はキャッシュを返す | `CachingFxRateSourceTests` |
| 15 | provider 既定・空・未知・キー無しは no-op（外部へ接続しない・警告する） | `FxRateSourceFactoryTests` |
| 16 | 判断ホストの配線（既定 no-op／`fred` 指定で実源／singleton／自己申告） | `FxWiringTests` |
| 17 | 損切り決済が建玉の換算レートを引き継ぐ／引けなくても決済は必ず発行する | `StopLossExecutionServiceTests`（3 ケース） |

## 完了条件

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` が緑。`dotnet format` 適用済み・警告ゼロ。
- 既定構成（`Fx:Provider` 未設定・JPY 市場）の挙動が現行と等価。実弾 OFF・SIMULATE 不変。
- テストが外部ネットワークへ送信しない（`HttpMessageHandler` スタブのみ）。
- `docs/DEFINITION_OF_DONE.md` を満たす。IADR-0107 に決定を記録する。
