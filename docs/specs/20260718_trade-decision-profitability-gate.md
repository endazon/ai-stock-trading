---
title: 取引判断の採算評価ゲート（FR-17 概算費用関数・最小期待利益／opt-in・fail-safe）
type: spec
status: review
related_ids: [FR-17, FR-04, FR-11, UC-01, UC-02, ADR-0003, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
---

# 仕様書: 取引判断の採算評価ゲート（FR-17 最小期待利益）

> Issue [#11](https://github.com/endazon/ai-stock-trading/issues/11)（FR-04, Must）の**残スコープ FR-17**。
> #11 は多数決・二段判断・実 LLM 結線・RAG まで実装済み（IADR-0061/0072 他）。本作業は **FR-17「概算費用関数による
> 採算評価・最小期待利益」を TradeDecisionService に適用する**ことに限る。FR-17 の概算費用関数そのもの
> （`CostCalculator` / `TradingAssumptions`）は #19（IADR-0021）で既に存在するため、**新規に作らず利用**する。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: **FR-17**（全体前提条件の一元管理・AI 判断の採算評価に一律適用）、FR-04（取引判断）、FR-11（判断根拠の記録）
- 計画書 §: [05_trading-assumptions](../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md) **§4「計算・判断の方針」**
  - 最小期待利益: 「往復費用＋税の <1.5 倍> を下回る取引は見送り」（AI 判断のガードレール・値は運用調整）

> **【❌ 訂正 2026-08-07・#358 / [IADR-0173](../adr/IADR-0173_minimum-expected-profit-tax-inclusive.md)】** 本仕様書が引いた「&lt;1.5 倍&gt;」は**計画の未確定の暫定表記**であった。**計画は 2026-07-23 の利用者決定で「往復費用＋税の 2 倍」へ確定している。** 以下の本文中の 1.5・「しきい値 = (往復費用 + 判断費用) × 倍率」という式・および**「税の精緻化は後続」とする記述（§前提・§設計・§受け入れ基準・§まとめ）は、いずれも現行ではない**。現行は倍率 **2**・基準 **往復費用＋税**であり、税が結果に依存するためしきい値は不動点 `T = m × C × (1 − r) / (1 − m × r)` で解く（m=2 / r=0.20315 で **T ≈ 2.684 × C**）。**本仕様書は 2026-07-18 時点の記録として残す**（point-in-time 記録）。現行は [IADR-0173](../adr/IADR-0173_minimum-expected-profit-tax-inclusive.md) を正とする。
  - 概算費用関数: `費用(市場, 売買, 約定代金) = 手数料 + 諸費用 + 為替スプレッド相当`（事前見積り・リスク判定・事後集計で共通利用）
  - 数値計算はコードで行い LLM には計算させない（採用方針）
- ユースケース（UC）: UC-01/02（取引判断のフロー）
- ADR: **ADR-0003**（方針階層＋独立リスク管理。採算ゲートは方針・リスクを上書きしない安全側の追加）、ADR-0001（platform 再利用）
- 関連 IADR:
  - [IADR-0021](../adr/IADR-0021_trading-assumptions-configuration.md)（`TradingAssumptions`/`CostCalculator` の所有者＝設定サービス。~~税の精緻化は後続と明記~~ **【❌ 2026-08-07・[IADR-0173](../adr/IADR-0173_minimum-expected-profit-tax-inclusive.md)】 IADR-0021 側の当該記述は訂正済みである**）
  - [IADR-0063](../adr/IADR-0063_assumptions-versioned-resolution.md)（`IAssumptionsProvider`＝版付き前提条件の消費口・fail-safe・`Configuration:BaseUrl` 未設定で既定/未解決）
  - [IADR-0065](../adr/IADR-0065_versioned-cost-limits-resolution.md)（#139：設定サービスの前提条件を消費側に薄いアダプタで結線する先例＝二重キャッシュを作らない）
  - [IADR-0055](../adr/IADR-0055_llm-cost-metering-event.md)（LLM 費用計測＝月次予算計上。本ゲートの per-trade 見積りとは**別目的**＝二重計上しない）
  - [IADR-0039](../adr/IADR-0039_decision-orchestration.md)（多数決・二段。採算ゲートは集約後の代表票に適用）
  - 本作業で新規 **[IADR-0076](../adr/IADR-0076_trade-decision-profitability-gate.md)**
- 対象 Issue: #11（本体）／依存 #19（前提条件の実配布・実額登録）・#22（実クラスタ結線の実証）

## 前提確認（着手前調査の結論）

1. **FR-17 の概算費用関数は既存**（複製しない）: `ConfigurationService.Domain.CostCalculator`
   （`EstimateOneWayCost` / `EstimateRoundTripCost` / `MinimumViableProfit`）＋ `TradingAssumptions`
   （手数料体系 `CommissionSchedule`・`FxSpreadRatio`・`MinimumExpectedProfitMultiple` 既定 1.5）。
2. **消費経路も既存**: `ConfigurationService.Client` の `IAssumptionsProvider` が版付き前提条件を fail-safe に供給する
   （CostControl #139 が同経路で消費）。`Configuration:BaseUrl` 未設定なら `DefaultAssumptionsProvider`（既定値・`Version=0`＝未解決）。
3. **未解決時の手数料は 0（moomoo 実額は口座開設後に登録）**。よって「版未解決 or 往復費用 ≤ 0」を採算不能とみなし
   **安全側（Hold）**へ倒す必要がある（費用 0 で採算判定を緩めない）。
4. **想定利益の出所**: LLM 判断出力。現行の `LlmDecision` は `ReferencePrice`/`StopLossDistancePerShare` のみで
   想定利益を持たないため、**採算評価には LLM の想定利益が必要**。→ 出力へ `expectedProfitPerShare` を追加する
   （価格・費用の**計算**はコード、想定値幅の**判断**は LLM ＝ §4 採用方針と整合）。

## スコープ（このPRで実装するもの）

すべて **TradeDecisionService 内に閉じる**。`Shared.Contracts` は変更しない。他サービスは無改修。

1. **純ドメイン採算ゲート** `TradeDecision.Domain.ProfitabilityGate`（純関数・TDD）
   - `Evaluate(expectedGrossProfit, estimatedRoundTripCost, decisionCost, minimumProfitMultiple) → ProfitabilityVerdict`
   - しきい値 = `(往復費用 + 判断費用) × 最小期待利益倍率`。`想定利益 ≥ しきい値` → `Viable`、未満 → `NotViable`。
   - **fail-safe = Indeterminate**（→ 呼び出し側 Hold）: 往復費用 `null`（版未解決）／往復費用 ≤ 0／倍率 ≤ 0。判断費用の負値は 0 に正規化。
2. **LLM 想定利益の取り込み**
   - `LlmDecision` に `ExpectedProfitPerShare`（既定 0）を追加（位置引数の既定値で後方互換）。
   - `TradeDecisionParser`：`expectedProfitPerShare`（任意）を解析。欠損・負値は 0（保守側）。
   - `TradeDecisionPromptBuilder.Build`：本判断プロンプトの JSON スキーマに `expectedProfitPerShare` を追加し、
     「費用（手数料・スプレッド）控除の採算で判断し、費用が相対的に大きい小口取引は Hold」を注記（§4 の概算費用方針をプロンプト文脈へ）。
     一次スクリーニングプロンプトは変更しない。
3. **Application ポート＋既定 no-op** `IProfitabilityAssumptionsProvider`
   - `AssessAsync(Market, notional) → TradeCostAssessment?`（`RoundTripCost`, `MinimumProfitMultiple`, `AssumptionsVersion`）。`null` = 未解決/不能。
   - 既定 `NoOpProfitabilityAssumptionsProvider`（常に `null`）＝ Application 単体は設定サービス非依存。
4. **Worker アダプタ** `AssumptionsProfitabilityProvider`（薄い）
   - `ConfigurationService.Client` の `IAssumptionsProvider` ＋ `CostCalculator`（Configuration.Domain）を利用。
   - `!IsResolved` → `null`（未解決）。解決時は `CostCalculator.EstimateRoundTripCost` と `MinimumExpectedProfitMultiple` を返す。
   - キャッシュ・無効化・fail-safe は共有クライアントに委譲（二重キャッシュを作らない＝IADR-0065 の踏襲）。
5. **opt-in 構成＋配線** `ProfitabilityGateOptions`（`Enabled` 既定 false／`DecisionCostJpy` 既定 0）＋ `ProfitabilityGateOptionsLoader`
   （`Profitability:Enabled` / `Profitability:DecisionCostJpy`）。`Program.cs` で登録し `AddAiStockTradingAssumptions` を配線。appsettings に空既定の口を開放。
6. **TradeDecisionService への結線**：サイジングで数量確定後・発注意図組み立て前に、`Enabled` のときのみ採算ゲートを適用。
   `Viable` 以外（`NotViable`/`Indeterminate`）は Hold（`null` を返す・FR-11 でログ）。

## スコープ外（後続 Issue の境界＝本 PR に含めない）

- ~~**税の精緻化（往復費用＋税ベース）**: `CostCalculator.MinimumViableProfit` と同様、実損益連携時の後続（IADR-0021 の申し送りを踏襲）。本ゲートは往復取引費用（＋任意の判断費用）まで。~~ **【❌ 撤回 2026-08-07・#358 / [IADR-0173](../adr/IADR-0173_minimum-expected-profit-tax-inclusive.md)】 税は現行では反映済みである。** しきい値は不動点 `T = m × C × (1 − r) / (1 − m × r)` で解き、`m × r ≥ 1` は `Indeterminate` へ fail-closed する。
- **実クラスタでの実 LLM＋実前提条件による採算ゲートの実証**（要 `Configuration:BaseUrl`・実額登録・#22 デプロイ配線）→ E2E/後続。
- **リスク管理側の費用込み上限判定（§4 の 3 番目の適用先）**: #152（Risk/Notification）が触るため本 PR 対象外。
- LLM 実トークンからの per-trade 判断費用の実測供給（`DecisionCostJpy` は構成の固定見積りに留める。既定 0＝中立）。

## 受け入れ基準 → テスト写像

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | 想定利益 ≥ (往復費用+判断費用)×倍率 → Viable、未満 → NotViable | `ProfitabilityGateTests` |
| 2 | 往復費用 null（版未解決）／≤0／倍率≤0 は Indeterminate（fail-safe） | `ProfitabilityGateTests` |
| 3 | 判断費用の負値は 0 に正規化して評価する | `ProfitabilityGateTests` |
| 4 | Parser が `expectedProfitPerShare` を解析、欠損・負値は 0 | `TradeDecisionParserTests` |
| 5 | 本判断プロンプトに `expectedProfitPerShare` と採算注記が載る／スクリーニングは不変 | `TradeDecisionPromptBuilderTests` |
| 6 | ゲート無効（既定）は現行挙動（採算評価せず発注意図を作る） | `TradeDecisionServiceTests` |
| 7 | ゲート有効＋採算不成立（想定利益過小）は Hold（発注意図を作らない） | `TradeDecisionServiceTests` |
| 8 | ゲート有効＋前提条件未解決（Assess=null）は Hold（fail-safe） | `TradeDecisionServiceTests` |
| 9 | ゲート有効＋採算成立は従来どおり発注意図を作る | `TradeDecisionServiceTests` |
| 10 | 未解決前提条件（IsResolved=false）でアダプタは null を返す／解決時は往復費用・倍率を返す | `AssumptionsProfitabilityProviderTests` |
| 11 | `Profitability:*` 未設定は Default（無効・判断費用0）／設定時に反映 | `ProfitabilityGateOptionsLoaderTests` |
| 12 | `AddAiStockTradingAssumptions` 配線でプロバイダが解決する（BaseUrl 未設定は既定＝未解決） | `ProfitabilityWiringTests` |

## 完了条件（Definition of Done 抜粋）

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx`（`Category!=Integration`）緑、`dotnet format` 差分なし、警告ゼロ。
- 新イベント追加なし・`Shared.Contracts` 変更なし・他サービス無改修（TradeDecisionService に閉じる）。
- ゲート既定 OFF ＝ 既存の判断挙動を一切変えない（現行テスト不変）。
- 二重計上なし（LLM 月次費用計測 IADR-0055 とは別目的の per-trade 見積り）。
- IADR-0076 に設計判断（既存費用関数の再利用・fail-safe の向き・想定利益の出所・~~税の後続化~~【❌ 撤回 2026-08-07・[IADR-0173](../adr/IADR-0173_minimum-expected-profit-tax-inclusive.md)。IADR-0076 決定 7 を撤回した】・opt-in）を明記。
