---
title: ナレッジベース保存・RAG 取得の基盤（platform 文書管理／検索の利用・既定 no-op・opt-in）
type: spec
status: review
related_ids: [FR-08, FR-01, FR-06, UC-03, UC-04, UC-06, UC-07, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-18
updated: 2026-07-18
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# 仕様書: ナレッジベース保存・RAG 取得の基盤（platform 文書管理／検索の利用）

> Issue [#18](https://github.com/endazon/ai-stock-trading/issues/18)（FR-08, Must）。
> **本作業のスコープは「実 KB への保存・取得の“基盤”を用意し、後続（#9 実 KB 保存 / #11 RAG 文脈 / #14 報告書 KB 保存）が
> 乗れる境界を切る」ことに限る。** 完全な E2E（確定報告書が保存され RAG で検索できる）の実証は、実 platform ＋
> 書き込みロール付与 ＋ オブジェクトストレージ/Ingestion 実接続が揃って初めて可能なため、本 PR には含めず後続へ委ねる
> （`Refs #18`）。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: **FR-08**（確定報告書・収集情報・判断根拠を platform ナレッジベースへ保存し RAG 検索に利用）、
  FR-01（情報収集＝保存元）、FR-06（platform 文書管理の利用）
- ユースケース（UC）: UC-03〜06（KB 保存・参照の各業務）、UC-07（監査・振り返り）
- ADR: **ADR-0001**（platform 再利用・基盤無改修。API/イベントで利用）
- 関連 IADR: [IADR-0022](../adr/IADR-0022_information-collection-safe-sourcing.md)（`IKnowledgeBaseSink` の安全既定 no-op ＝本作業で差し替え可能化）、
  [IADR-0051](../adr/IADR-0051_service-to-service-auth.md)（s2s client_credentials トークン伝播）、
  [IADR-0063](../adr/IADR-0063_assumptions-versioned-resolution.md)（共有クライアント＋`BaseUrl` 未設定 fail-safe の先例）、
  [IADR-0049](../adr/IADR-0049_integration-e2e-foundation.md)（CI と実基盤依存テストの切り分け）。
  本作業で新規 **[IADR-0069](../adr/IADR-0069_knowledge-base-rag-foundation.md)**。
- 対象 Issue: #18（本体）／後続 #9・#11・#14

## 前提確認（着手前調査の結論）

`../microservices-platform` を実地調査し、FR-08 が前提とする platform の文書管理・検索 API が**実在・呼び出し可能**であることを確認した。

| 用途 | サービス | エンドポイント | 契約 |
| --- | --- | --- | --- |
| 保存（カタログ登録） | DocumentService | `POST /documents` | `CreateDocumentRequest(Title, OriginalUri, ContentType, Attributes, Tags)` → `DocumentDto` |
| 取得（RAG） | RetrievalService | `POST /search` | `SearchRequest(Query, TopK, AttributeFilters, Scope)` → `SearchResponse(Results, TotalHits, ElapsedMs)` |

**実接続を既定オフ（opt-in）にすべき決定的な制約（本作業の設計根拠）:**

1. **書き込みは強い認可ゲートの背後** — `POST /documents` は `RequireAuthorization(platform-admin, platform-operator)`
   （サービス自身が最終防衛線, platform IADR-0044）。当リポの s2s クライアント（`trading-service` / client_credentials）は
   このロールを持たない → **403**。実書き込みには Keycloak でのロール付与が別途必要。
2. **機密区分が必須** — `POST /documents` は属性 `confidentiality ∈ {public, internal, confidential, restricted}` を必須検証
   （platform IADR-0047）。欠落・未知値は 400。
3. **「保存＝即 RAG 検索可能」は platform 内部パイプライン依存** — `POST /documents` はカタログ登録＋イベント発行まで。
   本文（Markdown 実体）はオブジェクトストレージ（`storage://`）に載り、Ingestion が Qdrant へ取り込んで初めて検索対象になる。
   当リポ側はオブジェクトストレージ書き込み口を持たない（本作業の対象外・後続）。
4. **検索側 `POST /search` はロールゲート無し**（ABAC は本文 `Scope` で絞る）＝ opt-in すれば RAG 取得ポートは実際に機能する。

この**保存（強ゲート・本文別経路）／検索（ゲート無し・機能可）の非対称性**が本設計の要点である。

## スコープ（このPRで実装するもの）

新規の共有クライアント `AiStockTrading.Shared.KnowledgeBase` を追加し、当リポ側の**疎な境界（ポート／DTO）**で
platform 契約を包む（platform 契約へ直結させない ＝ ADR-0001 基盤無改修）。

1. **保存ポート `IKnowledgeBaseWriter`** ＋ 実 HTTP アダプタ（→ DocumentService `POST /documents`）。
   機密区分は既定 `internal` を補完。非 2xx・例外・タイムアウトは「未保存」に倒す（fail-safe。収集サイクルを止めない）。
2. **RAG 取得ポート `IKnowledgeBaseSearch`** ＋ 実 HTTP アダプタ（→ RetrievalService `POST /search`）。
   非 2xx・例外は空結果に倒す（fail-safe）。
3. **fail-safe 配線** `AddAiStockTradingKnowledgeBase(config)`:
   - `KnowledgeBase:Documents:BaseUrl` 未設定/不正 → 保存は `NoOpKnowledgeBaseWriter`（ログのみ）。
   - `KnowledgeBase:Search:BaseUrl` 未設定/不正 → 取得は `NoOpKnowledgeBaseSearch`（空）。
   - s2s トークンは既存 `AddAiStockTradingServiceToken`（`ServiceAuth:ClientId/ClientSecret`）で opt-in。未設定なら認証ヘッダ無し。
4. **InformationCollection の保存経路を差し替え可能に** — 既存 `IKnowledgeBaseSink`（`CollectedInformation` を受ける）はそのまま。
   実 HTTP へ委譲する `KnowledgeBaseWriterSink` を追加し、`KnowledgeBase:Documents:BaseUrl` 設定時のみ選択。
   **既定は現行の `LoggingKnowledgeBaseSink` を維持**（安全既定）。

## スコープ外（後続 Issue の境界＝本 PR に含めない）

- **#9（実 KB 保存の本番運用）**: 書き込みロール付与・オブジェクトストレージ/Ingestion への本文取り込み・E2E 検証。
- **#11（TradeDecision の RAG 文脈）**: `IKnowledgeBaseSearch` を TradeDecision に結線し「確定日報のみ参照」を実装。
  本 PR は取得ポート＋既定 no-op＋実アダプタを**用意するのみ**（TradeDecision/Report のコードには触れない）。
- **#14（Report の KB 保存）**: `IKnowledgeBaseWriter` を Report に結線し確定報告書を保存。前提条件写しの KB 保存も同様。
- 実 platform 接続の E2E（実コンテナ）は #82 系の基盤に乗せる後続。CI は外部接続なしで緑（下記テスト方針）。

## 受け入れ基準 → テスト写像

| # | 基準 | テスト |
| --- | --- | --- |
| 1 | `KnowledgeBase:*:BaseUrl` 未設定/不正で保存・取得が安全既定（NoOp）に倒れる | `KnowledgeBaseSelectionTests` |
| 2 | `BaseUrl` 設定時に実 HTTP アダプタが選択される | `KnowledgeBaseSelectionTests` |
| 3 | 保存アダプタが `POST /documents` に機密区分 `internal` 既定付きで送る／2xx で保存成功・Id を返す | `HttpKnowledgeBaseWriterTests` |
| 4 | 保存アダプタは非 2xx・例外で未保存に倒し例外を伝播しない | `HttpKnowledgeBaseWriterTests` |
| 5 | 取得アダプタが `POST /search` に写像し結果を返す／非 2xx・例外は空に倒す | `HttpKnowledgeBaseSearchTests` |
| 6 | InformationCollection は既定で `LoggingKnowledgeBaseSink`、`Documents:BaseUrl` 設定時のみ `KnowledgeBaseWriterSink` | `KnowledgeBaseSinkSelectionTests` |
| 7 | `KnowledgeBaseWriterSink` が `CollectedInformation` を `KnowledgeDocument` に写像し writer へ委譲する | `KnowledgeBaseWriterSinkTests` |

## 完了条件（Definition of Done 抜粋）

- `dotnet build backend/backend.slnx` / `dotnet test backend/backend.slnx` 緑、`dotnet format` 済、警告ゼロ。
- 新イベント追加なし（監査 Consumer 変更不要）。`Shared.Contracts` 変更なし。
- 既定挙動（no-op/ログ）不変 ＝ 既存サービスの挙動を変えない。
- IADR-0069 に境界（認可ロール・本文/Ingestion・後続の差し込み点）を明記。
