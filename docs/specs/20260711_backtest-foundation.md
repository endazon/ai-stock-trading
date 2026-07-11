---
title: バックテスト基盤（過去データ供給の抽象・シミュレーション実行・過剰適合補正・Stage 0 合格判定）
type: spec
status: in-progress
related_ids: [FR-15, FR-20, FR-17, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 作業仕様書: バックテスト基盤（FR-15）

> Issue [#16](https://github.com/endazon/ai-stock-trading/issues/16)（FR-15）。ADR-0008 でバックテストは実弾投入前の
> **必須ゲート（Stage 0）**に格上げされた。本作業は「過去データ供給の抽象・シミュレーション実行・結果集計・過剰適合補正・
> Stage 0 合格判定」を**純ドメイン中心**で実装し CI 緑で完結させる（[IADR-0037](../adr/IADR-0037_backtest-foundation.md)）。
> 実データ源コネクタ・Worker ホスト・実コンテナ E2E は後続 Issue に切り分ける。

## 起点となる計画書・課題（トレーサビリティ）

- FR-15: 過去データによるバックテストを実弾投入前の必須ゲートとする。検証条件 = ①LLM 学習カットオフ後データ（または銘柄匿名化）、
  ②現実的コスト計上＋コスト 2 倍の感度分析、③ウォークフォワード検証、④試行数記録と過剰適合補正（DSR/PBO）、⑤生存者バイアスのない銘柄ユニバース。
- FR-20: 段階ゲート（Stage 0 検証 → 1 ペーパー → 2 最小実弾 → 3 段階増額）。Stage 0 合格戦略のみ Stage 1 へ。撤退基準の既定は
  「実 DD がバックテスト時最大 DD の 1.5 倍で自動停止・再検証」（ADR-0008）。
- FR-17: 概算費用関数（`CostCalculator`）を判断時見積り・事後集計・**バックテスト**で共通利用。
- UC-06: 要求トレーサビリティ表（`01_requirements.md`）の `FR-15, FR-20 | UC-06` に基づく。ただし UC-06 本文は「設定変更・緊急停止」が主で
  段階遷移承認・バックテストのフロー記述を欠く（計画側ギャップ）。`/plan-feedback` で計画側へ訂正提案する（#100 レビュー指摘）。
- 参照: 06_daytrading-review §3.2（バックテストの落とし穴：生存者バイアス・ルックアヘッド・コスト過小評価の統制、コスト 2 倍でも期待値が正）、§4（段階ゲート）。

## スコープと非スコープ

**スコープ（本 Issue）**: 検証条件①〜⑤の純ドメイン実装、Stage 0 合格判定、FR-20 遷移接続（昇格推奨・キルスイッチ基準）、in-memory の過去データ供給ポート。

**非スコープ（後続 Issue）**: 実データ源コネクタ（J-Quants Free/Stooq）、Worker(HTTP/メッセージ)ホスト、実コンテナ/実 API E2E、日中バー粒度、FR-20 の承認付き段階遷移フロー（#20）。

## アーキテクチャ

新規サービス `src/Services/BacktestService`（[IADR-0037]）。

- `BacktestService.Domain` — 純関数・不変レコード（I/O・時刻・乱数なし）。全計算・判定をここに置く。
- `BacktestService.Application` — オーケストレーション＋ポート（`IBarDataSource`）＋決定的 in-memory アダプタ。

再利用: `CostCalculator`/`TradingAssumptions`（`ConfigurationService.Domain`, FR-17）、`TradingStage`/`StageSettings`（`RiskManagement.Domain`, FR-20）、`Market`（`Shared.Contracts`）。

## 対象範囲（スライス別）

### Slice A — シミュレーションコア＋コストモデル＋結果集計（PR: `Refs #16`）

- `PriceBar(Symbol, Market, Date, Open, High, Low, Close, Volume)`: 日足 OHLCV（不変レコード）。
- `SecurityUniverse` / `UniverseMembership(Symbol, Market, ListedFrom, DelistedOn?)`: **Point-in-Time メンバーシップ**。
  `MembersAsOf(date)` は当時上場（廃止銘柄含む）の構成銘柄を返す ＝ 生存者バイアス排除（検証条件⑤）。
- `IBacktestStrategy`: 純関数。`DecideOrders(history bars[0..T], portfolio)` → 目標注文。**先読み禁止は構造で担保**（T までのバーのみ渡す）。
- `BacktestCostModel`: FR-17 `CostCalculator.EstimateOneWayCost` ＋スリッページ ＋**コスト倍率（`CostSensitivity` 1x/2x）**。約定ごとに計上（検証条件②）。
- `BacktestSimulator.Run(...)`: 決定的にバー単位で再生。**判断＝T 終値／約定＝T+1 始値＋スリッページ**（先読み排除）。→ `BacktestRun`（約定列・日次エクイティ曲線）。
- `BacktestMetrics`: 純集計。総リターン・Sharpe（日次リターン）・最大ドローダウン・勝率・取引数・エクイティ曲線。

### Slice B — 過剰適合補正ハーネス（PR: `Refs #16`。[IADR-0038]）

- `WalkForwardSplitter`: In-Sample→Out-of-Sample のローリング/アンカー窓分割（検証条件③）。
- `TrialLedger` / `BacktestTrial`: 試行（戦略構成候補）の Sharpe・OOS 実績・**試行数 N** を記録（検証条件④）。
- `DeflatedSharpeRatio`: 観測 SR・試行数 N・歪度・尖度・標本長 → DSR（多重検定補正後に真 SR>0 の確率）。
- `ProbabilityOfBacktestOverfitting`: CSCV（組合せ対称交差検証）で PBO 推定。
- `DataCutoffPolicy` / `SymbolAnonymizer`: 全バー日付が LLM 学習カットオフ後であることの検証、または決定的匿名化（検証条件①）。

### Slice C — Stage 0 合格判定＋FR-20 遷移接続（PR: `Closes #16`。[IADR-0039]）

- `Stage0GateCriteria` / `Stage0GateEvaluator`: ADR-0008 基準を判定 ＝ DSR 補正後もエッジ正・PBO 閾値以下・最大 DD 許容内・
  **コスト 2 倍でも期待値が正**・ウォークフォワード OOS 正・最小試行数。→ `Stage0GateResult(Passed, reasons[])`。
- FR-20 接続: 合格 → `Stage0Verification → Stage1Paper` の**昇格推奨**（承認は #20）。`KillSwitch.ShouldHalt(realDD, backtestMaxDD, 1.5)`（ADR-0008 撤退基準）。
- `BacktestService.Application` オーケストレータ: `IBarDataSource` からバー取得 → ベースライン/2x/ウォークフォワード実行 → 試行台帳 → ゲート判定。

## テスト方針（TDD）

- 受け入れ基準を `[Fact]`/`[Theory]`（xUnit + FluentAssertions）へ写像。コメントに起点 ID（FR-15/FR-20/ADR-0008）を残す。
- Domain は純関数として境界値・単調性・既知の解析値（合成データで手計算できるケース）を固定。
- 先読み排除は「未来バーを渡さない」ことをシミュレータ経路のテストで担保。DSR/PBO は文献の性質（試行数増で DSR 低下、無エッジ入力で PBO→0.5 近傍）を固定。

## 受け入れ基準（Issue #16 骨子）

- [ ] FR-15 検証条件①〜⑤（カットオフ後/匿名化・コスト 2 倍感度・ウォークフォワード・DSR/PBO・PIT ユニバース）が全て実装される。
- [ ] バックテスト合格（Stage 0 ゲート）した戦略のみ Stage 1 へ進める判定が実装される（FR-20 昇格推奨・撤退キルスイッチ）。
- [ ] `dotnet build` / `dotnet test` / `dotnet format` が緑。

## 関連仕様

- 機能仕様: [FR-15 バックテスト](../functional/FR-15_backtest.md)、[FR-20 段階ゲート](../functional/FR-20_staged-gates.md)
- 実装 ADR: [IADR-0037](../adr/IADR-0037_backtest-foundation.md)（基盤構成）、IADR-0038（過剰適合補正・Slice B）、IADR-0039（Stage 0 合格判定・Slice C）
