---
title: 実 KB 保存のクロスレルム s2s 配線（MSP レルムの専用クライアントで platform-operator 認証）
type: work
status: review
related_ids: [FR-08, FR-01, FR-06, ADR-0001]
author: endazon (with Claude Code)
created: 2026-07-19
updated: 2026-07-19
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0001_platform-reuse.md
---

# 作業仕様書: 実 KB 保存のクロスレルム s2s 配線

> Issue [#18](https://github.com/endazon/ai-stock-trading/issues/18)（FR-08 ナレッジベース）の残スコープ
> 「実 KB 保存のクロスレルム s2s 配線」を対象とする。`Refs #18`。設計判断は
> [IADR-0093](../adr/IADR-0093_kb-writer-cross-realm-s2s.md)。

## 前提の確認結果（着手前調査・実コードで裏取り）

- **中核の writer/sink 結線は完了済み**（[IADR-0069] / PR #162）、**デプロイ面の opt-in 露出も完了済み**（[IADR-0073]）。
  残る唯一の穴は **s2s トークンの発行元レルム/クライアントが誤っている**こと。
- **故障は二段**（[IADR-0093] 背景）:
  1. KB クライアントのトークンは `KnowledgeBaseExtensions` が `AddAiStockTradingServiceToken`（AST レルム
     `ai-stock-trading` / `ai-stock-trading-svc` / `trading-service`）で付与 → MSP `DocumentService` は
     `microservices-platform` レルムの Authority で検証 → **issuer 不一致で 401**。
  2. `POST /documents` は `platform-admin`/`platform-operator` 必須（`DocumentEndpoints.cs` の write グループ
     `RequireRole`）→ `trading-service` は非該当、AST レルムに `platform-operator` 不在 → **403**。
- したがって `KnowledgeBase:Documents:BaseUrl` を設定しても 401/403 で `NotSaved`（fail-safe）に縮退する。

## 課題（本作業で埋める穴）

KB の s2s トークンを、**MSP `microservices-platform` レルムの専用 confidential client `ai-stock-trading-kb-writer`
（service-account に `platform-operator`）** の資格で発行するよう是正し、実 KB 保存経路（201）を成立可能にする。
既定は空＝トークン無し＝現行 no-op/fail-safe を厳密保持する。

## 変更の切り分け（AST / MSP）

| リポ | 変更 |
| --- | --- |
| **AST**（本リポ・本 PR `Refs #18`） | KB 専用認証節 `KnowledgeBase:Auth`（分離 inline ハンドラ）／`KnowledgeBaseExtensions` の s2s 発行元差し替え／`values.yaml`・`.env.example`・`docker-compose.yml` の env 露出（空既定・Secret 参照）／単体・契約テスト／本仕様・IADR-0093 |
| **MSP**（`microservices-platform`・別 PR） | `microservices-platform-realm.json` に `ai-stock-trading-kb-writer` client＋service-account の `platform-operator` 付与／MSP 側 IADR |

## 実装方針（AST 側）

1. `AiStockTrading.Shared.KnowledgeBase.Foundation.Extensions.KnowledgeBaseAuthExtensions`（新規）:
   `KnowledgeBase:Auth` を読み、`ServiceAuthOptions`（無改修）で `ClientCredentialsTokenProvider` +
   `ServiceTokenHandler` を **inline 生成**して名前付きクライアントへ差し込む（DI 登録しない＝[IADR-0062] precedent）。
   token エンドポイントは `KnowledgeBase:Auth:TokenEndpoint` 明示優先、なければ `KnowledgeBase:Auth:Authority`
   から導出。**AST の `Auth:Authority` へはフォールバックしない**。
2. `KnowledgeBaseExtensions`: `kb-documents` / `kb-search` の `.AddAiStockTradingServiceToken(config)` を
   `.AddAiStockTradingKnowledgeBaseAuth(config)` へ差し替える。
3. デプロイ面: `KnowledgeBase__Auth__TokenEndpoint`（または `__Authority`）・`__ClientId`・`__ClientSecret` を
   information-collection / report の extraEnv、`.env.example`、`docker-compose.yml` へ空既定で露出。
   `__ClientSecret` は `ast-secrets`（空既定・optional）から注入。

## 受け入れ基準（テストへ写像）

- [ ] `KnowledgeBase:Auth` が揃えば KB documents クライアントの `POST /documents` に **Bearer トークンが付与**される
      （MSP レルム token エンドポイントから取得したトークン）。→ `KnowledgeBaseAuthTests` 統合テスト。
- [ ] `KnowledgeBase:Auth` 未設定なら Authorization を付けない（**AST レルム ServiceAuth が同一コンテナに登録されていても
      KB クライアントへ漏れない**）＝現行 no-op/401 の fail-safe を保持。→ 分離（isolation）回帰テスト。
- [ ] token エンドポイント導出は `KnowledgeBase:Auth:Authority`（MSP レルム）からのみ。AST `Auth:Authority` へは
      フォールバックしない。→ 導出ゲートの単体テスト。
- [ ] `ClientSecret` 欠如・両方未設定は no-op（トークン無し）。→ ゲートの単体テスト。
- [ ] 既存の writer fail-safe（403/例外→`NotSaved`）は不変。→ 既存 `HttpKnowledgeBaseWriterTests` 緑維持。
- [ ] `dotnet build` 警告0・`dotnet format` OK・`Shared.Contracts` 不変・新イベント無し。

## スコープ外（後続・未充足＝充足扱いしない）

- MSP realm 反映済み環境での **実 201 到達（経路B）** は AST/MSP 両 PR＋Secret 投入が前提。ローカル手動 / #82 系 E2E。
- Markdown 本文の object storage 書き込み・Ingestion 取り込みによる検索可能化 → platform 側。
- RAG 検索（read）の 201 実証 → #11 / #82 系。

## ローカル経路B の通し確認手順（PR/issue に明記）

1. MSP: realm に `ai-stock-trading-kb-writer`（`platform-operator`）を反映（MSP PR）。Keycloak 再インポート。
2. AST: `KnowledgeBase__Documents__BaseUrl=http://document-service:8080`、
   `KnowledgeBase__Auth__Authority=http://keycloak:8080/realms/microservices-platform`、
   `KnowledgeBase__Auth__ClientId=ai-stock-trading-kb-writer`、`ast-secrets` に `kb-auth-client-secret` を投入。
3. 情報収集を 1 サイクル走らせ、`document-service` が **201** を返し文書 Id が採番されることを確認。
