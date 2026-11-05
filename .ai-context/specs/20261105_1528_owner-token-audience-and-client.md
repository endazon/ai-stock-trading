---
title: REST の所有者の判定で、トークンの宛先（aud）と発行先のクライアント（azp）を確かめる（段 1 は報告のみ・段 2 で拒否）
type: spec
status: accepted
related_ids: [NFR, FR-10, FR-14, ADR-0047, IADR-0611, IADR-0011, IADR-0051, IADR-0448, IADR-0449, IADR-0324]
author: claude (Claude Code)
created: 2026-11-05
updated: 2026-11-05
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-10 受け入れ基準「kill switch・一時停止の操作は利用者本人のみが行える（他サービスの資格情報では拒否される）」)
  - planning:projects/ai-stock-trading/07_adr/ADR-0047_discord-bot-token-is-service-identity-east-west.md (決定3 gRPC 面の所有者の門は azp を確かめる)
---

# 仕様書: REST の所有者の判定に aud / azp を足す（#1528）

## 起点

- #1528（NFR・クラウド移行前の堅牢化）。
- REST の所有者の判定（`OwnerOnly`・`OwnerOrService` の所有者の分岐）は realm ロール `trading-owner` だけを見る。JwtBearer は `ValidateAudience=false`。
- gRPC 面の所有者の門は `azp` を確かめている（IADR-0448 / IADR-0449）。REST の面には付けていなかった（ADR-0047 決定 3 は gRPC 面だけ）。

## 実測（develop `26d0a18a7`・MSP develop `bab977e0f`）

| # | 事実 | 確かめ方 |
| --- | --- | --- |
| 1 | `OwnerOnly` = `RequireRole("trading-owner")`、`OwnerOrService` = `RequireRole("trading-owner", "trading-service")` | `AuthExtensions.cs` |
| 2 | `ValidateAudience = false` | `AuthExtensions.cs` |
| 3 | MSP レルムで所有者の REST 操作を送るのは BFF（client `bff`）と Discord ボット（`ai-stock-trading-owner`）。どちらも client scope `platform-api-audience`（aud `platform-api`）を持つ。ただし `platform-api` は MSP の全サービスアカウントにも付く（`ai-stock-trading-svc`・`-kb-writer`・`-llm-caller` ほか）ため、AST の宛先の確かめには使えない | MSP `deploy/keycloak/microservices-platform-realm.json` |
| 4 | 単体実行向けの AST レルムのクライアントには audience の mapper が無い | `infra/keycloak/realm-export.json` |

## 決定（IADR-0611）

1. 所有者の分岐に要件 `OwnerTokenBindingRequirement` を足す（`OwnerOnly`・`OwnerOrService`）。aud のどれかが許可集合に在り、azp がちょうど 1 つで許可集合に在るときだけ合う（序数一致。gRPC 面の門と同じ azp の判定を使う）。
2. サービスの分岐（`trading-service`）は変えない。gRPC 面の門（`GrpcOwnerOrService`・`GrpcOwnerOnly`）も変えない。
3. 段: `Auth:OwnerToken:Mode` = `Report`（既定。合わないトークンを Warning で残して通す）／`Enforce`（通さない＝403）。
4. 許可集合: `Auth:OwnerToken:Audiences`（既定 `ai-stock-trading-api`（AST 専用））・`Auth:OwnerToken:Clients`（既定 `bff`・`ai-stock-trading-owner`）。構成すると置き換える。1 つの値（配列でない）・段 2 で空・知らない段は起動で止める。
5. chart は `global.ownerTokenBinding.mode`（既定 Report）を auth のサービスへ env で描く。値は Report / Enforce 以外なら描画で止める。

### Keycloak の宣言（AST 専用の宛先）

- 宛先 `ai-stock-trading-api` は、所有者の操作を送る 2 つのクライアント（`bff`・`ai-stock-trading-owner`）にだけ audience mapper で付ける。MSP 共通の `platform-api` は使わない（MSP の全サービスアカウントのトークンに付くため、AST の宛先の確かめにならない）。
- AST の単体レルム（`infra/keycloak/realm-export.json`）: `ai-stock-trading-owner` に `oidc-audience-mapper`（`included.custom.audience=ai-stock-trading-api`・access token のみ）を足した（本 PR）。
- **MSP のレルム（`deploy/keycloak/microservices-platform-realm.json`）は基盤の管掌**であり、本 PR では変えない。段 2 の前に MSP 側で次を足す必要がある:
  - client scope `ai-stock-trading-api-audience`（`protocol: openid-connect`・mapper `oidc-audience-mapper`・`included.custom.audience: ai-stock-trading-api`・`access.token.claim: true`・`id.token.claim: false`）。
  - `bff` と `ai-stock-trading-owner` の `defaultClientScopes` へ `ai-stock-trading-api-audience` を足す（他のクライアントには足さない）。
- それまでは段 1 のまま（MSP レルムのトークンは宛先が合わず、記録だけが残る）。

## 段の切り替え（運用）

1. MSP のレルムへ上記の client scope を足す（基盤の変更・MSP#2419）。**本 PR は MSP#2419 の反映後に配備する。** 先に入れた場合は、所有者の操作のたびに Warning が 1 行出るため、ログの量とアラートの閾値を見る（段 1 なので認可の結論は変わらない）。
2. 段 1 のまま配備し、各サービスのログに「所有者のトークンの宛先またはクライアントが許可集合に無い」が出ないことを確かめる（出たら azp / aud を見て許可集合を構成するか、送り手の client scope を直す）。
3. `global.ownerTokenBinding.mode=Enforce` で描画する。
4. 段 2 にしたら `docs/security/security.md` の T-3（「段で導入中」の記述）を更新する。

## 母集合（規則 9）

- ポリシーの登録: `AuthExtensions.AddAiStockTradingAuth` の 1 か所（全 8 サービスが通る）。
- 判定を評価する試験: `OwnerTokenBindingPolicyTests`（新規）。既存の試験は段 1 の既定で挙動不変。
- 配備: `templates/deployment.yaml`・`values.yaml`。Keycloak: `infra/keycloak/realm-export.json`（AST の単体レルム）。MSP のレルムは基盤の変更（上記）。文書: `docs/security/security.md`（T-3・未決 5）・chart README。

## 受け入れ基準

- [x] 段 1 では従前どおり通り、合わないトークンの記録が残る
- [x] 段 2 では aud / azp が合わないトークンは拒否される
- [x] サービスの s2s の判定は変えない
- [x] 構成の誤り（配列でない・段 2 で空・知らない段）は起動で止まる

## 範囲外（残余）

- JwtBearer の `ValidateAudience` 自体は false のまま（s2s のトークンの aud の構成は基盤の管掌）。所有者の分岐だけを要件で確かめる。
- `RequireHttpsMetadata=false` は据え置き（クラスタ内の Keycloak は http。#1529 の範囲で扱う）。
