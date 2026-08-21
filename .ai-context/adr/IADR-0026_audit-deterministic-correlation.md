---
title: IADR-0026 注文相関を持たないイベントは自然キーから決定的 UUID（v5）で相関させる
type: impl-adr
status: Accepted
related_ids: [FR-11, UC-07]
author: endazon (with Claude Code)
created: 2026-07-10
updated: 2026-07-10
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0026: 注文相関を持たないイベントは自然キーから決定的 UUID（v5）で相関させる

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-10
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-11（監査・時系列記録）、UC-07（監査照会）
- 対象 Issue: [#17](https://github.com/endazon/ai-stock-trading/issues/17)（フォローアップ）
- 関連する実装仕様書: [20260710_audit-config-report-events](../specs/20260710_audit-config-report-events.md)
- 関連 IADR: [IADR-0019](IADR-0019_audit-log-service.md)（監査台帳・CorrelationId＝DecisionId/EventId）

## コンテキストと課題

監査台帳（IADR-0019）の `CorrelationId` は Guid で、注文チェーン（`DecisionId`）・市場検知（`EventId`）を相関キーにしている。
しかし設定変更（`AssumptionsChanged`）・報告書確定（`ReportConfirmed`）は注文チェーンの Guid 相関を持たず、自然キー
（PeriodKey 等）や種別で識別される。これらを監査台帳の Guid ベース `CorrelationId` にどう写像し、意味のある相関で照会
できるようにするかを決める必要がある（スキーマ＝Guid 列は変えたくない）。

## 検討した選択肢

1. **`Guid.Empty` を相関にする** — 実装は簡単だが、Guid 相関を持たないイベントがすべて同一相関に潰れ、照会で混ざる。
2. **`CorrelationId` を string に変更する** — 表現力は上がるが、監査スキーマ（Guid 列）・既存の全消費者・照会 API を変更する
   大きな波及。
3. **自然キーから決定的 UUID（名前ベース v5）を導出して相関にする（採用）** — スキーマ不変のまま、同一キーは同一相関・
   別キーは別相関で照会できる。実装は標準 API `Guid.CreateVersion5(namespaceId, name)` を用いる。

## 決定

**選択肢 3** を採用する。

- `AuditCorrelation.From(string key)` が **RFC 4122 名前ベース UUID（バージョン5・SHA1）** を返す。固定の名前空間 ID＋キーの
  バイト列を SHA1 でハッシュし、先頭16バイトの**バージョン（=5）・バリアント（RFC 4122）ビットを設定**する。BCL に名前ベース
  v5 生成 API が無い（`Guid.CreateVersion7` は時間ベースで用途が異なる）ため、v5 の導出を自前で実装する（バイト列の切り詰めのみで
  済ませず、バージョン/バリアントビットを正しく設定して well-formed な UUID にする）。
- 相関キーの規約: 設定変更＝`"assumptions"`（全変更が同一相関）、報告書確定＝`"report:{PeriodKey}"`（同一報告書は同一相関）。
  今後 Guid 相関を持たないドメインイベントを監査する場合も、この関数と `"<種別>:<自然キー>"` 規約で相関キーを導出する。
- 監査台帳のスキーマ（`CorrelationId` Guid 列）は不変。

## 理由

- スキーマ・既存消費者・照会 API を変えずに、意味のある相関でまとめて辿れる。決定的なので再送・再計算でも同一相関になる。
- 標準の v5 UUID は仕様準拠で、暗号関連の自前実装を持たずに済む。

## 結果

- 良い影響: 設定変更・報告書確定が意味のある相関で監査照会できる。パターンが再利用可能。
- 悪い影響・トレードオフ: `CorrelationId` の意味がイベント種別で異なる（注文系＝実 Guid、設定/報告系＝導出 Guid）。照会側は
  種別を意識する必要がある（EventType で判別可能）。名前空間 ID を変更すると過去の相関と不連続になるため固定する。
- フォローアップ: UC-07 の自然言語照会（RAG・#18）で、EventType 別の相関の意味づけを提示に反映する。

## 関連

- Supersedes: なし
- Superseded by: なし
- 関連: [IADR-0019](IADR-0019_audit-log-service.md)（監査台帳・相関）
