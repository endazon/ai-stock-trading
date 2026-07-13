---
title: サービス間同期照会の service-to-service 認証（呼び出し側トークン伝播）
type: spec
status: review
related_ids: [FR-04, FR-07, FR-10, ADR-0004, ADR-0007, IADR-0011, IADR-0028, IADR-0029, IADR-0030, IADR-0051]
author: endazon (with Claude Code)
created: 2026-07-13
updated: 2026-07-13
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_trading-guard-and-margin.md
  - ../../planning/projects/microservices-platform/07_adr/ADR-0004_authz-abac.md
---

# 仕様書: サービス間同期照会に service-to-service 認証を付与する（#76）

> Issue [#76](https://github.com/endazon/ai-stock-trading/issues/76)（`Refs #22`）。IADR-0028/0029/0030 で導入した
> 同期照会（OwnerOnly 保護）に対し、呼び出し側（取引判断・市場監視）が**サービストークン（client_credentials）を
> 伝播**して認証済みで呼べるようにする。最小権限の `trading-service` ロールを導入し、`open-positions` を最優先に
> `daily-policy` / `sizing-context` を対応する。方式・トレードオフは [IADR-0051](../adr/IADR-0051_service-to-service-auth.md)。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-04（取引判断のサイジング/方針）、FR-07（日報方針）、FR-10（保有・損切り監視の維持）
- ADR: ADR-0007（利用者のみ操作）、platform ADR-0004（Keycloak OIDC/認可）
- 関連 IADR: IADR-0011（Keycloak・OwnerOnly）、IADR-0028/0029/0030（同期 API 方式）、新規 [IADR-0051](../adr/IADR-0051_service-to-service-auth.md)
- 消費側: #82（IADR-0050 の s2s トークン伝播つき同期照会 E2E）
- 対象 Issue: #76

## コンテキストと課題

3 つの同期照会エンドポイントは提供側で `OwnerOnly`（`trading-owner`）に保護されているが、呼び出し側は認証
ヘッダなしで呼ぶため、BaseUrl 設定時に常に 401 → 安全既定へフォールバックする。特に `open-positions` は
「接続されているのに損切り検知が働かない」サイレント縮退で優先度が高い（IADR-0030 claude-review 指摘）。

自律ワーカーには伝播元のユーザトークンが無いため、方式は **client_credentials**（機械 ID）を採る（IADR-0051 決定 1）。
`trading-owner` をサービスへ与えるのは過剰権限（kill switch 操作が可能になる）ため、**読み取り専用の
`trading-service` ロール**を新設し、読み取り系エンドポイントのみ許可する（IADR-0051 決定 3）。

## 対象範囲

### PlatformShim（client 側トークン基盤・`AddAiStockTradingAuth` の対）

`backend/TestSupport/AiStockTrading.TestSupport.PlatformShim/Foundation/Auth/`（新設）:

- `IServiceAccessTokenProvider`: `Task<string?> GetTokenAsync(CancellationToken)`。
- `ClientCredentialsTokenProvider`: token エンドポイントへ `grant_type=client_credentials`（client_id/secret、
  任意 scope）を POST。`expires_in − マージン` までキャッシュし、スレッドセーフに単一取得（`SemaphoreSlim`）。
  失敗（非 2xx/例外/タイムアウト）は null を返し、安定メッセージ＋構造化フィールドで `LogWarning`（IADR-0051 決定 5）。
- `ServiceTokenHandler : DelegatingHandler`: `GetTokenAsync` の結果が非 null なら `Authorization: Bearer` を付与。
  null ならヘッダ無しで送信（→ 401 → 既存フェイルセーフ）。既存 Authorization は尊重。**例外は送出しない**。
- `ServiceAuthExtensions.AddAiStockTradingServiceToken(this IHttpClientBuilder, IConfiguration)`:
  `ServiceAuth:ClientId` と `ServiceAuth:ClientSecret` の**両方**が設定され token エンドポイントが解決できる時のみ
  `ClientCredentialsTokenProvider` を有効化し、名前付き HttpClient に `ServiceTokenHandler` を追加する。
  未設定なら no-op（**ハンドラを追加しない**）＝現行挙動を厳密保持（IADR-0051 決定 4）。
  - 設定キー: `ServiceAuth:TokenEndpoint`（任意・未指定なら `Auth:Authority` + `/protocol/openid-connect/token`）、
    `ServiceAuth:ClientId`、`ServiceAuth:ClientSecret`（秘密）、`ServiceAuth:Scope`（任意）。

### 提供側エンドポイント（最小権限ポリシーの分離）

- `AuthExtensions`（PlatformShim）:
  - `AiStockTradingAuthPolicies.OwnerOrService = "OwnerOrService"`、`ServiceRole = "trading-service"` を追加。
  - `OwnerOrService` ポリシー（`RequireRole(OwnerRole, ServiceRole)`＝いずれか）を登録。`OwnerOnly` は不変。
- RiskManagement `RiskControlEndpoints`: `sizing-context`・`open-positions` を `OwnerOrService` の
  サブグループへ移す。kill-switch・settings は `OwnerOnly` 据え置き。
- Reports `ReportEndpoints`: `daily-policy`（GET）を `OwnerOrService` へ。一覧・ドラフト・確定等は `OwnerOnly` 据え置き。

### 呼び出し側 Worker の配線

- 取引判断 `Program.cs`: `"reports"` / `"risk"` の `AddHttpClient(...)` に `.AddAiStockTradingServiceToken(Configuration)` を連結。
- 市場監視 `Program.cs`: `"risk"` の `AddHttpClient(...)` に同様に連結。
- Http アダプタ（`HttpDailyPolicyProvider`・`HttpSizingContextProvider`・`HttpPositionStore`）は**無改修**。

### Keycloak dev realm

- `infra/keycloak/realm-export.json`:
  - realm ロール `trading-service` を追加。
  - confidential クライアント `ai-stock-trading-svc`（`serviceAccountsEnabled: true`・`standardFlowEnabled: false`・
    `publicClient: false`・dev 用固定 `secret`）を追加。service account に `trading-service` ロールを付与
    （`serviceAccountClientRoles`/realm ロールマッピング）。
  - 本番秘密はコミットせず config（user-secrets/.env）から供給する旨をコメント。

## 受け入れ基準

CI で緑にする範囲（ユニット＋fake `HttpMessageHandler`＋WebApplicationFactory）:

- [ ] `ClientCredentialsTokenProvider`: fake token エンドポイントから `access_token` を取得し Bearer に用いる。
- [ ] キャッシュ: 有効期限内は再取得しない（token エンドポイント呼び出しは 1 回）。期限切れで再取得する。
- [ ] 取得失敗（非 2xx/例外/タイムアウト）は null を返し `LogWarning`（例外を送出しない）。
- [ ] `ServiceTokenHandler`: プロバイダが token を返すと `Authorization: Bearer` が付与される／null ならヘッダ無し。
- [ ] `AddAiStockTradingServiceToken`: `ClientId`＋`Secret` 設定時のみハンドラ有効。未設定はハンドラ無し（現行挙動）。
- [ ] `OwnerOrService` ポリシー: `trading-service` トークンで読み取り系 3 エンドポイントが 200、無ロールは 403、未認証は 401。
- [ ] 分離の非回帰: kill-switch/settings/report-confirm は `trading-service` では 403（`trading-owner` のみ 200）。
- [ ] 既存テスト（選択テスト・アダプタテスト・エンドポイントテスト）を緑に保つ。

実 API/実コンテナ前提（CI 既定では実行しない・#82 で検証）:

- [ ] 実 Keycloak の `ai-stock-trading-svc` で `client_credentials` を取得し、認証済み同期照会が 200 になる E2E。

## フェイルセーフの方向（明示）

- 秘密未設定 → Null プロバイダ → トークン無し → 401 → 既存安全既定（null/残枠0/空列）。既定 CI/ビルド不変。
- 取得失敗 → 例外を送出せずヘッダ無し送信 → 401 → 既存安全既定。ワーカー巡回は止めない。
- BaseUrl 未設定 → プレースホルダ選択で HTTP 自体が発生しない（二重ゲート）。
- 可観測性: 取得失敗を安定メッセージで `LogWarning`（OTel）。将来メトリクス化のフック。

## 対象外（後続）

- 実 Keycloak client_credentials 往復・認証済み同期照会の E2E（#82・IADR-0050 の残り）。
- トークンリフレッシュのリトライ/バックオフ高度化、複数 audience/scope の細分化。
- 本番 platform 統合時の実 Foundation による置換（#22）。

## テスト方針

- `ClientCredentialsTokenProvider`・`ServiceTokenHandler` は fake `HttpMessageHandler`（200/401/500/タイムアウト）で
  取得・キャッシュ・Bearer 付与・フェイルセーフを検証。`ILogger` のスパイで警告を確認。
- `AddAiStockTradingServiceToken` の有効/無効は WebApplicationFactory の構成上書きで検証。
- `OwnerOrService` の分離は各サービスの `WebApplicationFactory`（`TestAuthHandler` にロールを載せて）で
  200/403/401 と書き込み系の非回帰（403）を検証。

## 関連仕様

- 先行: [20260710_daily-policy-wiring](20260710_daily-policy-wiring.md)、[20260710_sizing-context-wiring](20260710_sizing-context-wiring.md)、[20260711_position-store-wiring](20260711_position-store-wiring.md)
- 実装ADR: [IADR-0051](../adr/IADR-0051_service-to-service-auth.md)
- 消費側: [20260713_82_e2e-slice-bc](20260713_82_e2e-slice-bc.md)（#82・s2s E2E は本 PR マージ後）

## 未決事項

- 秘密（`ServiceAuth:ClientSecret`）の投入先（user-secrets / `.env`）は実行環境（#107 スキャフォールド）に合わせる。
  キー名と設定先はユーザーへ明示指示し、値は要求しない。
