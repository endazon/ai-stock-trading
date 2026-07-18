---
title: バックテスト verdict／実 DD を Risk の IStagePerformanceStore へイベント射影で供給する
type: work
status: In progress
related_ids: [FR-20, FR-15, FR-11, UC-06, ADR-0008, IADR-0070, IADR-0045, IADR-0079, IADR-0089]
issue: 164
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/  # FR-20（段階ゲート）/ FR-15（バックテスト）/ FR-11（監査）
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-rollout.md
---

# 作業仕様書: バックテスト verdict／実 DD を Risk の IStagePerformanceStore へイベント射影で供給する（#164）

## 目的 / 背景

段階ゲート（#20・PR #163・[IADR-0070](../adr/IADR-0070_stage-gate-persistence-and-approval.md)）は、段階別実績
`StagePerformance` を Risk 専有の単一行ストア（`IStagePerformanceStore`）から供給する。現状は **fail-safe 既定**
（`BacktestPassed=false` ほか全 false/0）であり、Stage 0→1 昇格は既定で拒否（`BacktestNotPassed`）される。

バックテスト合格判定（#16・FR-15）は `BacktestService`（`Stage0GateService` → `Stage0Decision`）に純ドメインとして
実装済みだが、`BacktestService` は **Database per Service（[IADR-0001](../adr/IADR-0001_repo-structure-and-stack.md)）の
別サービス**であり、verdict を Risk へ運ぶ経路が無いため `BacktestPassed` が解錠されない。

本作業は、`BacktestService` の Stage 0 合格 verdict（＋バックテスト最大 DD）を Risk の `IStagePerformanceStore.Save`
へ **イベント射影**で供給し、段階昇格ゲートを解錠する配線を実装する。設計判断は [IADR-0089](../adr/IADR-0089_backtest-verdict-supply.md)。

## スコープ

- `Shared.Contracts.Events` に `BacktestEvaluated` イベントを**追加**（既存契約は不変・追加のみ・primitive 表現）。
- `BacktestService.Application` に純 mapper `BacktestEvaluatedFactory`（`Stage0Decision` → `BacktestEvaluated`）を追加。
  発行側（BacktestService）が自分の verdict の契約表現を所有する。ホスト無しでも単体テストで担保する。
- Risk に `BacktestEvaluatedProjectionConsumer` を追加し、`IStagePerformanceStore` へ **read-modify-write** で射影する
  （backtest 由来の `BacktestPassed`／`BacktestMaxDrawdownRatio` のみ更新し、運用系フィールドは保全）。`Program.cs`
  の DI/Consumer 登録は隣接行に限定（#155 と非干渉）。
- 監査 Consumer 追随: `BacktestEvaluatedAuditConsumer` ＋ `AuditEntryFactory.From(BacktestEvaluated)` を追加し、
  `AuditConsumerCoverageTests`（全イベントの監査購読を CI で要求）を緑に保つ。
- `event-schemas.baseline.json`（IADR-0079）を新イベント追加で再生成（追加のみ＝後方互換）。

### 非スコープ（後続・境界明示）

- **運用実績の供給**（`ObservedMaxDrawdownRatio`／`ControlViolationCount`／スリッページ・費用実績／日次損失実績＝
  Stage 1→2・2→3 ゲート・撤退基準の入力）は **backtest 由来ではない**別ドライバの供給源。本作業は backtest verdict
  （`BacktestPassed`＋`BacktestMaxDrawdownRatio`）に限定する。read-modify-write により運用系フィールドは温存され、
  将来の別ドライバがそれぞれ供給できる。
- **実 BacktestService の publish ホスト**（`IPublishEndpoint` 発行の実駆動）と**昇格の通し検証（実コンテナ E2E）**は
  **#82 系（go-live 側）へ分離**。`BacktestService` は現状ホストを持たない（Domain + Application ライブラリのみ）ため、
  実発行は go-live で結線する。本作業は配線＋単体/契約/ハーネステストまで。
- 実データ未供給時は既定 false のまま＝**昇格拒否の fail-safe を崩さない**。

## 設計（要点）

- **供給方式はイベント射影**（s2s 同期照会ではない）。Risk の昇格判定は同期ホットパス（`RequestTransition`/
  `AssessPromotion`）であり、そこで別サービスへ同期照会するとホットパスをブロックする。イベント射影は非同期で
  ブロックせず、既存 Risk 射影（`OrderApprovedLedgerConsumer` 等）・#167 発行規約とも整合する。
- **契約は primitive 表現**。`Shared.Contracts` は Backtest.Domain / Risk.Domain に依存しない（依存逆転を避ける・
  #167 と同型）。verdict は `bool`、最大 DD は `decimal`、DSR/PBO は `double`、未達条件は `string`。
- **射影は read-modify-write**。`IStagePerformanceStore.GetCurrent()` で現行行を読み、backtest 由来フィールドのみ
  `with` 更新して `Save`。単一行に他ドライバが供給した運用系フィールドを上書きしない。
- **監査相関**は注文/市場相関を持たないため `AuditCorrelation.From("stage-gate")` の決定的 GUID を用いる
  （StageTransitioned/WithdrawalTriggered と同一相関で束ね、段階ゲート系を一元照会できる）。`Symbol` は null。
- **fail-safe**: 射影未達（実供給前・バス未到達）時は `IStagePerformanceStore` の既定（`BacktestPassed=false`）が
  そのまま昇格拒否になる。射影は永続化の後段・非同期で、既定を崩さない。

## 受け入れ基準（issue #164）→ テスト写像

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | verdict を Risk へ供給し `IStagePerformanceStore.Save` に反映（イベント射影） | Risk: `BacktestEvaluatedProjectionConsumerTests`（受領→Save 反映） |
| 2 | 供給後、Stage 0→1 昇格が受理される（`BacktestNotPassed` 解消） | 射影後 `GetCurrent().BacktestPassed==true` → 既存 `StageGate` 昇格テストで受理 |
| 3 | 実 DD（`BacktestMaxDrawdownRatio`）を供給 | 射影テストで `BacktestMaxDrawdownRatio` 反映を検証 |
| 4 | fail-safe: 供給不達/未取得時は既定（false）維持し昇格を許可しない | 射影前 `GetCurrent()` 既定 false（既存 store テスト）＋運用系フィールド保全テスト |
| 5 | Database per Service を跨がない（イベント射影で供給・他 DB 直接参照なし） | mapper 単体（BacktestService 側）＋ Risk consumer（Shared.Contracts のみ介する） |
| 6 | 新イベントは監査 Consumer 追随 | `AuditConsumerCoverageTests`（緑）・`AuditEventConsumersTests`・`AuditEntryFactoryTests` |
| 7 | 既存契約は後方互換（追加のみ） | `EventBackwardCompatibilityTests`（違反ゼロ）・baseline 差分 |
| 8 | 昇格の通し検証（実コンテナ E2E・実 publish） | **#82 系へ分離**（本 PR 非スコープ・境界明示） |

## 検証

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` 緑。
- `dotnet format` 差分なし。`nullable` 有効・警告ゼロ。
