---
title: 米国株の売りの諸費用（SEC 料・TAF）を事前見積り（採算判定・バックテスト）へ算入し、設定画面の保存で料率が既定値へ戻らないようにする
type: spec
status: accepted
related_ids: [FR-06, FR-16, FR-17, FR-15, SC-01, ADR-0035, IADR-0501, IADR-0076, IADR-0043, IADR-0331, IADR-0173, IADR-0508]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0035_cost-ratio-denominator-and-cost-total-composition.md（決定 4・5）
  - planning:projects/ai-stock-trading/06_technical/05_trading-assumptions.md（§2 米国株 売却時諸費用・§4 概算費用関数）
---

# 仕様書: 米国株の売りの諸費用を事前見積りへ算入し、設定画面の保存で料率を保つ（#1217）

## 起点となる計画書（トレーサビリティ）

- FR-06 / FR-16（#1201 の残り）・FR-17（全体前提条件・概算費用関数）・FR-15（バックテストの費用モデル）・SC-01（設定画面）
- 計画 ADR-0035 決定 5（米国株売却時諸費用の暫定値）・決定 4（為替スプレッドは事前見積りに限る）
- 計画 `05_trading-assumptions.md` §2「米国株 売却時諸費用」（SEC $20.60 / 百万ドル・TAF $0.000166 / 株・上限 $8.30。確認日 2026-09-05）、
  §4「概算費用関数 `費用(市場, 売買, 約定代金) = 手数料 + 諸費用 + 為替スプレッド相当`。判断時の事前見積り・リスク判定で用いる」
- 読んだ版: planning `origin/main` `b5b584f`（隣接クローンの読み取りのみ）
- 起票: #1217（#1201 / PR #1216・IADR-0501 §残余リスク の起票先）

## 裁定待ちとの突合（着手前）

planning#741 の項目 1〜3（AST#1228 が受け皿）は ADR-0049 決定 3（呼値の丸めの向き）・ADR-0040 決定 1（起動時停止）・
ADR-0050 決定 2（「不明」の案内）であり、**費用の事前見積り・設定画面の保存はいずれにも依存しない**。ADR-0035 は `Accepted` の裁定済み。→ 全項目を実施する。

## 目的・背景

#1216 で SEC 料・TAF を事後集計（報告書）へ算入したが、(1) 事前見積り（採算判定・バックテスト）の関数は売買方向と数量を受け取らず、
諸費用を算入できない（計画 §4 は事前見積りを「手数料＋諸費用＋為替スプレッド相当」と定める）。(2) 設定画面（SC-01）の型が新しい欄を持たず、
HTTP の更新はレコード全体を置き換えるため、画面から保存すると料率が既定値へ戻る。

## 母集合（規則 9・10。着手前に引いた）

誤りの側の文字列で `git grep` した（`.ai-context/specs/`・`CHANGELOG.md` は point-in-time / 生成物のため除外）。

| 走査語 | 一致 | 扱い |
| --- | --- | --- |
| `EstimateOneWayCost` / `EstimateOneWayCostBreakdown` / `EstimateRoundTripCost` / `MinimumViableProfit`（呼び出し） | 本体: `BacktestCostModel.cs:27`・`AssumptionsProfitabilityProvider.cs:28`・`CostCalculator.cs` 内。試験: `CostCalculatorTests.cs`・`PeriodCostReviewTests.cs:97,192-193`・`TradeHistoryViewBuilderTests.cs:66`・`FillPnlAttributionTests.cs:139` | 署名に売買方向・数量を足し、全呼び出しを追随させる（旧署名を残さない——残すと諸費用を含まない見積りへの経路が残る） |
| `AssessAsync(`（採算見積りの供給口） | `IProfitabilityAssumptionsProvider.cs:11`・`AssumptionsProfitabilityProvider.cs:16`・`NoOpProfitabilityAssumptionsProvider.cs:11`・`TradeDecisionAppService.cs:1018`、試験 `AssumptionsProfitabilityProviderTests.cs`・`ProfitabilityWiringTests.cs`・`TradeDecisionServiceTests.cs:454` | 数量を足す |
| `.OneWayCost(` / `.RoundTripCost(`（バックテスト） | `BacktestSimulator.cs:109`、試験 `BacktestCostModelTests.cs` | 売買方向・数量を足す |
| `本欄を読まない` / `事前見積り（…EstimateOneWayCost）しか使わず` | `TradingAssumptions.cs:66-67` | 取引判断が読むようになるため書き直す |
| `手数料 + 為替スプレッド相当` / `手数料＋為替スプレッド`（事前見積りの定義） | `CostCalculator.cs:8`・`docs/data/trading-assumptions.md:55`・`docs/tests/FR-15_backtest-tests.md:98` | 諸費用を足す。凍結記録（IADR-0025 / 0226 / 0305 / 0306 / 0480 / 0501 の本文）は書き換えない |
| gRPC の前提条件の写し（`FromProto` / `ToProto`） | `ConfigurationService/.../GrpcEndpoint.cs`・`TradeDecisionService/.../GrpcAssumptionsClient.cs`・`CostControlService/.../GrpcAssumptionsClient.cs`・`assumptions.proto` | 取引判断が料率を読むようになるため欄を足す（下の決定 3）。費用統制は読まないので据え置く（既定値で埋まる） |
| フロントエンド `fxSpreadRatio`（前提条件の型・往復） | `sc01-settings/types/index.ts`・`SettingsPage.tsx`・試験 3 本・`e2e/fixtures.ts` | 型に欄を足し、保存で往復させる |
| `docs/tests/FR-1[57]*`（試験 ID） | FR-15 のみ存在（最大 `T-15-122`）。FR-17 のテスト仕様書は無い（網羅裁定の必須対象外） | バックテストの変化を `T-15-123` として足す。採算判定は FR-17 の試験として ID を採番しない（既存の採算試験も ID を持たない） |

**この変更で新たに誤りになる自分の記述（規則 10）**: `CostCalculator.cs` 冒頭の「事前見積り＝手数料＋為替スプレッド相当」、
`TradingAssumptions.cs` の「受け取る側は本欄を読まない」、`BacktestCostModel` の「片道 = (FR-17 概算費用 ＋ スリッページ)」、
`AssumptionsProfitabilityProvider` 冒頭（費用の由来）、`IProfitabilityAssumptionsProvider` の「市場・約定代金から」、
`docs/data/trading-assumptions.md` の「必須欄ではない —— gRPC／HTTP の受け手では既定値で埋まる」・事前見積りの式、
`docs/tests/FR-15_backtest-tests.md` の T-15-05 の受け入れ基準文。いずれも本 PR で直す。IADR-0501 の残余リスク（凍結）は追記で扱う。

**窓（規則 11）**: 本変更は時間差を扱わない（料率は前提条件の版で決まり、見積りは判断時点の 1 回）。対象外。

## 設計

### 決定 1: 事前見積りの関数に売買方向と数量を通す（諸費用は FillCost と同じ式・同じ設定点）

- `CostCalculator.EstimateOneWayCostBreakdown(assumptions, market, side, quantity, notional)` → `CostEstimateBreakdown(Commission, FxSpread, RegulatoryFees)`。
  `EstimateOneWayCost` は同じ引数でその `Total`。
- 諸費用の式は `FillCost` と**同じ private 関数**（`RegulatoryFees`）に置く（米国市場の売りだけ・`UnitedStatesSellRegulatoryFees.For(notional, quantity)`）。定数を複製しない。
- `EstimateRoundTripCostBreakdown(assumptions, market, quantity, notional)` ＝ **買いの片道＋売りの片道**。
  往復はロング（買い→売り）でもショート（空売り→買い戻し）でも**売りがちょうど 1 回**含まれるため、方向を問わず諸費用は 1 回分である。
  手仕舞いの約定代金は建てと同じと見積もる（手数料・為替スプレッドの往復見積りと同じ近似）。`EstimateRoundTripCost` はその `Total`。
- `MinimumViableProfit(assumptions, market, quantity, notional)` も往復費用に諸費用を含む。

### 決定 2: 採算判定は諸費用を含めるが、「手数料未登録で費用 0 なら見送り」（IADR-0076 決定 3）を保つ

`ProfitabilityGate` は往復費用 ≤ 0 を `Indeterminate`（見送り）とし、これが「moomoo の実額（手数料）が未登録」の安全網だった。
諸費用は計画の暫定値で**常に埋まる**ため、そのまま足すと手数料が未登録でも米国株の往復費用が正になり、
わずかな諸費用（例: $20,000・1,000 株で $0.578）だけを基準にしきい値がほぼ 0 へ緩む——決定 3 が禁じた「費用 0 で判定を緩める」と同じ壊れ方になる。

そこで `AssumptionsProfitabilityProvider` は**利用者が登録する費用（手数料＋為替スプレッド相当）の往復分が 0 以下なら見積り不能（`null`）**を返し、
従来と同じく見送りへ倒す。登録済みなら往復費用は諸費用を含む `Total` を返す。
この判定は従来の「往復費用 ≤ 0」と**登録費用の部分について同値**であり、諸費用だけが見送りの判定を変えることは無い（試験で固定する）。

### 決定 3: 取引判断へ諸費用の料率を運ぶ（gRPC の前提条件に欄を足す）

取引判断は配備既定で gRPC（`Configuration__Grpc`）から前提条件を読むが、proto に欄が無く既定値で埋まっていた。
採算判定が料率を読むようになるため、`aistocktrading.configuration.v1.TradingAssumptions` へ
`UsSellRegulatoryFeeSchedule united_states_sell_regulatory_fees = 7`（`sec_fee_per_million` / `taf_per_share` / `taf_cap_per_trade`。10 進文字列）を**追加**する（非破壊。番号は新規）。
提供側（`ToProto`）は常に書く。取引判断の受け手（`FromProto`）は**欄が無ければ計画の既定値**で埋める（旧提供側との並走で 0 にしない）。
費用統制の受け手は料率を読まないため据え置く（既定値で埋まる）。REST の受け手は JSON の逆直列化で既に運ぶ。

### 決定 4: バックテストは約定ごとに方向と数量を渡す

`BacktestCostModel.OneWayCost(market, side, quantity, notional, sensitivity)` と `RoundTripCost(market, quantity, notional, sensitivity)`。
`BacktestSimulator` は約定ごとに `SignedQuantity` の符号で方向（負＝売り）、絶対値で数量を渡す。感度倍率（コスト 2 倍）は諸費用にも掛かる（全費用の倍率）。

### 決定 5: 設定画面は前提条件を往復させる（SC-01）

`TradingAssumptions`（フロントエンドの型）に `unitedStatesSellRegulatoryFees`（`secFeePerMillion` / `tafPerShare` / `tafCapPerTrade`）を足し、
保存（`fromForm`）は**取得した前提条件を土台に編集欄だけを上書き**する（画面が編集しない欄は取得値のまま送る）。編集 UI は足さない（issue の「別途判断」）。

### IADR

取引判断の振る舞い（見送りの閾値に掛かる費用）が変わるため **IADR-0508** に記録し、IADR-0501 の残余リスクへ日付つき追記を足す。

## 受け入れ基準

1. **Given** 米国株の往復の事前見積り **When** 採算判定・バックテストの費用を計算する **Then** SEC 料・TAF（上限つき）が売りの片道に 1 回算入される。買い・日本株には算入されない。
2. 採算判定の判断が変わる場面（諸費用なしなら通過・ありなら見送り）を試験で固定する。手数料・為替スプレッドが未登録（0）なら諸費用があっても見送り（見積り不能）のまま。
3. 取引判断の gRPC の受け手が提供側の料率を運び、欄の無い応答では計画の既定値で埋まる。
4. **Given** 設定画面で保存する **When** 諸費用の料率が計画値以外である **Then** 送信する前提条件に同じ料率が載る。
5. 事前見積りに算入したことで期待値が変わった既存試験には日付つきの注記を残す（弱めない）。

## テスト方針

- `CostCalculatorTests`: 事前見積りの片道（米国の売りに諸費用・買いと日本株は 0・TAF 上限）、往復は諸費用 1 回分、事前見積りと事後集計の諸費用が同値、最小期待利益が諸費用を含む。
- `AssumptionsProfitabilityProviderTests` / `ProfitabilityWiringTests`: 往復費用が諸費用を含む・登録費用 0 なら null・判断が変わる場面（ゲートの通過→見送り）。
- `BacktestCostModelTests`（T-15-123）: 米国の売りに諸費用・買いには無し・コスト 2 倍に諸費用も含む。
- gRPC: `AssumptionsGrpcServiceTests`（提供側が料率を書く）・取引判断の `GrpcAssumptionsClient` 試験（運ぶ／欠けたら既定）。
- フロントエンド `SettingsPage.test.tsx`: 計画値以外の料率が保存の送信に往復する。
- 変異: 諸費用の算入を外す／売りの判定を外す／null 判定を外す／`fromForm` の土台を外す、で該当試験が赤になることを確かめる。

## 対象外・残るもの

- 設定画面での諸費用の編集 UI（issue の「別途判断」）。
- 前提条件の更新時の値域検証（負の料率等）—— 現状どの欄にも無い。本 issue の射程外。
- 手仕舞いの約定代金を建てと同額と見積もる近似（手数料と同じ）。
