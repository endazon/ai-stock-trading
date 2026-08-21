---
title: IADR-0019 監査ログは専用サービスが全ドメインイベントを購読し追記専用台帳へ記録する
type: impl-adr
status: Accepted
related_ids: [FR-11, UC-07, ADR-0001, ADR-0003]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
---

# IADR-0019: 監査ログは専用サービスが全ドメインイベントを購読し追記専用台帳へ記録する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-11（全イベントの時系列記録・監査）、UC-07（取引履歴の参照）、ADR-0001（Database per Service）、ADR-0003
- 対象 Issue: [#17](https://github.com/endazon/ai-stock-trading/issues/17)（Slice A）
- 関連する実装仕様書: [20260710_audit-log](../specs/20260710_audit-log.md)

## コンテキストと課題

FR-11 は「すべての収集・判断・発注・通知イベントを時系列ログとして記録し、後から監査できる」ことを要求する（Must）。
現状、監査で使う契約（`RejectionReason`・`OrderRejected` 等）はあるが記録・照会の実装がない。記録の**所有権と配置**を
Database per Service（ADR-0001）と整合する形で決める必要がある。また可観測性スタック（OTel/Loki）との役割分担も曖昧。

## 検討した選択肢

1. **各サービスが自サービス DB に自イベントを記録する** — Database per Service に純粋だが、「注文の全体像（いつ・何を根拠に・
   何をした）」を辿るのにサービス横断の集約が必要になり、UC-07 の照会が困難。監査の一元的な追跡性が損なわれる。
2. **可観測性スタック（OTel/Loki）に集約ログとして残す** — 運用テレメトリには適するが、業務監査（構造化・長期保持・
   照会・説明責任）には保持保証・構造化照会が弱く、FR-11 の「後から辿れる」要件に対して不十分。
3. **専用の監査サービスが全ドメインイベントを購読し、専有 DB の追記専用台帳へ記録する（採用）** — イベント駆動で疎結合に
   全量を一元記録でき、`DecisionId` 相関で注文単位の追跡が容易。Database per Service（監査サービス専有 DB）にも適合。

## 決定

**選択肢 3** を採用する。

- **新規サービス `AuditService`**（Application + Worker。監査に固有ドメインロジックが無いため Domain 層は設けない）を追加する。
- 全ドメインイベント（`PriceMovementDetected`／`StopLossTriggered`／`TradeDecisionMade`／`OrderApproved`／`OrderRejected`／
  `OrderExecuted`）を購読し、共通形 `AuditEntry` に正規化して追記専用台帳（`audit_svc` 専有 DB）へ記録する。
- **相関キー** `CorrelationId` は注文系イベントの `DecisionId`、市場系イベントの `EventId` を用いる。損切り機械執行は
  `StopLossTriggered.EventId` が後続の `DecisionId` になる（IADR-0015）ため、市場検知から決済までを同一相関で辿れる。
- **冪等キー**は MassTransit の `MessageId` とし、再送で重複記録しない（`Id` を PK に採用）。
- **照会**は OwnerOnly（利用者のみ・Keycloak `trading-owner`）の HTTP エンドポイントで、注文単位（相関）・期間で提供する。
  監査台帳は取引履歴＝機微情報のため、RiskControl と同じ認可方針に揃える。
- **OTel/Loki との役割分担**: 監査台帳＝**業務イベントの永続・構造化照会・説明責任**（長期・照会可能）。OTel/Loki＝
  **運用テレメトリ**（トレース・メトリクス・運用ログ）。両者は目的が異なり、監査を可観測性スタックで代替しない。

## 理由

- イベント駆動の一元記録は疎結合（各サービスは監査を意識せず発行するだけ）で、全量記録と `DecisionId` 相関の追跡性を両立する。
- 専有 DB は ADR-0001 に適合し、監査の保持・照会を業務要件として独立管理できる。

## 結果

- 良い影響: 注文の全体像を相関で辿れ、拒否理由も記録・照会できる（FR-11 の受け入れ基準を満たす）。
- 悪い影響・トレードオフ: 監査サービスが全イベントを購読するため、契約変更時に写像（`AuditEntryFactory`）の追随が要る。
  `Detail` はイベント全量 JSON で保持するため、契約追加時も最低限の記録は維持されるが、構造化照会したい属性は写像拡張が必要。
- フォローアップ: UC-07 の自然言語照会（基盤 RAG／KB 連携・FR-08・#18）、LLM プロンプト/入出力ログ（実 LLM 実装時）、
  保持期間・パーティション・アーカイブ（運用仕様・#17 後続）。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0015](IADR-0015_stop-loss-mechanical-close.md)（EventId→DecisionId の相関）、[IADR-0018](IADR-0018_portfolio-ledger-projection.md)（追記専用台帳パターン）
