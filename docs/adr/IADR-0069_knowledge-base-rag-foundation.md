---
title: IADR-0069 KB 保存・RAG 取得は共有クライアントの疎な境界で platform 文書管理／検索を包み、既定 no-op・構成で opt-in とする
type: impl-adr
status: Accepted
related_ids: [FR-08, FR-01, FR-06, UC-03, UC-04, UC-06, UC-07, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# IADR-0069: KB 保存・RAG 取得は共有クライアントの疎な境界で platform 文書管理／検索を包み、既定 no-op・構成で opt-in とする

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-18
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-08**（確定報告書・収集情報・判断根拠を platform ナレッジベースへ保存し RAG 検索に利用）、
  FR-01（情報収集＝保存元）、FR-06（platform 文書管理の利用）、UC-03〜07、**ADR-0001**（platform 再利用・基盤無改修）
- 対象 Issue: [#18](https://github.com/endazon/ai-stock-trading/issues/18)（FR-08 基盤）／後続 #9・#11・#14
- 関連する実装仕様書: [20260718_knowledge-base-rag-foundation](../specs/20260718_knowledge-base-rag-foundation.md)
- 関連 IADR: [IADR-0022](IADR-0022_information-collection-safe-sourcing.md)（`IKnowledgeBaseSink` の安全既定 no-op ＝本作業で差し替え可能化）、
  [IADR-0051](IADR-0051_service-to-service-auth.md)（s2s client_credentials トークン伝播）、
  [IADR-0063](IADR-0063_assumptions-versioned-resolution.md)（共有クライアント＋`BaseUrl` 未設定 fail-safe の先例）、
  [IADR-0049](IADR-0049_integration-e2e-foundation.md)（CI と実基盤依存テストの切り分け）

## 背景・課題

FR-08 は「確定報告書・収集情報・判断根拠を platform のナレッジベースへ保存し RAG 検索に利用する」ことを求める（Must）。
現状は `IKnowledgeBaseSink` ポートと `LoggingKnowledgeBaseSink`（ログのみ）だけで、実 KB 連携は未着手。後続（#9 実 KB 保存 /
#11 RAG 文脈 / #14 報告書 KB 保存）がこの基盤に乗る。

着手前に `../microservices-platform` を実地調査し、前提の API が**実在・呼び出し可能**であることを確認した。ただし実接続を
「既定オン」にできない制約が判明した:

1. **書き込みは強い認可ゲートの背後**。`POST /documents`（DocumentService）は `platform-admin`/`platform-operator`
   ロールを要求（サービス自身が最終防衛線, platform IADR-0044）。当リポの s2s クライアント（`trading-service`）は
   このロールを持たない → 403。
2. **機密区分が必須**。`confidentiality ∈ {public, internal, confidential, restricted}` 欠落・未知値は 400（platform IADR-0047）。
3. **「保存＝即 RAG 検索可能」は platform 内部パイプライン依存**。`POST /documents` はカタログ登録＋イベント発行まで。
   本文（Markdown 実体）はオブジェクトストレージ（`storage://`）に載り、Ingestion が Qdrant へ取り込んで初めて検索対象になる。
   当リポ側はオブジェクトストレージ書き込み口を持たない。
4. **検索側 `POST /search`（RetrievalService）はロールゲート無し**（ABAC は本文 `Scope` で絞る）。

つまり**保存（強ゲート・本文別経路）と検索（機能可）が非対称**であり、完全な E2E（確定報告書が保存され RAG で検索できる）の
実証には「実 platform ＋ 書き込みロール付与 ＋ オブジェクトストレージ/Ingestion 実接続」が揃う必要がある。

## 決定

### 1. 疎な境界（当リポ側ポート／DTO）で platform 契約を包む（ADR-0001 基盤無改修）
新規共有クライアント `AiStockTrading.Shared.KnowledgeBase` に、当リポ独自の DTO（`KnowledgeDocument`/`KnowledgeQuery`/
`KnowledgeHit`/`KnowledgeWriteResult`）とポート（保存 `IKnowledgeBaseWriter`・取得 `IKnowledgeBaseSearch`）を置く。
platform の `Knowledge.Contracts` へは**直接依存しない**（HTTP アダプタの内側でのみ JSON 形状に写像する）。基盤側の改修は行わない。

- 理由: 消費側（InformationCollection / 後続の TradeDecision・Report）を platform 契約の変化から隔離し、3 サービス横断の
  結合を 1 箇所へ集約する（[IADR-0063] の共有クライアント先例と同形）。

### 2. 実接続は構成で opt-in、既定は no-op（安全既定）
`AddAiStockTradingKnowledgeBase(config)` を提供し、解決時に構成を読む:

- `KnowledgeBase:Documents:BaseUrl` 未設定/不正 URI → 保存は `NoOpKnowledgeBaseWriter`（ログのみ・未保存を返す）。
- `KnowledgeBase:Search:BaseUrl` 未設定/不正 URI → 取得は `NoOpKnowledgeBaseSearch`（空を返す）。
- s2s トークンは既存 `AddAiStockTradingServiceToken`（`ServiceAuth:ClientId/ClientSecret`）で付与。未設定なら認証ヘッダ無し
  → 401 → fail-safe に倒れる（[IADR-0051]）。

よって既定ビルド/CI は外部接続なしで成立し、既存サービスの挙動を一切変えない。

### 3. 保存も取得も fail-safe（例外を業務経路へ伝播しない）
- 保存 HTTP アダプタ: 非 2xx・例外・タイムアウトは「未保存」（`KnowledgeWriteResult.NotSaved`）に倒し、収集サイクルを止めない。
  機密区分は呼び出し側未指定なら既定 `internal` を補完（決定 3 制約 2 対策）。
- 取得 HTTP アダプタ: 非 2xx・例外・タイムアウトは空結果に倒す。RAG 文脈の欠落は判断側で「文脈なし」に縮退できる。

### 4. InformationCollection の保存経路のみ差し替え可能化（他サービスには触れない）
既存 `IKnowledgeBaseSink`（`CollectedInformation` を受ける情報収集固有ポート）はそのまま残す。実 HTTP へ委譲する
`KnowledgeBaseWriterSink` を追加し、`KnowledgeBase:Documents:BaseUrl` 設定時のみ選択する。**既定は現行の
`LoggingKnowledgeBaseSink` を維持**。TradeDecision（#11）・Report（#14）のコードには本 PR では触れない。

## スコープの境界（後続 Issue への申し送り）

本 PR は**基盤と差し込み境界まで**であり、以下は**含めない**（`Refs #18`）:

- **#9 実 KB 保存の本番運用**: `trading-service` への `platform-operator` 相当ロール付与、Markdown 本文の
  オブジェクトストレージ書き込み口、Ingestion 取り込みによる検索可能化、実 E2E。
- **#11 RAG 文脈**: `IKnowledgeBaseSearch` を TradeDecision に結線し「確定日報のみ参照」を実装。本 PR は取得ポート・
  既定 no-op・実アダプタを**用意するのみ**。
- **#14 報告書 KB 保存**: `IKnowledgeBaseWriter` を Report に結線し確定報告書・前提条件写しを保存。
- 実 platform 接続の E2E は #82 系の実コンテナ基盤に乗せる（[IADR-0049] の切り分けに従い、CI は外部接続なしで緑）。

## 検討した代替案

- **A: いま実書き込みを既定オンにする** — 却下。ロール未付与で 403、本文経路も無いため「保存できたように見えて実は失敗」の
  危険。安全既定に反する。
- **B: ポートを各サービスに個別実装** — 却下。3 サービスで fail-safe・s2s・DTO 写像を書き写すことになり保守負債。共有化する。
- **C: platform `Knowledge.Contracts` を直接参照** — 却下。基盤契約への直結は ADR-0001（基盤無改修・疎結合）の趣旨に反し、
  platform の破壊的変更を当リポ全体へ波及させる。HTTP アダプタ内の写像に閉じる。

## 影響・リスク

- 実接続時、保存は 403（ロール未付与）で未保存に倒れる。これは想定内で fail-safe だが、実運用（#9）ではロール付与が前提。
- `Shared.Contracts` は不変・新イベント無し → 監査 Consumer への影響なし。
- 既定挙動は完全に不変（no-op/ログ）。
