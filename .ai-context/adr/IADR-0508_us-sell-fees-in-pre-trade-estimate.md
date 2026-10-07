---
title: IADR-0508 米国株の売りの取引諸費用（SEC・TAF）を事前見積り（採算判定・バックテスト）へ事後集計と同じ式で算入し、手数料・為替スプレッドが未登録なら採算判定は従来どおり見積り不能とする。取引判断の gRPC は料率を運び、設定画面は前提条件を往復させる
type: impl-adr
status: Accepted
related_ids: [FR-06, FR-16, FR-17, FR-15, SC-01, ADR-0035, IADR-0501, IADR-0076, IADR-0173, IADR-0177, IADR-0043, IADR-0331, IADR-0152]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0035_cost-ratio-denominator-and-cost-total-composition.md (決定 4・5)
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md (§2 米国株 売却時諸費用・§4 概算費用関数)
related_specs:
  - ../specs/20261008_1217_us-sell-fees-pre-trade-estimate.md
---

# IADR-0508: 米国株の売りの取引諸費用を事前見積りへ算入する（#1217）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-10-08
- 決定者: Claude Code（実装）。起点 [#1217](https://github.com/endazon/ai-stock-trading/issues/1217)（IADR-0501 §残余リスク の起票先）

## 起点・関連

- 関連する計画書 ID: FR-17（全体前提条件・概算費用関数）・FR-15（バックテスト）・FR-06 / FR-16（#1201 の残り）・SC-01（設定画面）
- 計画 ADR: **ADR-0035** 決定 5（米国株売却時諸費用の暫定値: SEC $20.60 / 百万ドル・TAF $0.000166 / 株・上限 $8.30）、決定 4（為替スプレッド相当は事前見積りに限る）
- 計画 `05_trading-assumptions.md` §4: 概算費用関数 `費用(市場, 売買, 約定代金) = 手数料 + 諸費用 + 為替スプレッド相当`（判断時の事前見積り・リスク判定）
- 先行 IADR: IADR-0501（事後集計へ諸費用を算入。事前見積りは不変とした）・IADR-0076 決定 3（往復費用 ≤ 0 は見送り）・IADR-0173 / 0177（最小期待利益の不動点と解無し）・IADR-0331（gRPC の 10 進文字列）

## コンテキスト

IADR-0501 は諸費用を報告書（事後集計）にだけ算入し、事前見積り `CostCalculator.EstimateOneWayCost` / `EstimateRoundTripCost` を
「手数料＋為替スプレッド相当」のまま残した。関数が売買方向と数量を受け取らず、TAF（株数比例・1 取引あたり上限）を計算できないためである。
そのため採算判定（見送りの閾値）とバックテストの費用は米国株の売り 1 回あたり最大 $8.30＋SEC 料だけ過小だった。

あわせて設定画面（SC-01）の型が新しい欄を持たず、HTTP の更新はレコード全体を置き換えるため、画面から保存すると料率が既定値へ戻った。
取引判断は配備既定で gRPC から前提条件を読むが、proto に欄が無く常に既定値で埋まっていた。

裁定待ちとの突合: planning#741 の項目 1〜3（ADR-0049 / ADR-0040 / ADR-0050。受け皿 #1228）は費用の見積りに関わらない。ADR-0035 は Accepted。

## 検討した選択肢

1. **［関数］事前見積りの関数へ売買方向と数量を通し、諸費用は事後集計（`FillCost`）と同じ private 関数で数える**（採用）——定数・式を複製せず、
   事前見積りと事後集計の諸費用が同じ約定で必ず同値になる。旧署名は残さない（残すと諸費用を含まない見積りへの経路が残る）。
2. ［関数］旧署名を残し、採算判定とバックテストだけ新関数へ移す —— 呼び出し側が旧署名を選べば黙って過小に戻る。
3. **［採算判定］登録費用（手数料＋為替スプレッド相当）が 0 なら見積り不能（`null`）にする**（採用）—— IADR-0076 決定 3 の安全網
   （「moomoo の実額が未登録なら手数料は 0。費用 0＝しきい値 0＝全通過は危険なので採算不能扱い」）を保つ。
4. ［採算判定］往復費用の合計だけで判定する（ゲートの `≤ 0` のまま）—— 諸費用は計画の暫定値で常に埋まるため、手数料が未登録でも
   米国株の往復費用が正になり、例えば $20,000・1,000 株で $0.578 を基準にしきい値がほぼ 0 へ緩む。決定 3 が禁じた壊れ方そのものである。
5. **［輸送］gRPC の前提条件に欄を足す（非破壊・番号 7）。欄の無い応答は計画の既定値で埋める**（採用）—— 料率を API で変えても
   取引判断が既定値を使い続ける状態を残さない。0 で埋めると諸費用が消え、過小見積りに戻る。
6. ［輸送］既定値のまま運ばない —— 「料率は前提条件の設定点から読む」（IADR-0501 決定 3）が取引判断でだけ成り立たない。
7. **［画面］取得した前提条件を土台に編集欄だけを上書きして送る**（採用）—— 編集 UI は足さない（issue の「別途判断」）。
   今後サーバが欄を足しても画面が黙って落とさない。

## 決定

### 決定 1: 事前見積りの関数は売買方向と数量を受け取り、諸費用を含む

- `EstimateOneWayCostBreakdown(assumptions, market, side, quantity, notional)` → `CostEstimateBreakdown(Commission, FxSpread, RegulatoryFees)`。
  `EstimateOneWayCost` は同じ引数の `Total`。諸費用は **`FillCost` と同じ private 関数 `RegulatoryFees`**（米国市場の売りだけ・空売りを含む）。
- 往復は `EstimateRoundTripCostBreakdown(assumptions, market, quantity, notional)` ＝ **買いの片道＋売りの片道**。ロングでもショートでも売りは 1 回なので
  諸費用は 1 回分。手仕舞いの約定代金は建てと同じと見積もる（手数料・為替スプレッドと同じ近似）。`EstimateRoundTripCost` はその `Total`。
- `MinimumViableProfit(assumptions, market, quantity, notional)` の往復費用も諸費用を含む。
- 採算見積りの供給口 `IProfitabilityAssumptionsProvider.AssessAsync(market, quantity, notional)` は、サイジング後の発注数量を受け取る。

### 決定 2: 採算判定は登録費用が 0 なら見積り不能のまま

`AssumptionsProfitabilityProvider` は往復の内訳の `RegisteredCost`（手数料＋為替スプレッド相当）が 0 以下なら `null` を返す（ゲートは見送り）。
登録済みなら往復費用は諸費用を含む `Total`。従来の「往復費用 ≤ 0」と登録費用の部分について同値であり、**諸費用だけが見送りの判定を変えることは無い**。

### 決定 3: 取引判断の gRPC は料率を運ぶ

`aistocktrading.configuration.v1.TradingAssumptions` に `UsSellRegulatoryFeeSchedule united_states_sell_regulatory_fees = 7`
（`sec_fee_per_million` / `taf_per_share` / `taf_cap_per_trade`。10 進文字列）を足す。提供側は常に書く。取引判断の受け手は欄が無ければ
`TradingAssumptionsDefaults.UnitedStatesSellRegulatoryFees`（計画の暫定値）で埋める。費用統制の受け手は料率を読まないので据え置く（既定値で埋まる）。

### 決定 4: バックテストは約定ごとに方向と数量を渡す

`BacktestCostModel.OneWayCost(market, side, quantity, notional, sensitivity)`／`RoundTripCost(market, quantity, notional, sensitivity)`。
`BacktestSimulator` は `SignedQuantity` の符号（負＝売り）と絶対値を渡す。感度倍率（コスト 2 倍）は諸費用にも掛かる。

### 決定 5: 設定画面は前提条件を往復させる

フロントエンドの `TradingAssumptions` に `unitedStatesSellRegulatoryFees` を足し、保存は取得値を土台に編集欄だけを上書きする。

## 結果

- 良い影響: 採算判定・バックテストの費用が計画 §4 の定義（手数料＋諸費用＋為替スプレッド相当）に揃い、事後集計と同じ式・同じ設定点で数える。
  API で変えた料率が画面の保存で戻らず、取引判断にも届く。
- **変わる判断**: 手数料が登録済みの米国株では、往復費用が諸費用（売り 1 回分）だけ増え、最小期待利益のしきい値が上がる。
  旧しきい値ちょうどの想定利益は見送りになる（T-17-11 が固定）。バックテストの米国株の売りの約定は費用が増え、エクイティがそのぶん下がる
  （`BacktestSimulatorTests` の期待を日付つきで変更）。日本株と買いの片道は変わらない。採算ゲートは既定で無効（`Profitability:Enabled=false`）。
- 悪い影響・トレードオフ:
  - 手仕舞いの約定代金を建てと同額と見積もるため、値上がりした売りの SEC 料は過小、値下がりは過大になる（料率は百万分の 20.6 で影響は小さい）。
  - Stage 0 の verdict は費用モデルの変更で値が変わり得る。2026-10-08 時点で Stage 0 の判定に用いた記録は無い（IADR-0507 §結果）。
  - 前提条件の更新に値域検証が無い（負の料率も受け付ける）のは従来どおり。本 IADR の射程外。
- テスト: T-17-05〜T-17-13（`CostCalculatorTests`・`AssumptionsProfitabilityProviderTests`・`TradeDecisionServiceTests`・`GrpcAssumptionsClientIntegrationTests`）、
  T-15-123（`BacktestCostModelTests`・`docs/tests/FR-15_backtest-tests.md`）、`AssumptionsGrpcServiceTests.全項目が写る`、
  フロントエンド `SettingsPage.test.tsx`（料率の往復）。
- 再配備: ConfigurationService・TradeDecisionService（proto の欄と採算判定）・BacktestService（費用モデル）・フロントエンド（設定画面）。
  提供側と受け手のどちらを先に更新しても壊れない（欄の追加は非破壊で、欠けたら既定値）。

## 関連

- [#1217](https://github.com/endazon/ai-stock-trading/issues/1217) / [#1201](https://github.com/endazon/ai-stock-trading/issues/1201) / PR #1216・[IADR-0501](./IADR-0501_report-cost-total-composition-and-post-trade-cost.md)
- 実装（`backend/` 配下）: `Shared/AiStockTrading.Shared.Kernel/Trading/{CostCalculator,TradingAssumptions}.cs`・
  `Shared/AiStockTrading.Shared.Grpc/Protos/aistocktrading/configuration/v1/assumptions.proto`・
  `Services/ConfigurationService/Features/Assumptions/GetAssumptions/GrpcEndpoint.cs`・
  `Services/TradeDecisionService/{Features/TradeDecision/IProfitabilityAssumptionsProvider.cs,Features/TradeDecision/DecideTrade/TradeDecisionAppService.cs,Infrastructure/ExternalServices/{AssumptionsProfitabilityProvider,NoOpProfitabilityAssumptionsProvider,GrpcAssumptionsClient}.cs}`・
  `Services/BacktestService/Domain/{BacktestCostModel,BacktestSimulator}.cs`
- フロントエンド: `frontend/src/features/sc01-settings/{types/index.ts,components/SettingsPage.tsx}`
