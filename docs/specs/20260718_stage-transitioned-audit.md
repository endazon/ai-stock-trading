---
title: 段階遷移イベント（StageTransitioned）のバス発行と中央監査集約
type: work
status: Draft
related_ids: [FR-20, FR-11, UC-06, ADR-0008, IADR-0070, IADR-0019, IADR-0082]
issue: 167
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/  # FR-20（段階ゲート）/ FR-11（監査）
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-rollout.md
---

# 作業仕様書: 段階遷移イベント（StageTransitioned）のバス発行と中央監査集約（#167）

## 目的 / 背景

段階ゲート（#20・PR #163・[IADR-0070](../adr/IADR-0070_stage-gate-persistence-and-approval.md)）は、遷移履歴を Risk 専有 DB の
`stage_transitions`（追記専用）へ永続化し `GET /risk-controls/stage-gate/history` で照会可能とした。ただしこれは
**Risk サービス専有の監査面**であり、中央監査台帳（`audit_events`・FR-11・AuditService）へは集約していない。
IADR-0070 は `StageTransitioned` のバス発行を「`Shared.Contracts` への追加＋Audit Consumer を伴うため後続へ分離」
としており、本作業はその中央監査集約（FR-11: 全イベントの時系列記録）を実装する。設計判断は [IADR-0082](../adr/IADR-0082_stage-transitioned-bus-audit.md)。

## スコープ

- `Shared.Contracts.Events` に `StageTransitioned` イベントを**追加**（既存契約は不変・追加のみ）。
- Risk の段階遷移**受理点**で `IPublishEndpoint`（Worker 層）により発行（受理時のみ・拒否時は非発行）。
- AuditService に `StageTransitionedAuditConsumer` を追加し `AuditEntryFactory` へ写像。中央 `audit_events` に集約。
- `event-schemas.baseline.json`（IADR-0079）を新イベント追加で再生成。

### 非スコープ（後続）

- #166（撤退ドライバ）による自動撤退の遷移確定の結線（Risk 同一箇所・本 issue の後）。
- Discord Bot からの承認ハンドラ（#15 系）。
- 実コンテナ E2E（#82 系）。本作業はユニット + MassTransit テストハーネスで担保する。

## 設計（要点）

- **発行点は Worker（エンドポイント）**。この基盤は「Application は純粋・Worker が発行を統率」する規約
  （`ScreeningOutcome` パターン。Risk.Application は MassTransit 非依存）に従う。`StageGateService.RequestTransition`
  は既に `StageTransitionResult{Accepted, Transition}` を返すため、`/risk-controls/stage-gate/transition`
  エンドポイントで **受理時のみ**（`Accepted && Transition is not null`）発行する。永続化（`ledgerStore.Append`）は
  サービス内で先に完了しており、これが権威（fail-safe）。
- **契約は primitive 表現**。`TradingStage` / `StageTransitionKind` は Risk.Domain の型のため、`Shared.Contracts` は
  それらに依存しない。段階は `int`（enum の数値割当と一致）、種別は `string`（`nameof`）で表現する。
- **監査相関**は注文相関（DecisionId）も市場相関（EventId）も持たないため、`AuditCorrelation.From("stage-gate")` の
  決定的 GUID を用いる（`AssumptionsChanged` と同系）。`Symbol` は null。

## 受け入れ基準（issue #167）→ テスト写像

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | `StageTransitioned` を `Shared.Contracts.Events` に追加（追加のみ・既存不変） | `EventBackwardCompatibilityTests`（違反ゼロ）・`event-schemas.baseline.json` 差分 |
| 2 | `RequestTransition` 受理時のみ発行（拒否時は非発行） | Risk Worker: エンドポイント経由の発行テスト（受理→発行 / 拒否→非発行） |
| 3 | AuditService に対応 Consumer を追加 | `AuditConsumerCoverageTests`（緑）・`AuditEventConsumersTests`（段階遷移が記録） |
| 4 | 中央 `audit_events` に from/to・承認者・時刻・種別が記録され OwnerOnly 照会可能 | `AuditEntryFactoryTests`（写像）・既存 OwnerOnly 照会は不変 |
| 5 | Risk 専有 `stage-gate/history` に加え中央監査でも一元照会 | 既存 Risk 履歴テスト不変 + 監査集約テスト |
| 6 | fail-safe: バス未到達でも `stage_transitions` は権威として保持 | 既存 `EfStageGateStoreTests` 不変（発行は永続化の後段・非同期） |

## 検証

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` 緑。
- `dotnet format` 差分なし。`nullable` 有効・警告ゼロ。
