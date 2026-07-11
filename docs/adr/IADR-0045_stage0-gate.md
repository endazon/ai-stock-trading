---
title: IADR-0045 Stage 0 合格判定は 7 条件の合成とし、FR-20 へは昇格推奨・キルスイッチで接続する
type: impl-adr
status: Accepted
related_ids: [FR-15, FR-20, ADR-0008]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# IADR-0045: Stage 0 合格判定は 7 条件の合成とし、FR-20 へは昇格推奨・キルスイッチで接続する

> 実装リポジトリ内の意思決定記録。[IADR-0043](IADR-0043_backtest-foundation.md) の Slice C に対応。

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-15（Stage 0）、FR-20（段階ゲート）、ADR-0008、06_daytrading-review §4。
- 対象 Issue: [#16](https://github.com/endazon/ai-stock-trading/issues/16)（Slice C・全条件充足で `Closes #16`）。

## コンテキストと課題

Slice A/B で結果集計（Sharpe・最大 DD・コスト 2 倍リターン）と過剰適合補正（DSR・PBO・ウォークフォワード OOS）が
揃った。ADR-0008 は「DSR 補正後もエッジが正・最大 DD が許容内・コスト 2 倍でも期待値が正」を合格基準とし、合格戦略のみ
Stage 1 へ進める。段階遷移（昇格・差し戻し）は**利用者承認**で行い（FR-20・#20）、実弾段階の撤退既定は「実 DD が
バックテスト最大 DD の 1.5 倍」。判定と接続を純ドメインで実装する。

## 決定

1. **Stage 0 合格判定は 7 条件の合成**（`Stage0GateEvaluator` / `Stage0GateCriteria`）:
   | 条件 | 判定 | 既定閾値 |
   | --- | --- | --- |
   | エッジ有意 | DSR ≥ 閾値 | 0.95 |
   | 過剰適合 | PBO ≤ 閾値 | 0.50 |
   | 最大 DD | ≤ 許容 | 0.15（前提条件 DD 上限の緩め） |
   | コスト頑健性 | コスト 2 倍リターン > 0 | — |
   | ウォークフォワード | OOS リターン > 0 | — |
   | 試行数 | ≥ 最小試行数 | 1 |
   | データ健全性 | 全バーがカットオフ後/匿名化 | — |
   不合格時は `FailedChecks` に該当条件を列挙する（デバッグ・監査可能性）。データ健全性（検証条件①）は
   **「全バーがカットオフ後（`DataCutoffPolicy`）」または「銘柄匿名化済み（`Stage0GateContext.DataAnonymized`）」の OR** で判定する
   （ADR-0008/IADR-0044 の代替 2 経路）。匿名化済みなら LLM は銘柄を同定できないためカットオフ日付は不問。
2. **FR-20 接続は「昇格推奨」に限定**（`Stage0Promotion`）: 合格なら `Stage0Verification → Stage1Paper` の**推奨**を返す。
   実際の遷移・資金上限変更は FR-20 の**利用者承認フロー（#20）**で行う。本 Issue では判定と推奨まで。
3. **撤退キルスイッチ**（`KillSwitch`）: `実DD ≥ バックテスト最大DD × 1.5`（既定倍率）で停止判定（ADR-0008）。
   バックテスト最大 DD が 0（無ドローダウン）の場合は、正の実 DD をもって発火する保守側とする。
4. **段階の型は再利用**: `TradingStage`（`RiskManagement.Domain`, FR-20）を参照し新設しない。
5. **Application `Stage0GateService`** は DSR・PBO・ゲート・昇格推奨を合成する（オーケストレーション）。試行の全数実行や
   実データ取得は本 Issue のスコープ外（[IADR-0043] のとおり後続）。

## 理由

- 7 条件は ADR-0008 と 06_daytrading-review §3.2/§4 の合格基準を機械判定に落としたもの。列挙式で「なぜ落ちたか」を残せる。
- 遷移そのものではなく「推奨」に留めるのは、FR-20 が段階遷移を**利用者承認**に限定しているため（自動昇格は規律違反・#20 の責務）。
- キルスイッチの倍率 1.5 は ADR-0008 の既定であり、リスク統制（FR-10）の DD 上限とは別レイヤ（実運用の撤退トリガ）。

## 結果

- 良い影響: Stage 0 合格が再現可能な数値判定になり、合格戦略のみ Stage 1 昇格が推奨される。撤退基準も判定可能。
- 悪い影響・トレードオフ: 既定閾値（DSR 0.95・PBO 0.5・DD 0.15）は初期値であり、実データでの較正は後続。昇格の**実行**は #20。
- フォローアップ: #20 の承認フローへの結線、閾値の前提条件（FR-17）化、実データでの Stage 0 実走（実行基盤の後続）。

## 関連

- [IADR-0043](IADR-0043_backtest-foundation.md)、[IADR-0044](IADR-0044_overfitting-correction.md)、[FR-20 機能仕様](../functional/FR-20_staged-gates.md)
