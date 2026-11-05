---
title: IADR-0611 REST の所有者の判定（OwnerOnly・OwnerOrService の所有者の分岐）はロールに加えて aud と azp を許可集合で確かめる（段 1 は報告のみ・段 2 で拒否。s2s と gRPC 面は変えない）
type: impl-adr
status: Accepted
related_ids: [NFR, FR-10, FR-14, ADR-0047, IADR-0011, IADR-0051, IADR-0448, IADR-0449, IADR-0324]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 受け入れ基準)
  - planning:projects/ai-stock-trading/07_adr/ADR-0047_discord-bot-token-is-service-identity-east-west.md (決定3)
---

# IADR-0611: REST の所有者の判定に aud / azp を足す

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-05
- 決定者: Claude Code（実装）／ オーケストレーター（REST の所有者の判定に期待する aud と azp を求める、段で切り替える、の指示）

## 起点・関連

- issue: #1528
- 前提: IADR-0011（認可の名称）・IADR-0051（OwnerOrService）・IADR-0448 / IADR-0449（gRPC 面の門の azp の判定）・IADR-0324（認証レルムを MSP レルムへ統一）。
- 実装仕様書: [`.ai-context/specs/20261105_1528_owner-token-audience-and-client.md`](../specs/20261105_1528_owner-token-audience-and-client.md)

## コンテキストと課題

REST の所有者の判定はロール `trading-owner` だけを見る。同じレルムの別のクライアント向けに発行された所有者のトークンも、ロールが付いていれば同じく通る。
FR-10 の受け入れ基準は「kill switch・一時停止の操作は利用者本人のみが行える」と定める。本人の操作の経路は BFF と Discord ボットの 2 つであり、いまの宛先 `platform-api` は MSP の全サービスアカウントに付く共通の値であり、AST の宛先を表さない（MSP レルムの実測）。

## 検討した選択肢

1. **所有者の分岐に認可の要件を足し、aud と azp を許可集合で確かめる（採用）** — s2s の分岐と JwtBearer の設定を変えずに済む。段 1 で実トークンの aud / azp を記録してから段 2 へ上げられる。
2. JwtBearer の `ValidateAudience=true` — s2s のトークンの aud まで一律に求めることになり、基盤の client scope の構成と揃わないと全サービスが 401 になる（セキュリティ仕様書 T-3 の据え置きの理由）。採らない。
3. azp だけを確かめる — azp の判定は gRPC 面と同じで足りるが、宛先を見ないと「別の資源サーバ向けのトークン」を区別できない。両方を見る。

## 決定

1. `OwnerTokenBindingRequirement`（`AllowService`）を `OwnerOnly`（false）と `OwnerOrService`（true）へ足す。判定器 `OwnerTokenBindingHandler`:
   - `AllowService` かつ `trading-service` → 通す（従前どおり）。
   - `trading-owner` でない → 何もしない（ロールの要件が落とす）。
   - aud のどれかが `Auth:OwnerToken:Audiences` に、azp（ちょうど 1 つ）が `Auth:OwnerToken:Clients` に序数一致で在る → 通す。
   - 合わない → `Report`（既定）は Warning（azp・aud を切り詰めて出す）を残して通す。`Enforce` は通さない。
2. 既定の許可集合は aud `ai-stock-trading-api`（AST 専用）、azp `bff`・`ai-stock-trading-owner`。構成すると置き換える。配列でない値・知らない段・段 2 で空の集合は起動で止める。
3. gRPC 面の門は変えない（azp は既に確かめている）。
4. chart は `global.ownerTokenBinding.mode` を auth のサービスへ `Auth__OwnerToken__Mode` で描く（Report / Enforce 以外は描画で止める）。

### Keycloak の宣言（AST 専用の宛先）

- 宛先 `ai-stock-trading-api` は、所有者の操作を送る 2 つのクライアント（`bff`・`ai-stock-trading-owner`）にだけ audience mapper で付ける。MSP 共通の `platform-api` は使わない（MSP の全サービスアカウントのトークンに付くため、AST の宛先の確かめにならない）。
- AST の単体レルム（`infra/keycloak/realm-export.json`）: `ai-stock-trading-owner` に `oidc-audience-mapper`（`included.custom.audience=ai-stock-trading-api`・access token のみ）を足した（本 PR）。
- **MSP のレルム（`deploy/keycloak/microservices-platform-realm.json`）は基盤の管掌**であり、本 PR では変えない。段 2 の前に MSP 側で次を足す必要がある:
  - client scope `ai-stock-trading-api-audience`（`protocol: openid-connect`・mapper `oidc-audience-mapper`・`included.custom.audience: ai-stock-trading-api`・`access.token.claim: true`・`id.token.claim: false`）。
  - `bff` と `ai-stock-trading-owner` の `defaultClientScopes` へ `ai-stock-trading-api-audience` を足す（他のクライアントには足さない）。
- それまでは段 1 のまま（MSP レルムのトークンは宛先が合わず、記録だけが残る）。

## 結果

- 段 1 の既定では認可の結論は変わらない（ログが増え得る）。
- 段 2 では、許可した経路以外で発行された所有者のトークンは 403 になる。

## 残余・フォローアップ

1. 段 2 へ上げるのは、段 1 で実トークンの記録に不一致が出ないことを確かめた後（オーナーの判断）。
2. 単体実行向けの AST レルム（docker-compose）のトークンは aud を持たないため、段 2 にすると compose では所有者の操作が通らない。compose は段 1 のまま使う。
3. JwtBearer の `ValidateAudience=false` は据え置き（s2s の aud の構成は基盤の管掌）。
