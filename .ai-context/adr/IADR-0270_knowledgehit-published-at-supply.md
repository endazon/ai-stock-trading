---
title: IADR-0270 KnowledgeHit の発行時刻は KnowledgeBaseWriterSink が書く publishedAt 属性を検索応答から復元して供給する
type: impl-adr
status: Accepted
related_ids: [FR-02, FR-04, FR-08, ADR-0003, IADR-0069, IADR-0072, IADR-0247]
author: endazon (with Claude Code)
created: 2026-08-29
updated: 2026-08-29
plan_refs:
  - planning:projects/ai-stock-trading/06_technical/01_architecture-overview.md
---

# IADR-0270: KnowledgeHit の発行時刻は既存の `publishedAt` 属性を検索応答から復元して供給する

- 状態: Accepted
- 日付: 2026-08-29
- 決定者: endazon（起票 #568）

## 起点・関連

- 関連する計画書 ID: FR-02・FR-04（縮退段③「古い順」）・FR-08（KB 保存・RAG 取得）・ADR-0003
- 関連する実装仕様書: [`20260829_568_knowledgehit-published-at.md`](../specs/20260829_568_knowledgehit-published-at.md)
- 先行 IADR: [IADR-0247](./IADR-0247_screening-context-degradation.md) 残余リスク
  「`RetrievedContext` に発行時刻が無く段③の『古い順』が実効になっていない」を解消する。

## コンテキストと課題

`ScreeningContextPlanner` の段③ソート（発行時刻の有無 → 発行時刻 → 関連度）自体は IADR-0247 で
実装済みだが、`KnowledgeHit`（KB 検索結果の当リポ側契約）が発行時刻を持たないため、
`ScreeningContextAssembler` は常に `PublishedAt: null` を渡していた。#565（platform `POST /documents`
が本文を受け取らない）と同じ「platform 側の欠落で計画が満たせない」型の壁に**当たっているかどうか**を、
実装前にコードを読んで確かめる必要があった。

## 検討した選択肢

1. **platform の契約（`SearchResultDto`）へ新規に発行時刻フィールドを追加してもらう**（#565 と同型の
   計画への環流）。
2. **platform 契約 `SearchResultDto.UpdatedAt`（索引の更新日時）を発行時刻として流用する**。
3. **AST 書き込み側が既に送っている ABAC 属性 `publishedAt` を、検索応答の `Attributes` から
   読み出す**（platform 側は無改修）。

## 決定

**選択肢 3 を採る。** 実測（作業仕様書「供給側は発行時刻を返せるか」参照）で以下を確認した。

1. `KnowledgeBaseWriterSink.ToDocument` は収集情報の実際の発行時刻
   （`CollectedInformation.PublishedAt`）を ABAC 属性 `publishedAt`（`"O"` 形式の文字列）として
   `KnowledgeDocument.Attributes` へ既に載せている（#568 着手前から存在。IADR-0069 決定4）。
2. platform 側は文書属性をチャンクペイロード（Qdrant のネスト構造体 `attributes -> {k: v}`）へ
   キー名を変えずに伝播し、`POST /search` の応答 `SearchResultDto.Attributes` として復元して返す
   （`IngestionService.../QdrantIngestionVectorStore.BuildChunkPayload` で書き込み、
   `RetrievalService.../QdrantVectorStore.ExtractAttributes` で読み出し。いずれも
   `OrdinalIgnoreCase`）。
3. したがって AST 側 HTTP アダプタ（`HttpKnowledgeBaseSearch`）が `SearchResultBody.Attributes` を
   デシリアライズし `publishedAt` キーを解釈するだけで、platform 側を一切改修せずに発行時刻を
   供給できる。**#565 とは異なり、達成不能な壁には当たっていない。**

**選択肢 2（`UpdatedAt` 流用）は採らない。** `SearchResultDto.UpdatedAt` は「索引（Document）の
更新日時」（platform 側の取り込み・再正規化のタイムスタンプ）であり、記事・開示の**実際の発行時刻**
とは意味が異なる。AST の取り込みは収集直後に行われるため通常は近い値になるが、遅延取り込み・
再正規化があれば乖離し、「古い順」の意味が「取り込み順」にすり替わる。**別の概念を代用しない**
（IADR-0069 が platform 契約への直接依存を避けた設計意図とも整合する——`UpdatedAt` に依存すると
platform 側の索引運用（再索引タイミング）に AST の縮退順序が結合してしまう）。

**選択肢 1（計画への環流）は不要と判断した。** 選択肢 3 で受け入れ基準を全て満たせるため、
platform 側への機能要求は生じない。

### 具体的な設計

1. `KnowledgeHit`（`KnowledgeModels.cs`）に `DateTimeOffset? PublishedAt = null` を末尾オプション引数で追加する。
   既存の位置引数構築（6 引数）はすべて無改修で通る（母集合の軸 4 で確認済み）。
2. `HttpKnowledgeBaseSearch.SearchResultBody` に `Dictionary<string, string>? Attributes` を追加し、
   `publishedAt` キー（`OrdinalIgnoreCase` 比較。platform 側と揃える）を
   `DateTimeOffset.TryParse`（`DateTimeStyles.RoundtripKind`）で解釈する。
   🔴 **fail-safe（捏造しない）**: 属性なし・キー欠落・解釈不能な値は例外化せずすべて `null` に倒す
   ——`ScreeningContextPlanner` の保守側既定（発行時刻不明＝最古扱いで先に削る）へそのまま合流する。
3. `RetrievedContext`・`KnowledgeBaseRetrievalContextProvider`・`ScreeningContextAssembler` へ
   `PublishedAt` を配線する（プランナ本体・ソート式は無改修）。

### 契約イベントへの波及確認

`KnowledgeHit` は `AiStockTrading.Shared.KnowledgeBase`（リポ内 DTO）であり、
`AiStockTrading.Shared.Contracts.Events` の契約イベントではない。実測（`grep -rn "KnowledgeHit"
backend --include=*.cs | grep -Ei "Audit|Notification|event-schema"` が 0 件、`event-schemas.baseline.json`
に記載なし）により、`AuditEntryFactory` / `AuditEventHandlers` / `AuditCycleCompletenessTests` /
`event-schemas.baseline.json` / `EventMessageTypeNameTests` / `NotificationFormatter.From` への
追随は不要と確認した。

## 理由

- platform 側は読み取り専用・pin 固定なしの依存であり（CLAUDE.md「計画リポジトリとの関係」）、
  改修を要さない解が選べるならそれを優先する。
- 既存の書き込み経路（#8/IADR-0069 決定4）が既に発行時刻を運んでいたため、読み出し側の追随だけで
  受け入れ基準を満たせた——「捏造」に当たらない（実在する属性を読むだけ）。

## 結果

- 良い影響: 縮退段③「古い順」が実効になる。platform への計画変更・環流が不要（#565 のような
  保留判断を要しない）。
- 悪い影響・トレードオフ: 発行時刻は ABAC 属性という緩やかに型付けされた経路（文字列）を通るため、
  将来 platform 契約が `publishedAt` 相当の型付きフィールドを持てば、そちらへ移行する余地を残す
  （本 IADR は現時点の最小変更を選んだだけであり、恒久設計として固定しない）。
- フォローアップ: 特になし（残余リスクを新たに持ち込まない）。

## 関連

- Supersedes: なし
- Superseded by: なし
