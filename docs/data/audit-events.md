---
title: 監査イベント（audit_events）データ仕様書
type: data-spec
status: review
created: 2026-07-10
updated: 2026-09-19
author: endazon (with Claude Code)
---
<!-- trace:
ids: [FR-04, FR-08, FR-10, FR-11, FR-12, FR-19, UC-07]
adrs: [ADR-0001, ADR-0003, ADR-0040]
iadrs: [IADR-0015, IADR-0019, IADR-0117, IADR-0342, IADR-0347]
specs: [20260710_audit-log, 20260917_819_stop-loss-method-selection, 20260918_821_s3-alternative-order-types, 20260919_848_terminal-close-approvals-release-inventory]
issues: [#17, #18, #809, #819, #821, #848]
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
  保護喪失の対処（`Remediation`）が `CloseDispatchIndeterminate` のときは、成行手仕舞いを**送信したが結果を確認できていない**
  （届いたか不明）ことを表す（#848）。要約は「解消に失敗」とは書かず「送信済みだが結果未確認・注文は重ねていない」と書く
  ——注文は証券会社側で生きているかもしれないためである。
  **この記録は同じ手仕舞い（同じ `CloseDecisionId`）について複数回残り得る**——据え置きが続くあいだ約 1 時間ごとと、
  発注執行の再起動後の最初の巡回で出し直されるためである（通知を無音にしないための再発行であり、新しい発注ではない）。
- moomoo SIMULATE で損切りの実行機構 S2（逆指値なしの建玉を許容）が選ばれていた新規建ては、保護逆指値を発注せず
  **免除の事実（`ProtectiveStopWaived`）**を記録する（#819）。種別は保護喪失（`ProtectiveStopCoverageLost`）と**別**であり、
  相関は同じくエントリーの `DecisionId` である。要約に手法（S2）・発注先・損切りラインと「逆指値なしの建玉を保持する
  （システムは決済しない）」を書く——「建玉あり ⇒ 有効な逆指値あり（または解消済み）」の読みの例外を台帳上で明示するため。
- moomoo SIMULATE で損切りの実行機構 S3（他のブローカー側注文種別）が選ばれていた新規建ては、保護レグを
  ストップリミット（または設定でトレーリングストップ）で発注し、**試行の事実（`AlternativeProtectiveStopAttempted`）**を
  **受理・拒否のどちらでも 1 件**記録する（#821）。🔴 **要約と payload に注文種別と拒否理由（`retType` / `retMsg`）を残すことが
  本記録の目的そのものである** —— 模擬取引は公式に「指値・成行のみ」とされており、断られた理由がここに無いと
  「なぜその種別が使えないのか」を後から誰も説明できない。相関はエントリーの `DecisionId`。
  結果の扱いは逆指値（既定の手法）と同一であり、試行の記録は `ProtectiveStopPlaced` / `ProtectiveStopCoverageLost` と
  **排他ではなく重ねて**残る。

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
