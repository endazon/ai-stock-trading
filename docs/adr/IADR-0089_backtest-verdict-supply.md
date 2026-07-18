---
title: IADR-0089 バックテスト verdict は BacktestEvaluated イベントで発行し Risk が read-modify-write で射影する（s2s 同期照会を退け fail-safe を保つ）
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-15, FR-11, UC-06, ADR-0008, IADR-0070, IADR-0045, IADR-0079, IADR-0082]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-rollout.md
---

# IADR-0089: バックテスト verdict は BacktestEvaluated イベントで発行し Risk が read-modify-write で射影する（s2s 同期照会を退け fail-safe を保つ）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-20**（段階ゲート）、**FR-15**（バックテスト）、**FR-11**（監査＝全イベントの時系列記録）、UC-06、ADR-0008（段階的展開）
- 対象 Issue: [#164](https://github.com/endazon/ai-stock-trading/issues/164)（バックテスト verdict／実 DD を Risk の `IStagePerformanceStore` へ s2s 供給・#20 後続）
- 関連 IADR: [IADR-0070](IADR-0070_stage-gate-persistence-and-approval.md)（段階ゲート永続化・`IStagePerformanceStore` が受け口）、[IADR-0045](IADR-0045_stage0-gate.md)（Stage 0 合格判定）、[IADR-0079](IADR-0079_event-backward-compat-contract-test.md)（イベント契約の後方互換）、[IADR-0082](IADR-0082_stage-transitioned-bus-audit.md)（イベント発行と中央監査の同型パターン）

## コンテキストと課題

段階ゲート（IADR-0070）は段階別実績 `StagePerformance` を Risk 専有の単一行ストア `IStagePerformanceStore` から供給し、
未記録時は fail-safe 既定（`BacktestPassed=false`）で Stage 0→1 昇格を拒否する。バックテスト合格判定（`Stage0GateService`
→ `Stage0Decision`）は `BacktestService` に純ドメインとして実装済みだが、`BacktestService` は Database per Service の
別サービス（Domain + Application ライブラリのみ・**ホストを持たない**）であり、verdict を Risk へ運ぶ経路が無い。論点:

1. **供給方式**: s2s 同期照会か、イベント射影か。
2. **契約表現**: `BacktestService` の verdict をどう Shared 契約に写すか（依存方向）。
3. **単一行への部分更新**: backtest 由来フィールドのみ更新し、他ドライバの運用系フィールドを壊さない方法。

## 決定

1. **供給方式はイベント射影とする（s2s 同期照会を退ける）。** Risk の昇格判定は同期ホットパス
   （`StageGateService.RequestTransition`/`StageGate.AssessPromotion`）である。ここで別サービスへ同期 s2s 照会すると
   ホットパスをブロックし、`BacktestService` の可用性に昇格判定が結合する。`BacktestService` が Stage 0 評価完了時に
   `BacktestEvaluated` を発行し、Risk が Consumer で `IStagePerformanceStore` へ射影する非同期経路にする。これは既存の
   Risk 射影（`OrderApprovedLedgerConsumer`/`OrderExecutedLedgerConsumer` が承認・約定を台帳へ射影する）と #167/IADR-0082 の
   発行規約に整合する。issue の「同期ホットパスを塞がない」「新イベントは監査 Consumer 追随」もイベント前提である。
   - 加えて `BacktestService` は照会先となる API ホストも持たない。s2s 照会にはホスト新設が必要で、本 PR の配線スコープを
     超える。イベント契約＋Risk 射影が確立パターンであり、実 publish ホストは #82/go-live へ分離する。

2. **契約は primitive で表現し、発行側 mapper は BacktestService.Application が所有する。** `Shared.Contracts` は
   Backtest.Domain / Risk.Domain に依存しない（依存逆転を避ける・IADR-0082 と同型）。`BacktestEvaluated` は verdict を
   `bool`、バックテスト最大 DD を `decimal`、DSR/PBO を `double`、未達条件を `string`（`Stage0GateCheck` 名の連結）で保持する。
   `Stage0Decision` → `BacktestEvaluated` の写像は純関数 `BacktestEvaluatedFactory`（BacktestService.Application）に置き、
   発行側が自分の verdict の契約表現を所有する。ホストが無くても mapper は単体テストで担保できる（配線の発行側担保）。
   追加のみで既存イベントは不変（IADR-0079）。`event-schemas.baseline.json` は `UPDATE_EVENT_BASELINE=1` で再生成する。

3. **Risk 射影は read-modify-write とし、backtest 由来フィールドのみ更新する。** `StagePerformance` は 7 フィールドの単一行で、
   `BacktestPassed`／`BacktestMaxDrawdownRatio` は backtest 由来だが、`ObservedMaxDrawdownRatio`／`ControlViolationCount`／
   `SlippageAndCostWithinExpected`／`DailyLossLimitRespected` は**運用系（別ドライバの供給源）**である。射影 Consumer は
   `GetCurrent()` で現行行を読み、backtest 由来 2 フィールドのみ `with` 更新して `Save` する。これにより運用系フィールドを
   上書きせず、将来の別ドライバがそれぞれ供給できる。本 PR は backtest verdict の供給に限定する（運用実績供給は非スコープ）。

4. **fail-safe を保つ。** 射影未達（実供給前・バス未到達）時は `IStagePerformanceStore` の既定（`BacktestPassed=false`）が
   そのまま昇格拒否になる。射影は永続化・判定の後段・非同期で、既定を崩さない。昇格の通し検証（実コンテナ E2E・実 publish）は
   #82 系へ分離し、本 PR は単体/契約/ハーネステストまでとする。

## 影響 / 波及

- 追加: `Shared.Contracts.Events.BacktestEvaluated`（primitive）、`BacktestService.Application.BacktestEvaluatedFactory`（純 mapper）、
  Risk `BacktestEvaluatedProjectionConsumer`（DI/Consumer 登録の隣接行）、`AuditEntryFactory.From(BacktestEvaluated)`、
  `BacktestEvaluatedAuditConsumer`（AuditService Worker の DI 隣接行）。
- 変更: `event-schemas.baseline.json` に `BacktestEvaluated` を追加（後方互換の追加）。
- Database per Service は跨がない（他サービス DB を直接参照せず、Shared.Contracts のイベントのみ介する）。
- **go-live（#82）側の発行ホストへの契約**: `BacktestEvaluatedFactory.From` の `backtestMaxDrawdownRatio` は、
  評価に用いた同一 `Stage0GateContext.BaselineMetrics.MaxDrawdown` から導出すること（`Stage0Decision` は最大 DD を
  保持しないため mapper 単体では強制できず、発行側の責務として残す）。乖離した値を渡すと Stage 0 判定と Risk へ供給する
  実 DD が食い違う。mapper のドキュメントコメントにも同契約を明記した。

## 代替案（不採用）

- **Risk から BacktestService へ s2s 同期照会**: 同期ホットパスをブロックし可用性を結合する。照会先ホストも未整備。
- **契約で Backtest.Domain / Risk.Domain の型を直接参照**: `Shared.Contracts` の依存逆転になり不可。
- **射影で `StagePerformance` 全体を上書き**: 他ドライバが供給する運用系フィールドを 0/false へ破壊し、Stage 1→2・2→3・
  撤退の入力を失う。read-modify-write でフィールド所有権を分離する。
- **中央監査を単一の権威にする**: Risk 専有ストアの fail-safe 既定（昇格拒否）を失う。中央監査は集約ビューに留める。
