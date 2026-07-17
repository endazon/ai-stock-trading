---
title: IADR-0072 RAG 文脈は Application 抽象ポートで受け、本判断プロンプトのみに参考情報として注入し、既定 no-op・取得失敗は文脈なしへ縮退する
type: impl-adr
status: Accepted
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

# IADR-0072: RAG 文脈は Application 抽象ポートで受け、本判断プロンプトのみに参考情報として注入し、既定 no-op・取得失敗は文脈なしへ縮退する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-04**（LLM 判断は確定日報の方針・リスク制約の範囲内）、**FR-08**（収集情報・判断根拠を KB へ保存し
  **RAG 検索に利用**）、FR-11（判断根拠の記録）、UC-01/02、**ADR-0003**（AI 判断ガードレール）、**ADR-0001**（platform 再利用・基盤無改修）
- 対象 Issue: [#11](https://github.com/endazon/ai-stock-trading/issues/11)（取引判断・残スコープ「RAG 文脈」）／前提 [#18](https://github.com/endazon/ai-stock-trading/issues/18)（マージ済み）
- 関連する実装仕様書: [20260718_11_rag-trade-decision-context](../specs/20260718_11_rag-trade-decision-context.md)
- 関連 IADR: [IADR-0069](IADR-0069_knowledge-base-rag-foundation.md)（RAG 取得ポート `IKnowledgeBaseSearch`・既定 no-op・opt-in ＝本作業で利用）、
  [IADR-0061](IADR-0061_llm-production-wiring.md)（実 LLM 結線・全量ログ `LogPrompts`・タイムアウト構成化）、
  [IADR-0039](IADR-0039_decision-orchestration.md)（多数決・二段オーケストレーション＝スクリーニングの費用統制）、
  [IADR-0028](IADR-0028_daily-policy-sync-api.md)（確定日報方針の同期照会＝Application ポート／Worker アダプタ分離の先例）、
  [IADR-0049](IADR-0049_integration-e2e-foundation.md)（CI と実基盤依存テストの切り分け）

## 背景・課題

FR-08 は「収集情報・判断根拠を KB へ保存し **RAG 検索に利用**」を求める。#11 の実 LLM 結線（IADR-0061）で判断は
`TradeDecisionPromptBuilder` が確定日報の方針・トリガー・リスク制約からプロンプトを組んで LLM ゲートウェイへ送るところまで到達したが、
**過去の収集情報・判断根拠を KB から引いて判断文脈へ加える経路が無い**。#18（IADR-0069）で RAG 取得ポート `IKnowledgeBaseSearch`
（fail-safe・既定 no-op・`KnowledgeBase:Search:BaseUrl` で opt-in）が用意されたので、これを判断プロンプトへ結線する。

補足: 定時サイクルの起点 `InformationCollected` イベントは**件数のみ**を運ぶ（本文を運ばない）。収集情報の実体は KB に保存される
（#9/#18）ため、「収集情報を判断に使う」正しい経路は **RAG 取得（KB 検索）**であり、本作業がその結線に相当する。

## 決定

### 1. RAG 文脈は Application 抽象ポート `IRetrievalContextProvider` で受ける（`Shared.KnowledgeBase` へ直接依存しない）

Application 層に `IRetrievalContextProvider`（`GetContextAsync(trigger, policy, ct) → IReadOnlyList<RetrievedContext>`）と
値型 `RetrievedContext(Title, Text, SourceUri, Score)`、既定実装 `NoOpRetrievalContextProvider`（常に空）を置く。
`IKnowledgeBaseSearch`（`Shared.KnowledgeBase`）を包む実アダプタ `KnowledgeBaseRetrievalContextProvider` は **Worker 層**に置く。

- 理由: 既存の `IDailyPolicyProvider`／`ISizingContextProvider`（IADR-0028/0029）と同じ「Application ポート／Worker アダプタ」
  分離を踏襲し、Application/Domain を外部クライアントの契約変化から隔離する（ADR-0001 疎結合）。テストも Application 層で完結する。

### 2. 注入は本判断プロンプトのみ。一次スクリーニングは据え置き（費用統制）

`TradeDecisionPromptBuilder.Build` にのみ取得文脈を渡し、非空のとき「# 参考情報（ナレッジベース）」節を追記する。
`BuildScreening`（一次スクリーニング）は**変更しない**。

- 理由: IADR-0039 の一次スクリーニングは軽量モデルで「本判断に値するか」を絞り込む費用統制の関門であり、RAG 文脈でトークンを
  膨らませる価値が薄い。RAG は二次本判断（高性能モデル）でこそ効く。ガードレール（ADR-0003）上も、参考情報は本判断で評価すれば足りる。

### 3. RAG 文脈は「参考情報」であって方針・制約を上書きしない（ADR-0003 ガードレール）

参考情報節の冒頭に「以下は参考情報であり、確定日報の方針とリスク制約を上書きしない。矛盾・不確実な場合は Hold」を明記する。
各ヒットは `Title` / 本文（`MaxSnippetChars` で切り詰め）/ 出典を列挙する。

- 理由: ADR-0003 は「確定日報の方針とリスク制約の範囲内でのみ判断」を要求する。RAG で引いた過去情報が方針を書き換えないよう、
  プロンプト上の位置づけを明示する（プロンプトインジェクション耐性ではなく、判断の権威順序の明示）。

### 4. 既定は文脈なし（現行動作）、取得は opt-in、取得失敗・空は判断を止めず縮退する（fail-safe）

- `TradeDecisionService` は `IRetrievalContextProvider` を**任意依存**（既定 `NoOpRetrievalContextProvider`）で受ける。既存の
  5 引数コンストラクタ呼び出し（テスト・DI）はそのまま動く。
- Worker では `AddAiStockTradingKnowledgeBase(config)` で `IKnowledgeBaseSearch` を配線し、`KnowledgeBase:Search:BaseUrl`
  未設定/不正なら #18 の `NoOpKnowledgeBaseSearch`（空）が選ばれる → 取得空 → 参考情報節なし → **実 LLM 結線（IADR-0061）と同一プロンプト**。
- 取得ポート呼び出しは `TradeDecisionService` 側でも try/catch し、例外は「文脈なし」に縮退して判断を継続する（#18 アダプタ自体も
  fail-safe だが、独自アダプタ差し替え時の保険として判断境界でも握る）。

### 5. 検索クエリは trigger（銘柄/市場）＋確定日報方針要約から組む。ABAC Scope は本作業では送らない

`KnowledgeBaseRetrievalContextProvider` は `KnowledgeQuery(Query, TopK)` を「`{Symbol} {Market} {policy.Summary}`」で構築する。
`TopK` は `Retrieval:TopK`（既定 5・不正/非正値は既定へ）。`Scope`（ABAC 利用者スコープ）は #18 同様に本作業では送らない（後続）。

- 理由: 収集情報・判断根拠は銘柄・当日方針に紐づくため、この 3 要素が最も関連文書を引ける最小クエリ。Scope 伝播は #18 の申し送りに従う。

## スコープの境界（後続 Issue への申し送り）

本 PR は **RAG 取得ポートの結線と本判断プロンプトへの文脈注入まで**であり、以下は含めない（`Refs #11`）:

- **実 KB 取得の疎通・E2E**: 実 RetrievalService への `POST /search`、実データでの検索・関連度検証は実基盤依存のため #82 系の
  実コンテナ基盤に乗せる（[IADR-0049] の切り分け。CI は外部接続なしで緑＝NoOp 検索でユニット検証）。
- **ABAC `Scope`（利用者スコープ）の伝播**: #18 の申し送りどおり後続。
- **KB 保存側**（#9 実 KB 保存 / #14 報告書保存）には触れない（本 PR は取得のみ）。

## 検討した代替案

- **A: `TradeDecisionService` から `IKnowledgeBaseSearch` を直接呼ぶ** — 却下。Application 層が `Shared.KnowledgeBase` の
  クエリ／ヒット DTO に直結し、既存のポート／アダプタ分離（IADR-0028/0029）と不揃いになる。抽象ポートで包む。
- **B: 一次スクリーニングにも RAG 文脈を入れる** — 却下。軽量モデルの絞り込みをトークンで膨らませ費用統制（IADR-0039）に逆行する。
- **C: `InformationCollected` イベントに収集本文を載せて渡す** — 却下。`Shared.Contracts` の破壊的変更（監査 Consumer 波及）で、
  かつ本文の権威は KB。RAG 取得で引くのが FR-08 の趣旨に合致する。
- **D: RAG を既定オンにする** — 却下。実接続は実 RetrievalService・実データ前提で、既定オンは未検証経路を本番判断に載せる。安全既定に反する。

## 影響・リスク

- 既定挙動は完全に不変（`Search:BaseUrl` 未設定＝NoOp＝空取得＝文脈なし＝IADR-0061 と同一プロンプト）。
- `Shared.Contracts`・`Shared.KnowledgeBase` は不変（新イベントなし・監査 Consumer への影響なし）。
- 実接続時、RAG 文脈が全量ログ（`LogPrompts`＝既定オフ）に載る。プロンプトは既に機微（残枠・方針）を含むため区分は不変。
- 実 RAG のヒット品質・トークン費用は実基盤・実データ検証（#82 系）で確認する後続事項。
