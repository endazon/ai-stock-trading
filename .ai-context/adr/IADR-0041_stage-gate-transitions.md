---
title: IADR-0041 段階遷移は承認ゲートを構造で強制し、撤退は「自動停止＋降格提案」に分離する（段階状態＝履歴の畳み込み）
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-15, ADR-0008, UC-06]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# IADR-0041: 段階遷移は承認ゲートを構造で強制し、撤退は「自動停止＋降格提案」に分離する（段階状態＝履歴の畳み込み）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-11
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-20（段階ゲート・遷移は利用者承認）、FR-15（バックテスト合格＝Stage 1 前提）、ADR-0008（段階ゲート採用・撤退基準 DD 1.5 倍）、UC-06
- 対象 Issue: [#20](https://github.com/endazon/ai-stock-trading/issues/20)（`Refs #7`）
- 関連する実装仕様書: [20260711_stage-gate-transitions](../specs/20260711_stage-gate-transitions.md)
- 関連 IADR: [IADR-0005](IADR-0005_stage-capital-cap-definition.md)（資金上限の判定基準）、[IADR-0033](IADR-0033_shared-inventory-fold.md)（畳み込みによる状態導出の先例）

## コンテキストと課題

FR-20 は「段階遷移（昇格・差し戻し）は合格・撤退基準に基づき**利用者の承認**で行う」とし、ADR-0008 は撤退基準到達時の「**自動停止・再検証**」を定める。
一方 Issue #20 の受け入れ基準は「**承認なしに段階が遷移しない**・遷移履歴が監査できる」「差し戻し基準到達時に**自動で安全側（降格提案・停止）に倒れる**」。
「承認で遷移」と「自動で安全側に倒れる」は一見矛盾するため、両立する設計上の切り分けが必要。加えて現状は段階の**強制**（`RiskEvaluator`）のみで**遷移管理**が無い。

## 決定

1. **承認ゲートを構造で強制する**: 段階遷移（`StageTransition`）を生成する唯一の経路を `StageGate.RequestTransition(...)` とし、その内部で
   `ApprovedBy` が空なら昇格・差し戻しとも必ず拒否（`NoUserApproval`）する。昇格は `current+1` かつ §4 合格基準充足を要し、飛び級は拒否する。
   差し戻し（段階を下げる方向）は安全側のため承認のみで受理する。これにより「承認なしに段階が遷移しない」を型・関数の構造で保証する。

2. **撤退は「自動停止（Halt）＋降格提案」に分離する**: `AssessWithdrawal(...)` は撤退基準（Stage 2/3＝実DD ≥ バックテスト最大DD × **1.5**、
   Stage 1＝乖離が説明不能）到達時に `HaltNewEntries`（**自動・即時の安全側停止**）と `ProposedStage=Stage 0`（**再検証への降格提案**）を返す。
   段階の実降格は提案に留め、確定は承認付き `RequestTransition` を要する。**自動＝停止、承認＝段階変更**という分離で、ADR-0008（差し戻しは承認）と
   Issue 受け入れ基準（撤退時に自動で安全側）を両立する。

3. **段階状態は遷移履歴の畳み込みで導出する**: `StageGateLedger` は不変の遷移履歴を保持し、`CurrentStage`・`NextSequence` を fold で導出する。
   `Append` は追記整合（`FromStage == CurrentStage`・`Sequence == NextSequence`）を検証する。これで「遷移履歴が監査できる」を純ロジックで満たす（IADR-0033 の畳み込み方針と整合）。

4. **合格・撤退基準の観測値は入力（`StagePerformance`）として受ける**: FR-15 バックテスト verdict・実DD・統制違反数・スリッページ/費用/日次損失実績は
   別コンポーネントで判定・計測され、本ロジックは決定的に評価する純関数に限定する。

5. **段階別モード・資金上限は `StageGatePolicy`（既定 `TradingDefaults.CreateStagePolicy()`）で定義する**: Stage 0/1＝Paper、Stage 2/3＝Live。
   **実弾段階（Stage 2/3）の資金上限は保守的な暫定既定**とし、実運用値は利用者が FR-17 設定で確定・変更する。

## 理由

- 「遷移を生む唯一の経路＝承認付き関数」という構造的制約は、フラグ検査に依存せず承認欠如時の遷移を型レベルで不可能にする（防御的分岐の漏れを排除）。
- 撤退時に「停止は自動・降格は承認」と分けることで、資金保全の即時性（NFR フェイルセーフ・ADR-0008 自動停止）と、段階変更の監査性・承認規律（ADR-0003 の介入規律）を両立できる。
- 段階状態を履歴の畳み込みで導出すると、現在段階と監査履歴が単一の情報源（append-only ログ）に一致し、乖離が生じない。
- メトリクスを入力に限定すると、判定ロジックを実基盤（FR-15・実DD 追跡）非依存で全面テストでき、CI 緑で完結できる。

## 結果

- 良い影響: 承認ゲート・撤退フェイルセーフ・遷移履歴が純ロジックとして整い全面テスト可能。段階の強制（`RiskEvaluator`）と遷移管理（`StageGate`）が責務分離される。
- 悪い影響・トレードオフ: 本スライス単体では production 挙動に変化なし（承認 UI・永続化・実績供給・Halt の KillSwitch 連動が未結線）。撤退の自動降格は行わず提案に留めるため、
  降格の最終確定には利用者操作が要る（停止は自動なので資金保全は即時に働く）。Stage 2/3 の資金上限既定は暫定値。
- フォローアップ: 承認 UI（Discord・UC-06）結線、遷移履歴の監査台帳永続化、段階状態の `RiskSettingsStore` 反映、`StagePerformance` 実供給（FR-15 verdict・実DD・統制違反）、
  撤退 `HaltNewEntries` の `KillSwitch` 連動、Stage 2/3 資金上限・分数ケリー上限の実値確定（FR-17／月報レビュー）。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: ADR-0008（計画リポ）、[IADR-0005](IADR-0005_stage-capital-cap-definition.md)、[IADR-0033](IADR-0033_shared-inventory-fold.md)
