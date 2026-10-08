---
title: 報告書の監査台帳の記録（AuditLedgerEntry）に発生時刻 OccurredAt を運び、本文を復元できなかった承認の記録もセッションの窓で 1 回だけ数える（#1255）
type: spec
status: accepted
related_ids: [FR-06, FR-10, UC-03, UC-04, UC-05, ADR-0053, ADR-0040, IADR-0516, IADR-0445, IADR-0427]
author: claude (Claude Code)
created: 2026-10-08
updated: 2026-10-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0053 (決定 2 前回の同種の報告書の生成の後から今回の生成までに閉場したセッションを集計)
  - planning:projects/ai-stock-trading/06_technical/04_report-templates (日報 §4・月報 §6 損切りの実行機構・復元できなかった承認の記録)
---

# 本文を復元できなかった承認の記録をセッションの窓で数える（#1255）

## 背景（issue の観測）

- IADR-0516（#1224・PR #1254）は監査台帳から期間の集計として引く入力をセッションの窓に揃えた。供給元へは窓を覆う JST の暦日の外包で照会し、
  受け取った後に `ReportLedgerWindowing.Within` で絞る。
- 本文を復元できなかった承認の記録の数（`StopLossMethodUsage.UnreadableCount`）は時刻を持たない。報告書が受け取る台帳の記録
  `AuditLedgerEntry(Id, EventType, Detail)` が発生時刻を運ばないためである。そのため外包の範囲で数え、**隣り合う報告書の両方に同じ記録が数えられ得る**
  （IADR-0516 §残余リスク・独立監査 🟡-1）。

## 実測（origin/develop 15f4d4f8）

- 提供側（AuditService）:
  - REST `GET /audit/events/by-type` は `AuditEntry` 全体（`OccurredAt`・`RecordedAt` を含む）を JSON で返す。**時刻は既に線に乗っている**。
  - gRPC `AuditEventsRead/GetEventsByType` の `LedgerRecord` は `id`・`event_type`・`detail` の 3 項目だけ（`optional`）。**時刻を運ばない**。
  - 照会の期間は `OccurredAt` の半開区間で絞る（`EfAuditEventStore.GetByTypesInPeriod`）。
- 受け手（ReportService）: `AuditLedgerEntry` は `internal sealed record`（共有契約ではない）。REST の 6 供給元は `ReadFromJsonAsync<IReadOnlyList<AuditLedgerEntry>>`、
  gRPC は `AuditGrpcTransport.ToEntry` で写す。どちらも `Http*Source.Build` の共有の解釈へ渡す。
- proto の後方互換は `scripts/check-proto-contracts.js` が `scripts/proto-contract-baseline.json` と比べる（フィールド追加は非破壊だが差分がある限り
  baseline の更新が要る）。イベントの共有契約（`Shared.Contracts`）は触らない。

## 決定（IADR-0516 への 2026-10-08 追記）

1. **発生時刻（`OccurredAt`。照会の絞り込みと同じ列）を運ぶ。**
   - REST: `AuditLedgerEntry` に `DateTimeOffset? OccurredAt = null` を足す（応答の `occurredAt` をそのまま読む。契約の変更なし）。
   - gRPC: `LedgerRecord` に `optional string occurred_at = 4` を足す（往復書式 `o`。フィールド追加＝非破壊）。提供側は `AuditEntry.OccurredAt` を書き、
     受け手は在れば解析する。**在るのに読めない**値は id と同じく原則 A（応答全体を未供給）。**無い**（旧版の提供側）は `null`。
2. **本文を復元できなかった承認の記録は、発生時刻（市場を持たない記録と同じ `ReportSessionWindow.Contains`）で窓に入る報告書に数える。**
   本文が読めないので市場は分からない（IADR-0516 決定 1 の「市場を持たない記録」の形）。
   - `StopLossMethodUsage` に `UnreadableOccurredAt`（復元できなかった記録ごとの時刻。`null` は時刻なし）を足す。
   - 時刻の無い記録（旧版の提供側）は**従来どおり外包の範囲で数える**（黙って 0 件にしない）。時刻の列が件数と食い違う値（件数だけで作った旧い値）は絞らない。
3. 損切りの手法の解決（`StopLossMethodResolutionFeed.UnreadableCount`）は対象外（IADR-0516 決定 2 で承認に DecisionId で従い、窓で絞らない入力。
   復元できなかった解決の数は比較の注記であり、本 issue の受け入れ基準の外）。

## 範囲

1. proto `audit_events_read.proto`（`occurred_at` の追加）・`scripts/proto-contract-baseline.json`（`--update`）。
2. AuditService: `AuditReadWireMapping.ToProto` が `occurred_at` を書く。
3. ReportService: `AuditLedgerEntry`・`AuditGrpcTransport.ToEntry`・`HttpStopLossMethodUsageSource.Build`・`StopLossMethodUsage`・`ReportLedgerWindowing.Within`。
4. 文書: IADR-0516 への日付つき追記（§残余リスクの #1255 の項を解消）・索引行の追記・データ仕様書（報告書）・本仕様書。

範囲外: 他の 5 供給元の解釈（時刻を持つ本文から配置する。`OccurredAt` は読まない）・解決の復元できなかった数（決定 3）・OrderExecutionService（#1253 の作業領域）。

## 母集合（規則 9・10）

- 誤りの側の文字列「記録時刻を運ば」「3 項目」で走査（`git grep`。旧い注記の文言そのもの）:

| 箇所 | 扱い |
| --- | --- |
| `ReportLedgerWindowing.cs` の `Within(StopLossMethodUsage…)` の注記（「記録時刻を運ばない…#1255 で運ぶ」。旧い文言） | **直す** |
| `AuditLedgerEntry.cs`（「報告書が読む 3 項目」） | **直す**（4 項目） |
| 本 PR 自身が `OccurredAt` を「記録時刻」と呼んだ箇所（独立監査 🟡-1。`AuditEntry` の定義では `OccurredAt`＝発生時刻・`RecordedAt`＝記録時刻） | **直す**（発生時刻（`OccurredAt`）へ統一） |
| `audit_events_read.proto`（「報告書が読む 3 項目」） | **直す** |
| `AuditEventsReadGrpcService.cs` の `AuditReadWireMapping`（「運ぶのは報告書が読む 3 項目だけ」） | **直す** |
| IADR-0516 §残余リスク（#1255 の項）・索引行の残余 | 凍結記録のため本文は変えず**日付つき追記** |
| `.ai-context/specs/20261008_1224_…`（範囲外の行） | 凍結記録のため変えない |
| `ReportRenderer`・`PolicyRevisionProposal`・`TradeDecisionPromptBuilder` ほかの「3 項目」 | 別の意味（対象外） |

- 規則 10: 本変更で新たに誤りになり得る記述＝「時刻を持たない記録は窓で絞らない」系の注記（上表で直す）と、T-10-1673（REST と gRPC の同値）—— 時刻を両経路で運ぶので
  同値は保たれる（試験で確かめる）。

## 受け入れ基準 → 試験

| ID | 受け入れ基準 | 試験 |
| --- | --- | --- |
| T-06-073 | REST・gRPC の両経路で `AuditLedgerEntry` が台帳の `OccurredAt` を同じ値で保持する。提供側は `occurred_at` を書く。gRPC の `occurred_at` が無ければ `null`、読めなければ応答全体を未供給 | `GrpcAuditLedgerSourcesTests`・`HttpStopLossMethodUsageSourceTests`・`AuditReadWireMappingTests` |
| T-06-074 | 本文を復元できなかった承認の記録は、発生時刻が窓に入る日報のちょうど 1 つに数えられる（隣り合う日報の両方には数えない） | `ReportLedgerWindowingTests` |
| T-06-075 | 月報のこの行（復元できなかった承認の記録の数）は、その月の日報の和に等しい | 同上 |
| T-06-076 | （否定形）発生時刻を欠く応答（旧版の台帳）の記録は従来どおり外包の範囲で数える（0 件にしない）。件数だけの旧い値も絞らない | 同上 |

## 残余リスク

- 配備順の窓: 提供側（AuditService）が旧版の間、gRPC は `occurred_at` を運ばない。読めない記録は時刻なしとして外包の範囲で数え、隣り合う報告書の両方に出得る（従来どおり）。
- ［独立監査 🟡-2］本文の読める承認は市場の形（米国は `max(大引け, ApprovedAt)`）で、読めない承認は発生時刻（`OccurredAt`）で窓に入れる。**同じ瞬間の承認でも
  読めるか否かで載る日報が違い得る**（例: 米国の寄り付き前＝JST 13:00〜16:00 の承認は、読めれば翌日報、読めなければ当日の日報）。同じ種別の報告書のちょうど 1 つに
  入る性質は保つ。根本の是正（台帳の記録に市場を運ぶ）は範囲外。
- 損切りの手法の解決の復元できなかった数は窓で絞らない（決定 3）。

## 検証

- `dotnet build backend/backend.slnx`（警告 0）・`dotnet format --verify-no-changes`・`ReportService.Tests`・`AuditService.Tests`。
- `node scripts/scripts.test.js`・`check-proto-contracts`・`check-trace-blocks`・`gen-knowledge-graph --check`・`check-test-traceability`・`check-adr-index-sync`・
  `check-adr-index-addendum-loss`・`check-cross-repo-refs`・`check-commit-messages`。
