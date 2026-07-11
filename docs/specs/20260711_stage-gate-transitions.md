---
title: 段階ゲートの遷移管理（状態機械＋承認フロー・純ロジック）
type: spec
status: done
related_ids: [FR-20, FR-15, ADR-0008, UC-06]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/06_technical/06_daytrading-review.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-gates-and-backtest.md
---

# 仕様書: 段階ゲートの遷移管理（状態機械＋承認フロー・純ロジック）

> Issue [#20](https://github.com/endazon/ai-stock-trading/issues/20)（`Refs #7`）。現状はリスク管理に `TradingStage`／`StageSettings`（段階ごとの
> モード・資金上限）と `RiskEvaluator` での**強制**のみが存在し、**遷移管理**（合格・撤退基準の評価、利用者承認フロー、遷移履歴）が未実装だった。
> 本スライスは段階遷移を管理する**純ロジック**（状態機械＋承認ゲート＋撤退フェイルセーフ＋遷移履歴の畳み込み）を TDD で実装し CI 緑で完結させる。
> 実運用への結線（承認 UI＝Discord/チャット、遷移履歴の永続化＝監査台帳、実績メトリクスの実供給、撤退時の KillSwitch 連動）は後続に切り分ける。

## 起点となる計画書・課題（トレーサビリティ）

- **FR-20**（Must）: 運用段階（Stage 0 検証 → 1 ペーパー → 2 最小実弾 → 3 段階増額）を管理し、段階ごとの動作モード（ペーパー/実弾）と資金上限を
  強制する。**段階遷移（昇格・差し戻し）は合格・撤退基準に基づき利用者の承認で行う**（`02_requirements/01_requirements.md` FR-20、UC-06）。
- **ADR-0008**（Accepted）: 段階ゲート運用を採用。撤退基準の既定＝「実ドローダウンがバックテスト時最大ドローダウンの **1.5 倍**に達したら自動停止・再検証」。
  昇格・差し戻しは事前定義した基準に基づき**利用者が承認**する。
- **06_daytrading-review §4**（段階ゲート提案）: 各段階の合格基準（次へ進む条件）と撤退・差し戻し基準を数値・条件で定義（下表）。
- **FR-15**（Must・バックテスト）: Stage 0 検証の合格＝Stage 1 昇格の前提条件（本スライスは合格判定を入力として受ける seam を用意）。
- **UC-06**: 利用者が設定変更・承認操作を行う（Discord コマンド／設定画面）。

### §4 の段階ゲート（合格・撤退基準）

| 段階 | 合格基準（次へ進む条件） | 撤退・差し戻し基準 |
| --- | --- | --- |
| Stage 0 検証 | DSR 補正後もエッジが正・最大DDが許容内（＝FR-15 バックテスト合格） | エッジ未確認の戦略は実弾に進めない |
| Stage 1 ペーパー | バックテストとの乖離が説明可能・統制違反 0 件 | 乖離が大きい場合は Stage 0 へ差し戻し |
| Stage 2 最小実弾 | 実効スリッページ・費用が想定内・日次損失上限の運用実績 | 実DD がバックテスト最大DD の 1.5 倍で自動停止・再検証 |
| Stage 3 段階増額 | 各増額後も指標が維持される | 同上（増額は月報レビュー時のみ） |

## 対象範囲（純ロジック・`RiskManagementService.Domain`）

### 入力メトリクス `StagePerformance`（record）

各段階の合格・撤退基準の評価に用いる観測値。FR-15 バックテスト verdict 等は別サービス（後続）で判定され、本ロジックには**入力として渡す**。

- `BacktestPassed`（bool）— Stage 0 合格ゲート（DSR 補正後エッジ正＋最大DD 許容内。FR-15 の verdict を封じ込める）。
- `BacktestMaxDrawdownRatio` / `ObservedMaxDrawdownRatio`（decimal）— 撤退基準の DD 比較（実DD ≥ バックテスト最大DD × 1.5）。
- `PaperDeviationExplained`（bool）— Stage 1→2 合格ゲート（バックテストとの乖離が説明可能）／Stage 1 撤退（乖離が大きい）。
- `ControlViolationCount`（int）— Stage 1→2 合格ゲート（統制違反 0 件）。
- `SlippageAndCostWithinExpected`（bool）・`DailyLossLimitRespected`（bool）— Stage 2→3 合格ゲート。

### 段階方針 `StageGatePolicy`（record）

- `Definitions`: `TradingStage → StageSettings`（Mode／CapitalCap）の4段階定義。Stage 0/1＝Paper、Stage 2/3＝Live。
- `WithdrawalDrawdownMultiple`（decimal・既定 1.5・ADR-0008）。
- 既定は `TradingDefaults.CreateStagePolicy()`。**実弾段階（Stage 2/3）の資金上限は保守的な暫定既定**とし、実運用値は利用者が FR-17 設定で確定する。

### 状態機械・承認ゲート `StageGate`（static・純関数）

- `AssessPromotion(current, perf, policy)` → `PromotionAssessment`（`TargetStage?`・`Eligible`・`UnmetCriteria`）。昇格先＝`current+1`。段階別に §4 合格基準を評価。
- `RequestTransition(current, nextSequence, approval, perf, policy, now)` → `StageTransitionResult`。
  - **昇格（target = current+1）**: 承認あり＋合格基準充足で受理。飛び級（target > current+1）は拒否。
  - **差し戻し（target < current）**: 承認ありなら常に受理（安全側・段階を下げる方向は基準不要）。撤退提案の承認先＝Stage 0（再検証、§4/ADR-0008）。
  - **承認の欠如**（`ApprovedBy` 空）は必ず拒否（`NoUserApproval`）。**昇格・差し戻しとも `RequestTransition` 経由でしか遷移を生成できない**（承認ゲートを構造的に強制）。
  - 受理時は `StageTransition`（履歴 1 件・不変）と `ResultingSettings`（新 Mode/Cap）を返す。
- `AssessWithdrawal(current, perf, policy)` → `WithdrawalAssessment`（`Triggered`・`Reason?`・`HaltNewEntries`・`ProposedStage?`）。
  - Stage 2/3: 実DD ≥ バックテスト最大DD × 1.5 → `Triggered`・`HaltNewEntries=true`（**自動停止**・ADR-0008）・`ProposedStage=Stage 0`（再検証提案）。
  - Stage 1: `!PaperDeviationExplained` → `Triggered`・`HaltNewEntries=false`（ペーパー）・`ProposedStage=Stage 0`（差し戻し提案）。
  - Stage 0: 撤退なし。

### 遷移履歴の畳み込み `StageGateLedger`（record・純関数）

- 不変の `History`（`StageTransition[]`）と、履歴を畳み込んだ `CurrentStage`・`NextSequence` を導出する。
- `Empty(initialStage)`／`Append(transition)`。`Append` は追記整合（`FromStage == CurrentStage`・`Sequence == NextSequence`）を検証し、破れば例外。
- これにより「**遷移履歴が監査できる**（承認者・時刻・from/to・理由が不変記録として残り、現在段階は履歴の畳み込みで再現できる）」を純ロジックで満たす。

## 受け入れ基準（Issue #20 骨子との対応）

CI で緑にする範囲（純ロジック・ユニット）:

- [x] **承認なしに段階が遷移しない**: `RequestTransition` は `ApprovedBy` 空だと昇格・差し戻しとも拒否（`NoUserApproval`）。遷移を生む経路は承認付き `RequestTransition` のみ。
- [x] **遷移履歴が監査できる**: 受理時に `StageTransition`（Seq/From/To/Kind/ApprovedBy/OccurredAt/Reason）を生成。`StageGateLedger` が追記整合を検証し、`CurrentStage` を履歴の畳み込みで再現。
- [x] **合格基準の評価**: Stage 0→1（BacktestPassed）、1→2（PaperDeviationExplained＋ControlViolation 0）、2→3（Slippage/Cost＋DailyLoss）。未充足は昇格拒否。飛び級拒否。
- [x] **差し戻し基準到達で自動安全側**: `AssessWithdrawal` が Stage 2/3 の DD 1.5 倍超で `HaltNewEntries`（自動停止）＋ Stage 0 再検証提案、Stage 1 の乖離大で Stage 0 差し戻し提案を返す。
- [x] **段階別モード・資金上限**: `StageGatePolicy`／`TradingDefaults.CreateStagePolicy()` が Stage 0/1＝Paper、Stage 2/3＝Live と各資金上限を定義。受理時 `ResultingSettings` に反映。

実 API／実コンテナ前提（CI 既定では実行しない・後続）:

- [ ] 承認操作 UI（Discord/チャット・UC-06）と `RequestTransition` の結線。
- [ ] 遷移履歴の永続化（監査台帳 `AuditService` 連携）と段階状態の永続化（`RiskSettingsStore` への `Stage` 反映）。
- [ ] 実績メトリクス（`StagePerformance`）の実供給（FR-15 バックテスト verdict・実DD 追跡・統制違反集計・スリッページ計測）。
- [ ] 撤退時 `HaltNewEntries` の `KillSwitch` 連動（自動停止の実発火）。

## 方式・トレードオフ（明示）

- **承認ゲートを構造で強制**: 遷移（`StageTransition`）を生む唯一の経路が承認付き `RequestTransition`。撤退は自動で**停止（Halt）**を促すが、**段階の降格は
  提案に留め承認を要する**。これで「差し戻しは利用者承認」（ADR-0008）と「撤退時に自動で安全側に倒れる」（Issue 受け入れ基準）を両立する。自動＝停止、承認＝段階変更。
- **メトリクスは入力**: FR-15 バックテスト verdict・実DD・統制違反等は別コンポーネントで判定され、本ロジックは決定的に評価する純関数に限定する（honest な責務分離・全面テスト可能）。
- **実弾段階の資金上限は保守的な暫定既定**: Stage 2/3 の CapitalCap 既定値は保守側に置き、実運用値は利用者が FR-17 設定で確定・変更する（`TradingDefaults` の他の暫定既定と同方針）。
- **段階状態は履歴の畳み込み**: 現在段階を `StageGateLedger` の履歴 fold で導出（SignedInventory の畳み込み方針と整合）。永続化は後続、純ロジックは追記整合の不変条件のみを担う。

## テスト方針

- `StageGateTests`（Domain）: 承認欠如拒否、段階別合格基準（充足/未充足）、飛び級拒否、差し戻し（承認あり受理）、撤退評価（Stage 2/3 DD 1.5 倍・Stage 1 乖離・Stage 0 なし）、受理時の `ResultingSettings`／`StageTransition` 内容。
- `StageGateLedgerTests`（Domain）: `Empty`→`Append` の畳み込み、`CurrentStage`/`NextSequence` 導出、追記整合違反（From 不一致・Seq 不一致）で例外。
- `TradingDefaultsTests`（既存に追記）: `CreateStagePolicy()` の段階別 Mode/Cap と撤退倍率 1.5。

## 関連仕様

- 実装ADR: [IADR-0041](../adr/IADR-0041_stage-gate-transitions.md)（承認ゲート／自動停止＋降格提案の分離・段階状態＝履歴畳み込み）
- 連携元: `RiskEvaluator`（段階モード・資金上限の強制。FR-20 の enforcement 側）／[IADR-0005](../adr/IADR-0005_stage-capital-cap-definition.md)（資金上限＝投入中資金＋当該注文額）

## 未決事項

- 承認 UI／永続化／実績メトリクス供給／撤退 Halt の KillSwitch 連動は後続 Issue で結線する。
- Stage 2/3 の資金上限の実運用値・分数ケリー上限（Stage 3 増額幅）は利用者が FR-17 設定・月報レビューで確定する。
