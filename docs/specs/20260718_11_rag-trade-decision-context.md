---
title: RAG 文脈の TradeDecision 結線（収集情報＋KB 取得を判断プロンプトへ注入・既定は文脈なし・opt-in）
type: spec
status: review
related_ids: [FR-04, FR-08, FR-11, UC-01, UC-02, ADR-0003, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0003_ai-decision-guardrails.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# 仕様書: RAG 文脈の TradeDecision 結線

> Issue [#11](https://github.com/endazon/ai-stock-trading/issues/11)（取引判断・FR-04）の**残スコープ**。
> #11 の実 LLM 結線（[IADR-0061](../adr/IADR-0061_llm-production-wiring.md)）は develop マージ済み。RAG 文脈は #18
> （[IADR-0069](../adr/IADR-0069_knowledge-base-rag-foundation.md)）の RAG 取得ポート `IKnowledgeBaseSearch` を待って残していた。
> #18 が develop に入ったため、本作業でこれを TradeDecision の判断文脈に結線する。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: **FR-04**（LLM による売買判断は確定日報の方針・リスク制約の範囲内で行う）、
  **FR-08**（収集情報・判断根拠を platform ナレッジベースへ保存し **RAG 検索に利用**）、FR-11（判断根拠の記録）
- ユースケース（UC）: UC-01（定時サイクル）、UC-02（価格変動トリガー）
- ADR: **ADR-0003**（AI 判断ガードレール＝確定日報の方針とリスク制約の範囲内のみ）、**ADR-0001**（platform 再利用・基盤無改修）
- 関連 IADR: [IADR-0061](../adr/IADR-0061_llm-production-wiring.md)（実 LLM 結線・全量ログ `LogPrompts`）、
  [IADR-0069](../adr/IADR-0069_knowledge-base-rag-foundation.md)（RAG 取得ポート `IKnowledgeBaseSearch`・既定 no-op・opt-in）、
  [IADR-0039](../adr/IADR-0039_decision-orchestration.md)（多数決・二段オーケストレーション）、
  [IADR-0028](../adr/IADR-0028_daily-policy-sync-api.md)（確定日報方針の同期照会）。
  本作業で新規 **[IADR-0072](../adr/IADR-0072_rag-trade-decision-context.md)**。
- 対象 Issue: #11（本体）／前提 #18（マージ済み）

## 背景・課題

FR-08 は KB への保存に加え「**RAG 検索に利用**」を求める。現状の TradeDecision は確定日報の方針（`DailyPolicy.Summary`）・
トリガー（銘柄/市場/価格変動）・リスク制約のみを `TradeDecisionPromptBuilder` でプロンプト化しており、**過去の収集情報・判断根拠を
ナレッジベースから引いて判断文脈に加える経路が無い**。#18 で RAG 取得ポート `IKnowledgeBaseSearch`（fail-safe・既定 no-op・
`KnowledgeBase:Search:BaseUrl` で opt-in）が用意されたので、これを判断プロンプトへ注入する。

なお `InformationCollected` イベント（定時サイクルの起点）は件数のみを運ぶ（本文を運ばない）。収集した情報の実体は KB に保存される
（#9/#18）ため、「収集情報を判断に使う」正しい経路は **RAG 取得＝KB 検索**である。本作業はその結線に相当する。

## スコープ（このPRで実装するもの）

TradeDecisionService に閉じる（`Shared.KnowledgeBase` は変更せず利用のみ、`Shared.Contracts` は変更なし）。

1. **Application 層の抽象ポート** `IRetrievalContextProvider`（+ 値 `RetrievedContext`）と既定 `NoOpRetrievalContextProvider`
   （常に空＝現行動作）。Application 層は `Shared.KnowledgeBase` に直接依存せず、既存の他ポート（`IDailyPolicyProvider` 等）と
   同じ「Application ポート／Worker アダプタ」分離を踏襲する。
2. **`TradeDecisionPromptBuilder.Build`** に取得文脈（`IReadOnlyList<RetrievedContext>`）を渡し、非空なら
   「# 参考情報（ナレッジベース）」節を追記する（ADR-0003: **参考情報**であって方針・制約を上書きしない旨を明記）。
   一次スクリーニング（`BuildScreening`）は**据え置き**（費用統制。軽量モデルの絞り込みに RAG 文脈は不要）。
3. **`TradeDecisionService`** は `IRetrievalContextProvider` を**任意依存**で受け（未指定＝NoOp）、判断前に取得して `Build` へ渡す。
   取得失敗・空は判断を止めず「文脈なし」に縮退する（呼び出し点で例外を握り潰し、既存の判断フローを継続）。
4. **Worker 層のアダプタ** `KnowledgeBaseRetrievalContextProvider`（#18 `IKnowledgeBaseSearch` を包む）。
   trigger（銘柄/市場）＋ policy 要約から `KnowledgeQuery` を構築し、`KnowledgeHit` を `RetrievedContext` に写像する。
   `AddAiStockTradingKnowledgeBase(config)` で配線し、既定（`Search:BaseUrl` 未設定）は NoOp 検索＝空＝文脈なし＝現行動作。

## スコープ外（後続 Issue の境界＝本 PR に含めない）

- **実 KB 取得の疎通・E2E**（実 RetrievalService への `POST /search`、実データでの検索）は実基盤依存のため #82 系の実コンテナ基盤に乗せる。
  CI は外部接続なしで緑（既定 no-op・NoOp 検索でユニット検証）。
- **利用者スコープ（ABAC `Scope`）の伝播**は #18 で未送出のまま（後続）。本作業は検索クエリ＋文脈注入に閉じる。
- **KB 保存側**（#9 実 KB 保存 / #14 報告書保存）には触れない。

## 受け入れ基準 → テスト写像

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | 取得文脈が非空なら「# 参考情報（ナレッジベース）」節がプロンプトに含まれる（＝全量ログ `LogPrompts` にも載る） | `TradeDecisionPromptBuilderTests` |
| 2 | 取得文脈が空なら参考情報節を出さない（既定＝現行動作と同一プロンプト） | `TradeDecisionPromptBuilderTests` |
| 3 | 一次スクリーニングプロンプトは RAG 文脈を含まない（費用統制・据え置き） | `TradeDecisionPromptBuilderTests` |
| 4 | `TradeDecisionService` は既定（NoOp 取得）で従来どおり判断する（RAG 未結線と等価） | `TradeDecisionServiceTests` |
| 5 | `IRetrievalContextProvider` が結果を返すと判断プロンプトに文脈が渡る（取得ポートが呼ばれる） | `TradeDecisionServiceTests` |
| 6 | 取得ポートが例外を投げても判断は止まらず文脈なしで継続する（fail-safe） | `TradeDecisionServiceTests` |
| 7 | `KnowledgeBaseRetrievalContextProvider` が trigger+policy から `KnowledgeQuery` を作り `KnowledgeHit` を写像する | `KnowledgeBaseRetrievalContextProviderTests` |
| 8 | `KnowledgeBase:Search:BaseUrl` 未設定なら NoOp 検索＝空取得＝文脈なし（Worker DI の既定） | `RetrievalContextProviderSelectionTests` |

## 完了条件（Definition of Done 抜粋）

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` 緑、`dotnet format` 済、警告ゼロ。
- 新イベント追加なし（監査 Consumer 変更不要）。`Shared.Contracts`・`Shared.KnowledgeBase` 変更なし。
- 既定挙動（RAG 未設定＝文脈なし）不変 ＝ 実 LLM 結線（IADR-0062）の挙動を変えない。
- 設定キー追加（`KnowledgeBase:Search:*`・`Retrieval:TopK` 等）は PR 末尾の単一コミットに閉じる。
- IADR-0072 に境界（取得クエリ設計・スクリーニング据え置き・fail-safe・実基盤依存の後続）を明記。
