---
title: 判定の基準通貨を JPY から USD へ移行する（#364）
type: spec
status: draft
related_ids: [FR-10, FR-17, FR-19, FR-20, NFR, UC-06, SC-02, ADR-0016, ADR-0018, ADR-0022, IADR-0107, IADR-0130, IADR-0152]
author: endazon (with Claude Code)
created: 2026-08-05
updated: 2026-08-05
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0018_risk-defaults-sync-and-stage0-dd.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0022_fx-rate-source-and-freshness.md
---

# 仕様書: 判定の基準通貨を JPY から USD へ移行する

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/ai-stock-trading/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: **FR-10**（リスク統制）／ FR-17（全体前提条件）／ FR-19（取引ガード）／ FR-20（段階ゲート）／ **NFR**
- ユースケース（UC）: UC-06（リスク設定の確認・変更）
- 画面（SC）: SC-02（リスク設定。実額併記の通貨表記）
- 関連 ADR: [ADR-0016](../../planning/projects/ai-stock-trading/07_adr/ADR-0016_short-selling-staged-release.md) 決定6（空売り解禁の自己資金 $5,000）、
  [ADR-0018](../../planning/projects/ai-stock-trading/07_adr/ADR-0018_risk-defaults-sync-and-stage0-dd.md)（既定値の確定単一値）、
  [ADR-0022](../../planning/projects/ai-stock-trading/07_adr/ADR-0022_fx-rate-source-and-freshness.md)（為替レート源と鮮度）
- 実装 ADR: [IADR-0107](../adr/IADR-0107_base-currency-conversion.md)（基準通貨 JPY・本作業で一部 supersede）／
  [IADR-0130](../adr/IADR-0130_equity-ratio-risk-limits.md) 決定3（切り離しの根拠）／
  **[IADR-0152](../adr/IADR-0152_usd-base-currency-migration.md)（本作業の決定）**
- 計画書リンク: [05_trading-assumptions.md](../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md) **§3**（基準通貨〔判定〕= USD・利用者決定 2026-07-31）・§5
- 対象 Issue: [#364](https://github.com/endazon/ai-stock-trading/issues/364)（由来 [#329](https://github.com/endazon/ai-stock-trading/issues/329)。
  関連 [#409](https://github.com/endazon/ai-stock-trading/issues/409)）

## 目的・背景

計画 §3 は 2026-07-31 の利用者決定で**判定の基準通貨を USD** と定めた（理由: JPY 基準では取引で損失が無くても
円高だけで最大 DD 上限に到達し統制が誤作動する。moomoo の信用必要額〔USD 建て〕とも基準が一致する）。
一方、実装のパイプラインは [IADR-0107](../adr/IADR-0107_base-currency-conversion.md)（当時の計画 §3「基準通貨 = JPY」に従ったもの）
のまま JPY 基準であり、`MarketCurrency.Base = Currency.Jpy` を単一情報源としている。

[#329](https://github.com/endazon/ai-stock-trading/issues/329)（IADR-0130 決定3）は equity の権威値だけを USD で保持し、
基準通貨パイプラインへは計画 §5 記載の参照レート（1 USD ≈ 163.7 円）で 1 点換算する形で移行を切り離した。
その切り離しは次の不変条件に依っている。

> equity と注文金額を**同一通貨で**評価する限り、比率による判定の結果は通貨に依存しない。

本作業はその不変条件を保ったまま基準通貨そのものを反転させ、**計画 §3 と実装を一致させる**。

### なぜ今か（移行の適期であることの裏取り）

基準通貨を反転させると、**既に記録された台帳行の意味が変わる**。`approved_orders.FxRateToBase` は
「ローカル通貨 1 単位あたりの基準通貨額」であり、基準通貨が変われば同じ数値が別の意味になる。
記録が積まれた後に移行すると、7 年保持で破棄できない監査証跡・業務台帳（[#346](https://github.com/endazon/ai-stock-trading/issues/346)）に
「JPY 建ての行と USD 建ての行が判別不能なまま混在する」状態が生まれる。

本作業では以下を確認した（結果は「未決事項」ではなく事実として §設計 に反映する）。

1. 実弾の発注経路は閉じている（`LiveTradingGate.LiveTradingReleased = false`。**本作業で触らない**）
2. `PriceInBase` は**永続化されていない**（`LedgerFill.PriceInBase` は `Price * FxRateToBase` の計算プロパティであり、
   DB に列を持たない）。永続化されているのは `approved_orders.Price`（ローカル通貨・意味は不変）と
   `approved_orders.FxRateToBase`（**意味が変わる**）の 2 つだけである
3. したがって移行の危険は `approved_orders` の既存行に限局する。**この 1 点を EF マイグレーションで構造的に検査する**
   （移行後も意味が変わらない行しか無いことを確かめ、そうでなければ移行を止める）

## 対象範囲

- **対象**
  - `MarketCurrency.Base` の反転（`Jpy` → `Usd`）と、それに追随する契約・ドメインの単位表記
  - FX レート源の**換算の向き**の是正（FRED `DEXJPUS` は USD/JPY のため USD 基準では逆数が要る）
  - equity の 1 点換算の廃止（`InitialCapital` / 空売り解禁下限が USD の権威値そのものになる）
  - SIMULATE プロファイルの基準資金の USD 建て化
  - 費用の為替スプレッド適用条件を「非基準通貨市場」へ一般化（現状は `Market.Japan` を直書き）
  - 表示単位の是正（報告書の金額表記・SC-02 の実額併記）。**基準通貨の単位で正しく表記する**ところまで
  - 既存台帳行に対する移行時の fail-closed 検査（EF マイグレーション）
  - 機能仕様書 FR-10・テスト仕様書 FR-10・画面仕様書 SC-02 の追随
- **対象外**
  - **表示通貨 JPY への換算表示**（計画 §3「基準通貨〔表示〕= JPY・外貨併記」）。報告書の円換算・為替差損益の独立表示は
    [#338](https://github.com/endazon/ai-stock-trading/issues/338)（報告サイクル）の範囲であり、本作業では表示の作り直しをしない
  - 監査・取引記録の保持設計（[#339](https://github.com/endazon/ai-stock-trading/issues/339)）
  - 旧実装からの切替計画（[#346](https://github.com/endazon/ai-stock-trading/issues/346)）。設定 JSON の読み替えは同 issue の担当
  - 日銀 API の第一情報源化・鮮度警告（[#381](https://github.com/endazon/ai-stock-trading/issues/381) / ADR-0022）。本作業は**向きだけ**を正す
  - 統制値（比率）の変更。`LiveTradingGate` に触れること

### 範囲の判断（#338 / #339 / #346 と統合しない）

issue #364 は「独立に進めるか、いずれかへ統合するかを着手前に決めること」と明記している。**独立に進める**。

| 候補 | 判断 | 理由 |
| --- | --- | --- |
| #338（報告サイクル）へ統合 | しない | #338 は**未実装**。統合すると基準通貨の定義の是正が報告機能の実装待ちになる。本作業が触るのは報告書の**単位表記**だけで、円換算表示という #338 の本体には手を入れない |
| #339（監査・取引記録）へ統合 | しない | 同じく未実装。かつ本作業の眼目は「記録が積まれる**前に**通貨の定義を確定させる」ことであり、記録設計の完成を待つと目的と逆行する |
| #346（切替計画）へ統合 | しない | #346 は旧実装から再実装版への切替（設定 JSON の互換）という別の関心事である。基準通貨の定義は切替の前提であって切替の一部ではない |

## 設計

### 1. 基準通貨の反転（単一情報源）

```csharp
// AiStockTrading.Shared.Contracts/Trading/Currency.cs
public const Currency Base = Currency.Usd;   // 旧: Currency.Jpy
```

`MarketCurrency.Base` は単一情報源であり、`IsBaseCurrency` / `FxRate.Identity` / `NoOpFxRateSource` /
`BaseCurrencyOnlyFxRateProvider` / `CachingFxRateSource` はいずれもこの定数から導いているため自動的に追随する。

**帰結（安全側の向きが変わる）**: FX レート源が未接続のとき、従来は「日本株は動き米国株が見送られる」であったが、
移行後は「**米国株は動き日本株が見送られる**」になる。主ターゲットは米国株（計画 §制約条件）であるため、
未接続時の既定挙動はむしろ改善する。

`OrderIntent.Price` / `LedgerFill.Price` / `OpenPosition.AverageEntryPrice` / `StopLossPrice` が**ローカル通貨**である
ことは変えない（IADR-0107 決定1 の執行価格に関する部分・決定4 の損切り検知は維持する）。

### 2. FX レート源の換算の向き（FRED `DEXJPUS` の逆数）

`DEXJPUS` は「1 USD あたりの円」である。`IFxRateSource.GetRateToBaseAsync(quote)` の契約は
「**quote 通貨 1 単位あたりの Base 通貨額**」であるから、Base = USD では次のようになる。

| quote | 旧（Base = JPY） | 新（Base = USD） |
| --- | --- | --- |
| `Usd` | `DEXJPUS`（観測値そのもの） | **1（恒等・外部へ問い合わせない）** |
| `Jpy` | 1（恒等） | **1 ÷ `DEXJPUS`（逆数）** |

- 解決対象の通貨は `Usd` → `Jpy` へ入れ替わる。**対応しない通貨は推測で換算しない**（現行の規律を維持）。
- **丸めない**。`decimal` の除算結果をそのまま用いる（`1m / 163.7m`）。丸めると往復（換算→逆換算）で誤差が
  片側へ偏り、統制の実効上限が系統的にずれる。`decimal` は 28〜29 桁の有効数字を持ち、統制判定に必要な精度に対して
  丸め誤差は無視できる。
- 観測値が 0 以下・解析不能な行を採らない既存の防御（`rate <= 0m` で `continue`）は逆数化の**前**に効くため、
  ゼロ除算は構造的に起こり得ない。
- 鮮度判定（`CachingFxRateSource` / `FxOptions`）は観測日 `AsOf` に対して行われ、逆数化の影響を受けない。

### 3. equity の 1 点換算を廃す

| 項目 | 旧 | 新 |
| --- | --- | --- |
| `TradingDefaults.InitialCapital` | `InitialEquityUsd × ReferenceUsdToJpyRate` = 491,100（円） | **`InitialEquityUsd`** = 3,000（USD） |
| `TradingDefaults.ReferenceUsdToJpyRate` | 163.7（1 点換算に使用） | **削除** |
| `StageProductPolicy.ShortSellLiveReleaseEquityInBase` | `5,000 × 163.7`（円） | **削除**（`ShortSellLiveReleaseEquityUsd` を直接使う） |

`ReferenceUsdToJpyRate` は「USD の権威値を JPY 基準のパイプラインへ供給する」ためだけに存在した定数であり、
その供給が不要になれば**死ぬ**。死んだ定数を残すと「まだ使う値」に見え、次の実装者が表示換算へ結線し直す余地が残る
（IADR-0137 決定2 / IADR-0148 決定2 / IADR-0149 決定3 と同じ規律）。**表示通貨 JPY への換算は静的な参照レートでは
なく実勢レート（ADR-0022 の情報源）で行うべきもの**であり、#338 の担当である。

`ShortSellLiveReleaseEquityInBase` の削除は**近似の除去**である。ADR-0016 決定6 は解禁下限を「自己資金の
**米ドル建て**評価額 $5,000」と定めており、equity が USD になれば参照レートによる近似は不要になる。

### 4. SIMULATE プロファイルの基準資金

moomoo シミュレータ口座の残高は USD $1,000,000 ＋ JPY ¥20,000,000 である。USD 基準では次のように持つ。

```csharp
public const decimal SimulatorUsdBalance = 1_000_000m;
public const decimal SimulatorJpyBalance = 20_000_000m;
public const decimal UsdToJpyRate = 150m;                       // プロファイル用の固定概算レート（据え置き）
public const decimal SimulatorJpyBalanceInUsd = 133_333m;       // ¥20,000,000 ÷ ¥150/USD ≒ $133,333（切り捨て）
public const decimal InitialCapital = SimulatorUsdBalance + SimulatorJpyBalanceInUsd;  // $1,133,333
```

`20,000,000 ÷ 150` は循環小数になるため、**切り捨てた整数**を定数として明示する。切り捨ては基準資金を小さくする
方向＝統制上限を緩めない方向であり、安全側である。プロファイルの目的（米国株の数量が算出できる規模を与える）は
`$1,133,333 × 25% = $283,333.25` で十分に満たされる（AAPL ≒ $335/株）。

### 5. 費用の為替スプレッドの適用条件

`CostCalculator.EstimateOneWayCost` は為替スプレッドを `market == Market.Japan ? 0m : ...` と**市場を直書き**して
判定している。これは「基準通貨が JPY である」ことに暗黙に依存した書き方である。
`MarketCurrency.IsBaseCurrency(market)` へ一般化し、**非基準通貨市場に適用する**（結果として日本市場へ反転する）。

為替スプレッドは通貨の交換に伴う費用であり、基準通貨の市場では交換が発生しない、という定義に忠実な形である。
手数料体系（`CommissionSchedule.Minimum` / `Cap`）は基準通貨建ての絶対額だが、**既定はいずれも 0（未登録）**で
あり（`TradingAssumptionsDefaults`）、再表示・再登録の必要は生じない。登録は口座開設後（USD 建て）に行う。

### 6. 表示単位

- **報告書**（`ReportAmountFormat`）: 「` 円`」固定を改め、**基準通貨の単位で表記する**。USD は小数 2 桁（セント）、
  JPY は小数 0 桁とする。単位は `MarketCurrency.Base` から導き、表記を 1 箇所に閉じる規律（IADR-0116）は保つ。
  **表示通貨 JPY への換算表示は #338 の範囲**であり本作業では作らない。
- **SC-02**（`formatAmount` / `RiskSettingsPage`）: `capital` が USD になるため、計画 SC-02 の表記例「**25%（$750）**」が
  そのまま正しくなる。`$` 接頭辞を付け、「円」ラベルを外す。これにより [#409](https://github.com/endazon/ai-stock-trading/issues/409) は
  **解消する**（環流記録 `feedback/20260805_sc02-equity-amount-currency.md` の案 C）。

### 7. 既存台帳行に対する fail-closed 検査（EF マイグレーション）

移行後も意味が変わらない `approved_orders` の行は、**米国市場（`Market = 1`）かつ `FxRateToBase` が 1 または NULL**
の行だけである。それ以外の行（日本市場の行・USD/JPY レートを同伴した行）は、移行によって同じ数値が別の意味へ化ける。

マイグレーション `AssertLedgerSafeForUsdBaseCurrency` は `Up` で該当行数を数え、**1 行でもあれば例外を送出して
移行を止める**。`Down` は何もしない（`Up` が状態を変えないため）。

```sql
-- Up（抜粋）
IF (SELECT count(*) FROM approved_orders
    WHERE NOT ("Market" = 1 AND ("FxRateToBase" IS NULL OR "FxRateToBase" = 1))) > 0
THEN RAISE EXCEPTION '...';
```

黙って通貨を化けさせるより、移行を止めて人間に判断させる方が安全である（統制の fail-closed 規律・IADR-0131 決定4 と同型）。
スキーマは変更しないため、モデルスナップショットは不変であり `has-pending-model-changes` は増えない。

### 8. 比率判定が通貨に依存しない不変条件（T-10-112）の維持

`RiskEvaluator` は `PortfolioSnapshot.Capital`（equity）と `OrderIntent.NotionalInBase` を**同じ通貨で**比較し、
上限は `MaxOrderAmountFor(equity)` 等の比率解決で得る。本作業は両者の通貨を**同時に**USD へ移すため、
不変条件は構造的に保たれる。既存のプロパティテスト（レート 1 / 150 / 163.7）はそのまま緑であり続けなければならない。
**このテストが赤くなったら移行の設計が誤っている。**

## 受け入れ基準

- [ ] `MarketCurrency.Base == Currency.Usd` であり、日本市場が非基準通貨・米国市場が基準通貨として扱われる
- [ ] FRED レート源は `quote == Jpy` に対してのみ観測を解決し、`1 ÷ DEXJPUS` を返す。`quote == Usd` は外部へ
      問い合わせず 1 を返す。逆数は丸めない
- [ ] FX レート源が未接続のとき、米国株は従来どおり判断でき、日本株の新規建てが見送られる
- [ ] `TradingDefaults.InitialCapital == 3_000m`（USD）であり、1 注文上限が `$750`・日次上限が `$4,500` に解決される
- [ ] 空売り解禁の判定が `$5,000` の equity で境界となる（参照レートによる近似が無い）
- [ ] **プロパティテスト T-10-112（比率判定は通貨に依存しない）が移行後も緑である**
- [ ] 統制値（比率 7 値・保有建玉数 3・段階の発注可能額比率）が 1 つも書き換わっていない
- [ ] 費用の為替スプレッドが非基準通貨市場（＝日本市場）へ適用される
- [ ] 報告書の金額表記と SC-02 の実額表示が基準通貨（USD）の単位で正しく表記される
- [ ] 既存台帳行が「移行後も意味が変わる」状態であればマイグレーションが失敗する
- [ ] `LiveTradingGate.LiveTradingReleased` は `false` のままである
- [ ] 計画適合検査（`PlanConformanceTests`）が緑であり、基準通貨に関する既知逸脱が残っていない

## テスト方針

| 観点 | 種別 | 置き場所 |
| --- | --- | --- |
| 基準通貨が USD である／市場と基準通貨の対応が反転する | 正 | `Shared.Contracts.Tests/CurrencyTests.cs` |
| `NotionalInBase` が同伴レートで USD へ換算される | 正 | 同上 |
| FRED が `Jpy` に対して逆数を返す／`Usd` は外部へ出ない | 正 | `TradeDecisionService.Infrastructure.Tests/FredFxRateSourceTests.cs` |
| FRED が `Usd` 以外・未対応通貨を推測で換算しない | **否定形** | 同上 |
| 逆数を丸めない（`1/163.7` の往復が観測値へ戻る） | 正 | 同上 |
| 未接続時に日本株が見送られ米国株は通る | **否定形** | `TradeDecisionService.Application.Tests` ／ `FxWiringTests` |
| `InitialCapital` が USD の権威値そのものである | 正 | `RiskManagementService.Domain.Tests/TradingDefaultsTests.cs` |
| 統制の比率が 1 つも変わっていない | 正 | 同上（既存の確定値テスト） |
| **比率判定が通貨に依存しない（T-10-112）** | プロパティ | `EquityRatioRiskLimitsTests.cs`（既存・無改修で緑） |
| 同伴レートを操作して上限を緩められない（T-10-124） | **否定形** | `RiskEvaluatorTests.cs`（既存） |
| 為替スプレッドが非基準通貨市場に掛かる | 正 | `ConfigurationService.Domain.Tests/CostCalculatorTests.cs` |
| 基準通貨市場に為替スプレッドが掛からない | **否定形** | 同上 |
| 報告書の金額表記が基準通貨の単位になる | 正 | `ReportService.Domain.Tests` |
| SC-02 の実額に `$` が付き円ラベルが消える | 正 | `frontend/src/features/risk/contracts.test.ts` ほか |

## 計画書との差異

- 差異: **あり**（縮小方向）。
  - IADR-0107 決定4（含み損益を建玉の加重平均約定時レートで換算する。計画 §3 は日次終値レートを指定）は**据え置く**。
    本作業は基準通貨の定義と換算の向きを正すものであり、評価レートの厳密化は別件（#338 の損益表示と同じ層）である。
  - 計画 §3「基準通貨〔表示〕= JPY（円換算で統一、外貨併記）」は**未実装のまま残る**。本作業は表示単位を
    「嘘をつかない状態」（基準通貨の単位で表記する）へ是正するに留め、円換算表示は #338 へ送る。
  - ADR-0022（日銀 API 第一・FRED フォールバック）への追随は #381 の担当であり、本作業は FRED の**向き**だけを正す。
    `KnownPlanDeviations` の `Fx.*` 3 件は本作業では解消しない。

## 未決事項

- なし（範囲の判断・IADR の起こし方は本仕様書 §対象範囲 と IADR-0152 で確定した）。
