---
title: バックテスト基盤（FR-15）機能仕様書
type: functional-spec
status: draft
related_ids: [FR-15, FR-20, FR-17, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 機能仕様書: バックテスト基盤（FR-15）

> 過去データによるバックテストを実弾投入前の**必須ゲート（Stage 0）**とする（ADR-0008）。本基盤は過去データ供給の抽象・
> シミュレーション実行・結果集計・過剰適合補正・Stage 0 合格判定を提供する。実装は純ドメイン中心（[IADR-0037](../adr/IADR-0037_backtest-foundation.md)）。

## 起点となる計画書（トレーサビリティ）

- 機能要求: FR-15（バックテスト＝Stage 0 の前提）。横断: FR-20（段階ゲート）・FR-17（費用関数共通化）。
- ユースケース: UC-06（段階遷移の承認）。
- 計画書リンク: `06_daytrading-review.md` §3.2/§4、ADR-0008。

## 検証条件（FR-15 記載）と実装対応

| # | 検証条件 | 実装（純ドメイン） | スライス |
| --- | --- | --- | --- |
| ① | LLM 学習カットオフ後データ（または銘柄匿名化） | `DataCutoffPolicy`（全バー日付 > カットオフ）／`SymbolAnonymizer`（決定的匿名化） | B |
| ② | 現実的コスト計上＋コスト 2 倍の感度分析 | `BacktestCostModel`（FR-17 `CostCalculator` ＋スリッページ ＋ `CostSensitivity` 1x/2x） | A |
| ③ | ウォークフォワード検証 | `WalkForwardSplitter`（IS→OOS 窓分割） | B |
| ④ | 試行数記録と過剰適合補正（DSR/PBO） | `TrialLedger`＋`DeflatedSharpeRatio`＋`ProbabilityOfBacktestOverfitting`（CSCV） | B |
| ⑤ | 生存者バイアスのない銘柄ユニバース | `SecurityUniverse`（Point-in-Time メンバーシップ・廃止銘柄含む） | A |

## 機能詳細

### シミュレーション（Slice A）

- 入力: 過去データ（`IBarDataSource`）・銘柄ユニバース（PIT）・戦略（`IBacktestStrategy`）・コストモデル・期間。
- **先読み排除**: 判断は当日 T の終値まで（`bars[0..T]`）で行い、約定は翌営業日 T+1 の始値＋スリッページ（マーケタブルリミット近似）。
- 出力: 約定列・日次エクイティ曲線・`BacktestMetrics`（総リターン・Sharpe・最大 DD・勝率・取引数）。

### 過剰適合補正（Slice B）

- ウォークフォワードで IS 最適・OOS 評価を分離。試行台帳が候補数 N を記録。DSR は N と標本モーメントで観測 SR を多重検定補正。
  PBO は CSCV で「IS 最良が OOS で中央値以下に落ちる確率」を推定。
- LLM 汚染対策: カットオフ後データの強制、または銘柄匿名化で LLM が銘柄を同定できないようにする。

### Stage 0 合格判定・遷移接続（Slice C）

| 判定 | 条件（ADR-0008） | 既定閾値 |
| --- | --- | --- |
| エッジ有意 | DSR 補正後もエッジが正 | DSR ≥ 0.95（真 SR>0 の確率） |
| 過剰適合 | PBO が閾値以下 | PBO ≤ 0.5 |
| 最大 DD | 許容内 | ≤ 許容 DD（既定 15%＝前提条件の DD 上限） |
| コスト頑健性 | **コスト 2 倍でも期待値が正** | 2x リターン > 0 |
| ウォークフォワード | OOS が正 | OOS 総リターン > 0 |
| 試行数 | 最小試行数以上 | N ≥ 1（記録の存在） |
| データ健全性 | 全バーがカットオフ後/匿名化（検証条件①） | `DataCutoffPolicy` 充足（`Stage0GateCheck.DataCutoff`） |

- 合格 → `Stage0Verification → Stage1Paper` の**昇格推奨**を返す（実際の遷移承認は利用者・#20）。
- 撤退キルスイッチ: 実 DD がバックテスト最大 DD の **1.5 倍**で自動停止・再検証（ADR-0008。`KillSwitch.ShouldHalt`）。

## 例外・エラー処理

| 条件 | 振る舞い |
| --- | --- |
| カットオフ以前のバーが混入 | `DataCutoffPolicy` が違反を検出（合格させない） |
| ユニバースに廃止銘柄が無い（生存者バイアス疑い） | 検証は可能だが、PIT メンバーシップで当時構成を要求 |
| 試行数 0 / 標本長不足 | DSR/PBO は保守側（合格させない方向）に倒す |
| いずれかの合格基準を満たさない | `Stage0GateResult.Passed=false` と不合格理由を返す |

## 受け入れ基準

- [x] 検証条件①〜⑤が実装され、テストで固定される（①③④=Slice B、②⑤=Slice A）。
- [x] Stage 0 合格判定が ADR-0008 基準（DSR/PBO/最大 DD/コスト 2 倍/ウォークフォワード＋データカットオフ＝7 条件）で行われる（Slice C）。
- [x] 合格戦略のみ Stage 1 昇格推奨が出る（FR-20 接続）。撤退キルスイッチ（実 DD>1.5x）が判定できる（Slice C）。

## 関連仕様

- 機能仕様: [FR-20 段階ゲート](FR-20_staged-gates.md)、[FR-10 リスク統制](FR-10_risk-controls.md)
- 実装 ADR: [IADR-0037](../adr/IADR-0037_backtest-foundation.md)、IADR-0038（過剰適合補正）、IADR-0039（Stage 0 合格判定）
- 作業仕様: [20260711_backtest-foundation](../specs/20260711_backtest-foundation.md)
