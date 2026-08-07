---
title: IADR-0076 取引判断の採算評価は既存の概算費用関数を再利用し、opt-in の純ドメインゲートで採算不成立・見積り不能を安全側 Hold に倒す
type: impl-adr
status: Accepted
related_ids: [FR-17, FR-04, FR-11, ADR-0003, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md
---

# IADR-0076: 取引判断の採算評価は既存の概算費用関数を再利用し、opt-in の純ドメインゲートで採算不成立・見積り不能を安全側 Hold に倒す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-17**（AI 判断の採算評価に前提条件を一律適用）、FR-04（取引判断）、FR-11（判断根拠の記録）、ADR-0003（方針階層＋独立リスク管理）、ADR-0001（platform 再利用）
- 計画書 §: [05_trading-assumptions §4](../../planning/projects/ai-stock-trading/06_technical/05_trading-assumptions.md)（最小期待利益＝往復費用＋税の 1.5 倍・概算費用関数・計算はコード）
- 対象 Issue: [#11](https://github.com/endazon/ai-stock-trading/issues/11)（残スコープ FR-17）
- 関連する実装仕様書: [20260718_trade-decision-profitability-gate](../specs/20260718_trade-decision-profitability-gate.md)
- 関連 IADR: [IADR-0021](IADR-0021_trading-assumptions-configuration.md)（`TradingAssumptions`/`CostCalculator` の所有）、
  [IADR-0063](IADR-0063_assumptions-versioned-resolution.md)（版付き前提条件の消費口・fail-safe）、
  [IADR-0065](IADR-0065_versioned-cost-limits-resolution.md)（薄い消費アダプタ・二重キャッシュ回避）、
  [IADR-0055](IADR-0055_llm-cost-metering-event.md)（LLM 月次費用計測＝別目的）、
  [IADR-0039](IADR-0039_decision-orchestration.md)（多数決・二段。ゲートは代表票に適用）

## コンテキストと課題

#11 の受け入れ骨子は多数決・二段判断・実 LLM 結線・RAG まで充足済みだが、スコープ箇条の
**FR-17「概算費用関数による採算評価（最小期待利益）」が未着手**で、これが `Closes` にしなかった理由だった。

計画 05 §4 は AI 判断のガードレールとして「往復の費用・税を差し引いて採算が合うか」を要求し、
「往復費用（＋税）の 1.5 倍を下回る取引は見送り」と定める。ただし FR-17 の概算費用関数
（`CostCalculator` / `TradingAssumptions`）は #19（IADR-0021）で**既に実装され、設定サービスが所有**している。

したがって課題は「費用関数を新規に作る」ことではなく、**既存の費用関数を TradeDecisionService に採算ゲートとして
適用し、採算が合わない判断・費用が見積れない状況を安全側（Hold）に倒す**ことである。以下を満たす必要がある。

- 既存費用計測基盤（IADR-0021 の `CostCalculator`／IADR-0055 の LLM 費用計測）を**変更でなく利用**し、二重計上・不整合を作らない。
- 純ドメインで判定を組み、TDD。設定は既定安全側・opt-in で調整可能。
- 採算不明・費用見積り不能時は安全側（Hold）。
- TradeDecisionService に閉じる（#152 が Risk/Notification を同時に触るため干渉しない）。`Shared.Contracts` は追加のみ。

## 決定

### 決定 1: 概算費用関数は新規に作らず、設定サービスの既存資産を消費経路ごと再利用する

`ConfigurationService.Domain.CostCalculator`（`EstimateRoundTripCost` ほか）＋ `TradingAssumptions` を単一情報源とし、
消費は `ConfigurationService.Client` の `IAssumptionsProvider`（版付き・fail-safe・`Configuration:BaseUrl` 未設定で既定/未解決）
を通す。これは CostControl(#139・IADR-0065) と同一経路で、キャッシュ・`AssumptionsChanged` 無効化・fail-safe を
共有クライアントに委ねる（**二重キャッシュ・二重実装を作らない**）。TradeDecision 側の消費アダプタは**薄く**する。

**代替案（棄却）**: TradeDecision に独自の費用関数・手数料表を持つ。→ FR-17 の「一元管理・一律適用」に反し、
設定サービスの版・実額登録と乖離する。棄却。

### 決定 2: 採算判定は純ドメイン `ProfitabilityGate` に置き、プリミティブのみを受ける

`ProfitabilityGate.Evaluate(expectedGrossProfit, estimatedRoundTripCost, decisionCost, minimumProfitMultiple)`
→ `ProfitabilityVerdict { Viable, NotViable, Indeterminate }`。しきい値 = `(往復費用 + 判断費用) × 倍率`。

Domain は Configuration.Domain に依存させない（費用の**計算**は Worker アダプタが `CostCalculator` で行い、
ゲートには算出済みの数値を渡す）。これによりゲートは設定サービス非依存で単体テスト可能な純関数となる。
多数決・二段（IADR-0039）で集約された**代表票**に対して適用する（合成せず実在の 1 票の想定利益を用いる）。

### 決定 3: fail-safe の向き＝「採算不能は Hold」。費用 0 で判定を緩めない

以下を `Indeterminate`（→ 呼び出し側 Hold）とする:

- 往復費用が `null`（前提条件が未解決＝`IAssumptionsProvider` が既定・`Version=0`）
- 往復費用 ≤ 0（moomoo 実額が未登録なら手数料は 0。**費用 0＝しきい値 0＝全通過は危険**なので採算不能扱い）
- 最小期待利益倍率 ≤ 0（構成異常）

判断費用の負値は 0 に正規化する。**費用が見積れないほど安全側（Hold）に倒す**のが本ガードレールの要点で、
実額が登録されて初めてゲートが有意に働く（計画 05 §2「実額は口座開設後に登録」と整合）。

### 決定 4: 想定利益は LLM 判断出力から取り、計算はコード・判断は LLM の分業を保つ

現行 `LlmDecision` は想定利益を持たないため、`ExpectedProfitPerShare`（既定 0・位置引数の既定値で後方互換）を追加し、
本判断プロンプトの JSON スキーマへ `expectedProfitPerShare` を加える。想定利益 = `ExpectedProfitPerShare × 数量`。
**値幅の見込み（相場判断）は LLM、費用・しきい値の算術はコード**という 05 §4 採用方針（「LLM には計算させない」）と整合する。
欠損・負値は 0（→ ゲート有効時は採算不成立で Hold＝保守側）。一次スクリーニングプロンプトは変更しない
（費用統制のため軽量に保つ・IADR-0039）。

### 決定 5: ゲートは opt-in（既定 OFF）。有効時のみ採算評価する

`ProfitabilityGateOptions { Enabled=false; DecisionCostJpy=0 }` と `Profitability:Enabled` / `Profitability:DecisionCostJpy`
の構成口を開放する。**既定 OFF ＝ 現行の判断挙動を一切変えない**（既存テスト不変・段階的有効化）。有効化は
`Configuration:BaseUrl`（実前提条件）の配線と対で意味を持つ（未配線なら決定 3 により Hold＝空振りせず安全）。

**「現行挙動不変」はコード側の分岐に留めず、LLM へ渡すプロンプト文言も含めて保証する**：決定 4 の採算節・
`expectedProfitPerShare` フィールドは `TradeDecisionPromptBuilder.Build(..., includeProfitability)` で `Enabled` の
ときのみ注入し、無効の既定ではプロンプトを現行動作とバイト単位で一致させる（LLM の判断傾向まで変えない）。
`CapturingLlm` で有効／無効のプロンプト差分をテストする（採算節の有無）。

### 決定 6: LLM/判断費用は per-trade の固定見積り（既定 0）に留め、月次計測と二重計上しない

IADR-0055 の LLM 費用計測は**月次予算計上**（`LlmCostIncurred` → CostControl）であり、本ゲートの `DecisionCostJpy` は
**その取引の採算分岐に足す per-trade 見積り**で、会計上の別目的＝二重計上ではない。実トークンからの動的供給は
オーケストレータへの usage 配線が要りスコープを広げるため後続とし、既定 0（中立）＋構成の固定値に留める。
往復取引費用と ×倍率マージンが主たるガードレールで、判断費用は補助項。

### 決定 7: 税は往復費用ベースに含めず、実損益連携時の後続とする

> **【❌ 撤回 2026-08-07・#358 / [IADR-0173](./IADR-0173_minimum-expected-profit-tax-inclusive.md)】 本決定は現行ではない。**
>
> 決定 7 は「計画 05 §4 は『往復費用＋税』を挙げるが……税は後続に委ねる」と述べたが、**その計画 §4 は 2026-07-23 の利用者決定で確定していた**（本 ADR の起案時点で既に確定済みであり、「後続に委ねる」判断は**計画からの逸脱**であった）。
>
> **現行の `ProfitabilityGate` は税を含める。** しきい値は不動点 `T = m × C × (1 − r) / (1 − m × r)` で解く（m＝倍率 2・C＝往復費用＋判断費用・r＝譲渡益税率 0.20315 → **T ≈ 2.684 × C**）。あわせて `m × r ≥ 1` は解が無いため **`Indeterminate`（見送り）へ fail-closed** する。
>
> 本文は起案時点の記録として残す。**現行は [IADR-0173](./IADR-0173_minimum-expected-profit-tax-inclusive.md) を正とする。**

計画 05 §4 は「往復費用＋税」を挙げるが、`CostCalculator.MinimumViableProfit`（IADR-0021）も税の精緻化を後続としている。
限界利益に対する税（20.315%）の織り込みは実損益連携で扱うのが整合的なため、本ゲートは往復取引費用（＋任意の判断費用）
までとし、税は後続に委ねる（IADR-0021 の申し送りを踏襲・スコープ肥大を避ける）。

## 影響・トレードオフ

- **良い点**: FR-17 の一元管理を崩さず採算ガードレールを追加。純ドメインで TDD。既定 OFF で無риск導入。fail-safe が費用 0 の抜けを塞ぐ。
- **代償**: 既定 OFF かつ `Configuration:BaseUrl` 未配線では採算評価は空振り（現行挙動）。実効化は実額登録・実配線（#19/#22）に依存する。
- ~~**税の非対応**: 限界利益への課税は未反映。実損益連携時に往復費用＋税へ拡張する余地を残す。~~ **【❌ 解消 2026-08-07・#358 / [IADR-0173](./IADR-0173_minimum-expected-profit-tax-inclusive.md)】** 税は**現行では反映済み**である（決定 7 の撤回を参照）。
- **想定利益の信頼性**: LLM の `expectedProfitPerShare` に依存する。幻覚時は過大/過小になり得るが、欠損・非正は保守側（Hold）に倒れ、
  過大時も ×倍率マージンが緩衝する。将来はバックテスト由来の期待値較正が拡張余地。

## 検証

- `ProfitabilityGate`・Parser・PromptBuilder・OptionsLoader・アダプタ・サービス結線を xUnit + FluentAssertions で TDD。
- `dotnet build/test backend/backend.slnx`（`Category!=Integration`）緑・`dotnet format` 差分なし・警告ゼロ。
- 既定 OFF で既存 `TradeDecisionServiceTests` が不変であることを確認（現行挙動保持）。
