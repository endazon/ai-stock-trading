---
title: 監査イベント（audit_events）データ仕様書
type: data-spec
status: review
created: 2026-07-10
updated: 2026-08-21
author: endazon (with Claude Code)
---
<!-- trace:
ids: [FR-04, FR-08, FR-10, FR-11, FR-19, UC-07]
adrs: [ADR-0001, ADR-0003]
iadrs: [IADR-0015, IADR-0019]
specs: [20260710_audit-log]
issues: [#17, #18]
-->


# データ仕様書: 監査イベント（audit_events）

> 監査ログサービス（`AuditService`）が全ドメインイベントを購読して記録する追記専用の時系列台帳。
> 全イベントの時系列記録（監査）と、取引履歴・判断根拠の参照を支える実データである。
> 設計判断は「監査ログは専用サービスが全ドメインイベントを購読し追記専用台帳へ記録する」、
> 作業仕様は 仕様書: 監査ログサービス（全ドメインイベントの時系列記録） を参照する。

## 本書が受け持つ範囲

- 機能要求: 監査・時系列記録（Must）。関連するのは取引判断の判断根拠、およびリスク統制・取引ガードの拒否理由である。
- ユースケース: 取引履歴・判断根拠の参照。
- 計画 ADR: 基盤採用（Database per Service）、判断根拠の記録。

## エンティティ定義

### AuditEventRow（`audit_events`・永続・追記専用）

`AuditEntry`（Application 値オブジェクト）を正規化した監査記録。専有 DB `audit_svc` に配置する。

| 属性 | 型 | 必須 | 説明 |
| --- | --- | --- | --- |
| Id | Guid (PK) | ○ | 冪等キー＝MassTransit `MessageId`。再送で重複記録しない |
| EventType | string(64) | ○ | イベント種別（`TradeDecisionMade` / `OrderApproved` / `OrderRejected` / `OrderExecuted` / `PriceMovementDetected` / `StopLossTriggered`） |
| CorrelationId | Guid (index) | ○ | 注文系は `DecisionId`、市場系は `EventId`。注文の全体像を辿る相関キー |
| Symbol | string(32)? | | 銘柄コード（`OrderExecuted` は銘柄を持たないため null） |
| Summary | string(512) | ○ | 人間可読の一行要約（拒否理由の列挙を含む） |
| Detail | string (jsonb) | ○ | イベント全量の JSON（列挙は文字列化） |
| OccurredAt | DateTimeOffset (index) | ○ | イベント発生時刻（照会の時系列・期間の基準） |
| RecordedAt | DateTimeOffset | ○ | 監査サービスが記録した時刻 |

- インデックス: `CorrelationId`（注文単位の相関照会）、`OccurredAt`（新しい順の期間照会）。

## 照会

- `GET /audit/events/{correlationId}` — 相関単位の全記録を `OccurredAt` 昇順（時系列）で返す。
- `GET /audit/events?limit=N` — 直近の記録を `OccurredAt` 降順で返す（limit 1〜500・既定 100）。
- いずれも OwnerOnly（利用者のみ・Keycloak `trading-owner`）。監査は取引履歴＝機微情報のため RiskControl と同じ認可方針。

## 整合性・制約ルール

- 追記専用（更新・削除しない）。冪等は `Id`（=MessageId）で担保する。
- 損切りライン到達（`StopLossTriggered`）は検知の記録である（#331 の逆指値一本化により、到達を起点とする
  決済発行は廃止）。決済はエントリーと同時に発注済みの保護逆指値がブローカー側で行い、
  保護レグの記録（`ProtectiveStopPlaced` / `ProtectiveStopCoverageLost`）はエントリーの `DecisionId` を
  `CorrelationId` に採るため、エントリーから保護・解消までを同一相関で辿れる。

## 永続化方針

| 集約 | 永続化 | 実装 issue | 備考 |
| --- | --- | --- | --- |
| AuditEventRow（`audit_events`） | PostgreSQL 追記専用（専有 DB `audit_svc`） | #17（PR）| 全ドメインイベントをイベント駆動で一元記録 |

## 対象外（後続）

- 取引履歴・判断根拠の自然言語照会（基盤の RAG／ナレッジベース連携・#18）。本サービスは構造化直接照会に限定。
- LLM プロンプト／入出力ログ（実 LLM 実装時）。保持期間・パーティション・アーカイブ（運用仕様・#17 後続）。

## 関連仕様

- 作業仕様書: 仕様書: 監査ログサービス（全ドメインイベントの時系列記録）
- 実装ADR: 監査ログは専用サービスが全ドメインイベントを購読し追記専用台帳へ記録する／損切りの決済注文はスクリーニングを通さず無条件に Close 承認を発行する（`EventId` → `DecisionId` の相関）
