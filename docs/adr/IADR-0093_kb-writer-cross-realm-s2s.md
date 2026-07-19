---
title: IADR-0093 KB 保存・検索の s2s は MSP レルムの専用 confidential client（platform-operator）でクロスレルムに認証し、AST レルムの ServiceAuth とは分離した inline ハンドラで発行する
type: impl-adr
status: Accepted
related_ids: [FR-08, FR-01, FR-06, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/06_technical/01_architecture-overview.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# IADR-0093: KB 保存・検索の s2s は MSP レルムの専用 confidential client（platform-operator）でクロスレルムに認証し、AST レルムの ServiceAuth とは分離した inline ハンドラで発行する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-19
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-08**（確定報告書・収集情報・判断根拠を platform ナレッジベースへ保存し RAG 検索に利用）、
  FR-01（情報収集＝保存元）、FR-06（platform 文書管理の利用）、**ADR-0001**（platform 再利用・基盤無改修）
- 対象 Issue: [#18](https://github.com/endazon/ai-stock-trading/issues/18)（FR-08 基盤）の残スコープ「実 KB 保存のクロスレルム s2s 配線」。`Refs #18`
- 関連する実装仕様書: [20260719_kb-writer-cross-realm-s2s](../specs/20260719_kb-writer-cross-realm-s2s.md)
- 関連 IADR:
  [IADR-0069](IADR-0069_knowledge-base-rag-foundation.md)（KB 共有クライアント・sink 差し替えの基盤＝**中核結線は済**。本 ADR は s2s トークンの発行元だけを差し替える）、
  [IADR-0073](IADR-0073_kb-save-deploy-optin-surface.md)（KB 保存 opt-in のデプロイ面露出。`ServiceAuth__*`＝AST レルムを想定していたのを本 ADR で是正）、
  [IADR-0051](IADR-0051_service-to-service-auth.md)（s2s client_credentials 基盤＝`ClientCredentialsTokenProvider`/`ServiceTokenHandler` を**無改修再利用**）、
  [IADR-0062](IADR-0062_discord-bot-gateway-and-authorization.md)（Discord Bot が kill switch を trading-owner 専用クライアントで叩く「分離した inline ハンドラ」パターン＝本 ADR が踏襲する precedent）

> **参照上の注意（ADR 番号の跨ぎ）**: 本文中の `microservices-platform IADR-00xx` は上流の基盤リポジトリ
> [`../microservices-platform`](../../../microservices-platform) 側の ADR を指し、**本リポの `docs/adr/IADR-00xx` とは別採番**である。

## 背景・課題

FR-08 の「収集情報・確定報告書を platform ナレッジベースへ保存する」は [IADR-0069] で writer 結線まで、[IADR-0073] で
デプロイ面の opt-in 露出まで済んでいる。しかし `KnowledgeBase:Documents:BaseUrl` を実 DocumentService へ向けても、
**実書き込みが成立しない**。実コードで裏取りした故障は二段重なっている:

1. **issuer 不一致 → 401**: KB クライアントのトークンは
   `AiStockTrading.Shared.KnowledgeBase.Foundation.Extensions.KnowledgeBaseExtensions` が
   `AddAiStockTradingServiceToken(config)`（[IADR-0051]）で付与しており、これは固定の `ServiceAuth` セクション＝
   **AST レルム**（`ai-stock-trading` / confidential client `ai-stock-trading-svc` / realm role `trading-service`。
   `infra/keycloak/realm-export.json`）のトークンを発行する。一方 MSP `DocumentService` は **`microservices-platform`
   レルムの Authority** で JWT を検証するため、AST レルム発行トークンは issuer 不一致で拒否される。
2. **role 不一致 → 403**: 仮に issuer が一致しても、`POST /documents` は `platform-admin`/`platform-operator`
   ロール必須（microservices-platform IADR-0044、`DocumentService.Api` の write グループ `RequireRole`）。
   `trading-service` は該当せず、かつ **AST レルムに `platform-operator` は存在しない**。

結果、`HttpKnowledgeBaseWriter` は 401/403 を `NotSaved`（fail-safe）へ倒すだけで、実 KB 保存は永久に成立しない。
本 ADR は**この s2s 経路を成立させる**（＝トークンの発行元レルムとクライアントを是正する）ことだけを決める。
writer/sink の中核結線（[IADR-0069]）・デプロイ面の露出（[IADR-0073]）は解き直さない。

## 決定

### 1. KB の s2s は MSP レルムの専用 confidential client でクロスレルムに認証する
KB 書き込み（DocumentService `POST /documents`）・検索（RetrievalService `POST /search`）は、
**MSP `microservices-platform` レルム**の専用 confidential client **`ai-stock-trading-kb-writer`**
（`serviceAccountsEnabled: true`、service-account に realm role **`platform-operator`** を付与）の client_credentials
資格で MSP の各サービスを叩く。トークンは MSP レルムの token エンドポイントから取得するため issuer が一致し（決定1が401を解消）、
`platform-operator` を持つため write の `RequireRole` を通過する（403 を解消）。

- **クライアントの identity/role は MSP レルム側が所有・管理**する。AST は client secret を「受け取って提示する」だけで、
  ロールの付与主体にはならない。これは ADR-0001（基盤無改修＝platform の**利用**）と、ADR-0010（AST は自前で鍵を持たない＝
  identity 発行の主体にならない）の双方と整合する。AST が保持するのは既存の `ServiceAuth`/`OwnerAuth` と同種の
  s2s client secret のみで、LLM/プロバイダ鍵の類は増やさない。

### 2. AST レルムの ServiceAuth とは「分離した inline ハンドラ」でトークンを発行する（レルム跨ぎの取り違え防止）
KB クライアント（`kb-documents` / `kb-search`）には、[IADR-0062] の `DiscordOwnerAuthExtensions` と同じく
**DI に provider を登録せず**、名前付きクライアントの `AddHttpMessageHandler` ファクトリ内で
`ServiceTokenHandler(new ClientCredentialsTokenProvider(...))` を**inline 生成**して差し込む。専用の token 取得用
名前付き HttpClient（`kb-writer-token`）を用いる。

- 理由（重要）: 消費側 Worker（InformationCollection / Report / TradeDecision）は、自分の s2s クライアントへ
  `AddAiStockTradingServiceToken` も呼ぶ。これは `IServiceAccessTokenProvider` を **`TryAddSingleton`** で登録する
  （[IADR-0051]）。もし KB も provider を DI に登録すると、同一コンテナ内で **TryAdd 衝突**が起き、先勝ちで
  **AST レルムのトークンが KB クライアントへ漏れる（または逆）**という、まさに本 ADR が直したい故障を再生産する。
  inline 生成は KB の名前付きクライアントにトークン発行を閉じ込め、レルム跨ぎのトークン取り違えを構造的に防ぐ。
- `AddAiStockTradingServiceToken`（AST レルム）の呼び出しは KB クライアントから**外す**。KB は本 ADR の KB 専用認証節のみを用いる。

### 3. 専用 config セクション `KnowledgeBase:Auth`。AST の Auth:Authority へはフォールバックしない
KB の認証は独立した `KnowledgeBase:Auth` セクションから読む（`ServiceAuthOptions` を無改修再利用）:

- `KnowledgeBase:Auth:TokenEndpoint` — MSP レルムの token エンドポイント（明示指定・最優先）。
- `KnowledgeBase:Auth:Authority` — 未指定時に token エンドポイントを導出する MSP レルムの Authority
  （例 `http://keycloak:8080/realms/microservices-platform` → `.../protocol/openid-connect/token`）。
- `KnowledgeBase:Auth:ClientId` / `ClientSecret` / `Scope`。

**AST の `Auth:Authority`（＝AST レルム）へはフォールバックしない**。[IADR-0051] の `AddAiStockTradingServiceToken` は
`Auth:Authority` から導出するが、それを流用すると誤って AST レルムのトークンを出し、故障（決定1）を再現する。
KB の token エンドポイントは MSP レルムを**明示**する（`KnowledgeBase:Auth:TokenEndpoint` か `KnowledgeBase:Auth:Authority`）。

### 4. 既定は空＝トークン無し＝現行 no-op / fail-safe を厳密保持。秘密は Secret 経由・空既定・平文禁止
`KnowledgeBase:Auth:*` が未設定（＝`IsEnabled` false）なら**トークンを付けない**。`ServiceTokenHandler` は
Authorization を付けずに送信し、DocumentService が 401 を返し、writer は `NotSaved` へ倒れる（現行挙動＝fail-safe）。
`KnowledgeBase:Documents:BaseUrl` 自体が空なら `NoOpKnowledgeBaseWriter`（保存しない）で、そもそも通信しない。

デプロイ面の `KnowledgeBase__Auth__ClientSecret` は k8s Secret（`ast-secrets`、空既定）から注入し、
`.env.example` / `values.yaml` / `docker-compose.yml` には**キー名と用途のみ**を置く（実値・実秘密は書かない）。

### 5. レルム定義は MSP リポの変更として分離する
`ai-stock-trading-kb-writer` client と service-account の `platform-operator` 付与は **MSP リポジトリ**
（`microservices-platform/deploy/keycloak/microservices-platform-realm.json`）の変更であり、別 PR・MSP 側 IADR で扱う。
本 AST PR は「AST 側の認証節・デプロイ面 env 露出・テスト・仕様」に閉じる。両者は疎結合で、AST 側は Secret が空なら
現行どおり no-op のため、MSP 側の反映を待たずに独立してマージ可能（安全既定）。

## スコープの境界（後続への申し送り・未充足＝充足扱いしない）

- **実 201 到達（ローカル経路B）**: `KnowledgeBase:Documents:BaseUrl` ＋ `KnowledgeBase:Auth:*`（MSP レルム token
  エンドポイント＋`ai-stock-trading-kb-writer` の資格）を投入し、MSP realm 反映済みの環境で `POST /documents` が
  **201** を返す状態は、AST/MSP 両 PR＋Secret 投入が揃って初めて成立する。通し確認手順は PR/issue に明記し、
  自動化は **#82 系**の実コンテナ E2E／ローカル手動へ分離する。CI は外部接続に依存しない単体/契約テストで担保する。
- **本文（Markdown）の object storage 書き込み・Ingestion による検索可能化** → platform 側（当リポは書き込み口を持たない）。
  本 ADR はカタログ登録（メタデータ）の s2s 成立まで。RAG で本文がヒットするのはこの取り込みが揃ってから。
- **RAG 検索（read）の実 201 実証** → `kb-search` も同一 MSP レルム/クライアントで同型に認証するが、取得の
  end-to-end 実証は #11（TradeDecision の RAG 結線）／#82 系に乗せる。

## 検討した代替案

- **A: AST レルムの `ai-stock-trading-svc`（trading-service）に platform-operator 相当を足す** — 却下。
  トークンの issuer は AST レルムのままなので MSP の Authority 検証を通らない（401 が残る）。レルム跨ぎの本質を解かない。
- **B: MSP の DocumentService の write ロール要件を緩めて AST トークンを通す** — 却下。基盤（ADR-0001）と
  microservices-platform IADR-0044 の多層防御を弱める。書き込みの管理系ロール要件は維持する。
- **C: KB provider を DI（`TryAddSingleton`）で登録し `AddAiStockTradingServiceToken` を KB:Auth 用に一般化する** — 却下。
  消費側 Worker の AST レルム ServiceAuth provider と TryAdd 衝突し、レルム跨ぎでトークンが漏れる（決定2の理由）。
- **D: いま MSP realm 変更まで本 AST PR に混ぜる** — 却下。realm 定義は MSP リポの資産で、リポ跨ぎの単一 PR にできない。
  分離して MSP 側 PR/IADR にする（決定5）。

## 影響・リスク

- 既定挙動は完全に不変（`KnowledgeBase:Auth:*` 空＝トークン無し＝現行の 401/no-op）。既存サービス・テストへの影響なし。
- KB クライアントの s2s 発行元を AST レルム→MSP レルムへ移すのは KB クライアント（`kb-documents`/`kb-search`）に限定され、
  他サービスの ServiceAuth（AST レルム）には触れない（inline 分離）。
- `Shared.Contracts` は不変・新イベント無し → 監査 Consumer への影響なし。
- MSP realm 反映前に AST 側だけマージしても、Secret 空なら no-op のため退行なし。realm 反映後に Secret を投入して初めて有効。
