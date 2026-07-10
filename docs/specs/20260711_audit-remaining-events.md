---
title: 監査購読の残り 2 イベント（CostThresholdReached / InformationCollected）を監査台帳へ記録
type: spec
status: review
related_ids: [FR-11, NFR, FR-01]
author: endazon (with Claude Code)
created: 2026-07-11
updated: 2026-07-11
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: 監査購読の残り 2 イベントを監査台帳へ記録する

> Issue [#80](https://github.com/endazon/ai-stock-trading/issues/80)（`Refs #17`）。監査ログ（FR-11「全イベントの時系列記録」）で
> 未購読だった 2 イベント（`CostThresholdReached`・`InformationCollected`）を AuditService が購読し `audit_events` 台帳へ記録する。
> 既存の監査設計（[IADR-0019](../adr/IADR-0019_audit-log-service.md)・[IADR-0026](../adr/IADR-0026_audit-deterministic-correlation.md)）を
> そのまま適用する（新規 IADR 不要）。

## 起点となる計画書・課題（トレーサビリティ）

- FR-11（Must）: すべての収集・判断・発注・通知イベントを時系列ログとして記録し監査できる。UC-07。
- 課題: 発行済みイベント 10 種のうち `CostThresholdReached`（#23）・`InformationCollected`（#9）が未購読（未監査）だった（#22 棚卸しで発見）。
- 関連 IADR: IADR-0019（監査台帳）、IADR-0026（決定的相関 v5 UUID）。対象 Issue: #80（`Refs #17`）。

## 対象範囲（AuditService）

- `AuditEntryFactory.From` に 2 オーバーロードを追加。
  - `CostThresholdReached` → 相関 `AuditCorrelation.From("cost:{Month}:{Category}")`（月×カテゴリで集約）・Symbol=null。
  - `InformationCollected` → 相関 `EventId`（市場系と同様）・Symbol=null。
- `CostThresholdReachedAuditConsumer` / `InformationCollectedAuditConsumer` を追加し Program.cs で購読登録。
- 再発防止: 全 `Shared.Contracts.Events` イベントに監査 Consumer が存在することをリフレクションで検証する
  カバレッジテスト（新規イベント追加時の追随漏れを CI で検知）。

## 受け入れ基準

- [ ] `CostThresholdReached` / `InformationCollected` が `audit_events` 台帳へ記録される（写像・相関）。
- [ ] 全イベントに監査 Consumer が存在することを保証するカバレッジテストがある。
- [ ] 既存の監査テストを緑に保つ。

## 対象外（後続）

- UC-07 自然言語照会（RAG・#18）、LLM プロンプト/入出力ログ（実 LLM・#11）、保持期間/アーカイブ（運用）。

## 関連仕様

- 連携元: [20260710_audit-log](20260710_audit-log.md)（AuditService）
- 実装ADR: [IADR-0019](../adr/IADR-0019_audit-log-service.md) / [IADR-0026](../adr/IADR-0026_audit-deterministic-correlation.md)（既存を適用）
