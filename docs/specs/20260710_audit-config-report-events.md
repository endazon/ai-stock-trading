---
title: 監査ログの購読拡張（AssumptionsChanged / ReportConfirmed を監査台帳へ記録）
type: spec
status: review
related_ids: [FR-11, UC-07, FR-17, FR-07]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: 監査ログの購読拡張（設定変更・報告書確定）

> Issue [#17](https://github.com/endazon/ai-stock-trading/issues/17)（FR-11）のフォローアップ。監査サービス（`AuditService`）が
> `AssumptionsChanged`（#19 設定変更）と `ReportConfirmed`（#14 報告書確定）を購読して監査台帳へ記録する。IADR-0019 の
> 「全ドメインイベントを購読して監査台帳へ記録する」を、後から追加されたこの 2 イベントに対して完成させる（IADR-0024 の後続明記）。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-11（全イベントの時系列記録・監査）。関連 FR-17（設定変更）・FR-07（報告書確定）
- ユースケース（UC）: UC-07（監査照会）
- ADR: なし（監査記録は FR-11、変更権限は FR-17・FR-07 の各本文が根拠）
- 関連 IADR: [IADR-0019](../adr/IADR-0019_audit-log-service.md)（監査台帳）、[IADR-0024](../adr/IADR-0024_report-confirmation-and-policy.md)（後続明記）、本作業で新規 [IADR-0026](../adr/IADR-0026_audit-deterministic-correlation.md)（決定的相関 UUID）
- 対象 Issue: #17（フォローアップ）

## 目的・背景

`AssumptionsChanged`（#19・PR #66）と `ReportConfirmed`（#14・PR #69）は AuditService の後に追加されたため、監査台帳に
記録されていない。報告書確定＝取引方針の有効化・前提条件変更は監査上重要なため、これらを購読して記録する。両イベントは
注文チェーンの Guid 相関（DecisionId/EventId）を持たないため、自然キーから決定的 GUID を `CorrelationId` に導出する。

## 対象範囲（`AuditService`）

- `AuditEntryFactory`（純関数）に写像を追加:
  - `From(AssumptionsChanged)`: `CorrelationId` = 決定的 GUID（"assumptions"）、Summary＝バージョン・アクター・理由、Detail＝全量 JSON、OccurredAt＝ChangedAt。
  - `From(ReportConfirmed)`: `CorrelationId` = 決定的 GUID（"report:{PeriodKey}"）、Summary＝種別・PeriodKey・アクター・前提バージョン、OccurredAt＝ConfirmedAt。
- `AuditCorrelation.From(string)`（決定的 GUID 導出・SHA1 ベース。同一キーは同一相関で照会できる）。
- Consumer 追加: `AssumptionsChangedAuditConsumer` / `ReportConfirmedAuditConsumer`（既存パターン・MessageId 冪等）。
- `Program.cs` / `AuditWorkerWebApplicationFactory` に登録。

## 受け入れ基準

CI で緑にする範囲（ユニット＋MassTransit テストハーネス＋EF InMemory）:
- [ ] `AssumptionsChanged`／`ReportConfirmed` を購読すると監査台帳に記録される（EventType・CorrelationId・OccurredAt）。
- [ ] `ReportConfirmed` は同一 PeriodKey で同一 CorrelationId、`AssumptionsChanged` は共通の CorrelationId で照会できる。
- [ ] 同一 MessageId の再送は重複記録しない（既存の冪等）。
- [ ] 既存テストを緑に保つ。

## 対象外（後続）

- UC-07 自然言語照会（RAG・#18）、LLM プロンプト/入出力ログ、保持期間（#17 本体の対象外を踏襲）。

## テスト方針

- `AuditEntryFactory` の 2 写像を単体検証（EventType・相関・要約・アクター）。
- Consumer は MassTransit ハーネス＋InMemory 台帳で記録を検証。

## 関連仕様

- 先行: [20260710_audit-log](20260710_audit-log.md)（監査サービス本体）
- 連携元: [20260710_configuration-assumptions](20260710_configuration-assumptions.md)、[20260710_report-confirmation](20260710_report-confirmation.md)
