---
title: IADR-0082 段階遷移イベントは Worker 発行点でバス発行し中央監査へ集約する（契約は primitive・Risk 専有台帳を権威に据え置く）
type: impl-adr
status: Accepted
related_ids: [FR-20, FR-11, UC-06, ADR-0008, IADR-0070, IADR-0019, IADR-0079]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0008_staged-rollout.md
---

# IADR-0082: 段階遷移イベントは Worker 発行点でバス発行し中央監査へ集約する（契約は primitive・Risk 専有台帳を権威に据え置く）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-20**（段階ゲート）、**FR-11**（監査＝全イベントの時系列記録）、UC-06、ADR-0008（段階的展開）
- 対象 Issue: [#167](https://github.com/endazon/ai-stock-trading/issues/167)（段階遷移イベントのバス発行と中央監査集約・#20 後続）
- 関連 IADR: [IADR-0070](IADR-0070_stage-gate-persistence-and-approval.md)（段階ゲート永続化・本 issue の親）、[IADR-0019](IADR-0019_audit-log-service.md)（監査台帳サービス）、[IADR-0079](IADR-0079_event-backward-compat-contract-test.md)（イベント契約の後方互換）

## コンテキストと課題

段階ゲート（IADR-0070）は遷移履歴を Risk 専有 DB `stage_transitions`（追記専用）へ永続化するに留まり、中央監査台帳
`audit_events`（FR-11）には集約していない。IADR-0070 は `StageTransitioned` のバス発行を「`Shared.Contracts` への追加＋
Audit Consumer を伴うため後続へ分離」とした。本 issue はこの中央集約を実装する。設計上の論点は 3 つ:

1. **発行点をどこに置くか**（Application サービス内か Worker か）。
2. **契約でドメイン enum（`TradingStage`/`StageTransitionKind`）をどう表現するか**。
3. **Risk 専有台帳と中央監査の二重記録をどう整合させるか**。

## 決定

1. **発行点は Worker（エンドポイント）に置く。** この基盤は「Application は純粋・Worker が発行を統率」する規約に従う
   （`RiskManagementService.Application` は MassTransit 非依存。`ScreeningOutcome` はサービスが結果を返し、Worker の
   Consumer が発行する既存パターン）。`StageGateService.RequestTransition` は既に `StageTransitionResult{Accepted,
   Transition}` を返すため、`/risk-controls/stage-gate/transition` エンドポイントで **受理時のみ**
   （`result.Accepted && result.Transition is not null`）`IPublishEndpoint` で発行する。拒否時は発行しない。
   - Application サービスへ `IBus`/publisher ポートを持ち込む代替案は退けた。1 イベントのために純粋レイヤの規約を崩す
     コストに見合わず、既存の発行パターンと不整合になる。受理経路の唯一の呼び出し元はこのエンドポイントであり
     （`EvaluateWithdrawal` は遷移を確定せず提案に留める）、ここで発行すれば受理遷移を漏れなく捕捉できる。

2. **契約は primitive で表現する。** `Shared.Contracts` は Risk.Domain に依存しない（依存方向の逆転を避ける）。
   `StageTransitioned` は段階を `int`（`TradingStage` の数値割当と一致・StageSettings.cs が連続昇順を固定）で、
   種別を `string`（`nameof(StageTransitionKind)`）で保持する。追加のみで既存イベントは不変（IADR-0079 の後方互換）。
   `event-schemas.baseline.json` は `UPDATE_EVENT_BASELINE=1` で再生成し差分を PR レビューする。

3. **Risk 専有台帳を権威として据え置き、中央監査は集約ビューとする（fail-safe）。** 永続化（`ledgerStore.Append`）は
   サービス内で発行より先に完了する。バスが未到達でも `stage_transitions` は権威として保持され、承認なしの遷移は
   純ドメインが構造的に拒否する不変条件も変わらない。中央 `audit_events` は「全イベントの時系列記録」（FR-11）を
   満たす集約ビューであり、二重情報源だが役割が異なる。監査 Consumer（`StageTransitionedAuditConsumer`）を追加し、
   `AuditConsumerCoverageTests`（全イベントの監査購読を CI で要求）を緑に保つ。監査相関は注文/市場相関を持たないため
   `AuditCorrelation.From("stage-gate")` の決定的 GUID を用いる（`AssumptionsChanged` と同系）。`Symbol` は null。

## 影響 / 波及

- 追加: `Shared.Contracts.Events.StageTransitioned`、`AuditEntryFactory.From(StageTransitioned)`、
  `StageTransitionedAuditConsumer`（AuditService Worker の DI 隣接 1 行）。
- 変更: Risk `RiskControlEndpoints` の `/stage-gate/transition` を受理時発行に結線（async 化・隣接行）。
- #166（撤退ドライバ）は Risk 同一箇所を触るため、本 issue の後に回す（発行点の重複を避ける）。

## 代替案（不採用）

- **Application サービスへ publisher ポート注入**: 純粋レイヤ規約を崩す。既存の `ScreeningOutcome`/Worker 発行と不整合。
- **契約で Risk.Domain enum を直接参照**: `Shared.Contracts → Risk.Domain` の依存逆転になり不可。
- **中央監査を単一の権威にする**: Risk 専有台帳の即時一貫性・構造的不変条件（承認ゲート）を失う。fail-safe に反する。
