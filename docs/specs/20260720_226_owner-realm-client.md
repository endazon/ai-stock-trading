---
title: dev realm に Discord Bot 制御コマンド用 OwnerAuth 機密クライアントを追加する
type: work
status: done
related_ids: [FR-10, FR-14, UC-06, UC-07, ADR-0007, ADR-0009]
author: endazon (with Claude Code)
created: 2026-07-20
updated: 2026-07-20
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_kill-switch-authz.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0009_pause-resume.md
---

# 作業仕様書: dev realm の OwnerAuth 機密クライアント追加（Discord 制御コマンド疎通）

> Issue [#226](https://github.com/endazon/ai-stock-trading/issues/226)（live 検証で判明したギャップ）を対象とする。
> `Refs #226`。関連 [#148](https://github.com/endazon/ai-stock-trading/issues/148)（IADR-0062 Bot owner 配線）/
> [#165](https://github.com/endazon/ai-stock-trading/issues/165)（段階ゲート Bot コマンド）/
> [#152](https://github.com/endazon/ai-stock-trading/issues/152)（pause/resume）。設計判断は
> [IADR-0098](../adr/IADR-0098_owner-realm-client.md)。

## 前提の確認結果（着手前調査・実コードで裏取り）

- **制御コマンドの認可先はすべて OwnerOnly = レルムロール `trading-owner` 単独**。
  - kill switch / pause・resume / `/status`: `RiskControlEndpointsTests.cs`（`Owner = "trading-owner"`・trading-service は 403）。
  - stage 遷移: `StageGateEndpointsTests.cs:17`（`Owner = "trading-owner"`）。
  - `Http{KillSwitch,Pause,StageGate}Controller.cs` のコメントも「OwnerOnly（trading-owner）」と明記。
  - → **追加ロールは `trading-owner` のみで十分**（最小権限。kill switch/pause/stage 個別のロールは存在しない）。
- **owner トークンの構成**（`DiscordOwnerAuthExtensions.cs`）: セクション `Notifications:Discord:OwnerAuth` の
  `ClientId`/`ClientSecret`/`TokenEndpoint`（未指定なら `Auth:Authority` から導出）/`Scope`/`RefreshSkewSeconds`。
  `IsEnabled` は **ClientId・ClientSecret・TokenEndpoint が全て揃うこと**（`ServiceAuthOptions.IsEnabled`）。欠ければ
  ハンドラを付けない → Risk が 401（fail-safe）。
- **realm のギャップ**: `infra/keycloak/realm-export.json` の client は `ai-stock-trading-dev`(public) と
  `ai-stock-trading-svc`(trading-service・`serviceAccountsEnabled`) のみ。**owner マップの機密 client が不在** →
  Bot の owner トークン発行先が無く、制御コマンドが 401/403 になる。
- **helm のギャップ**: `values.yaml` は `discord-owner-auth-client-id`/`-secret` を `ast-secrets` 経由で既に配線済み。
  だが notification サービスは `auth: true` を持たず `Auth__Authority` が注入されない上、
  `Notifications__Discord__OwnerAuth__TokenEndpoint` も未設定 → **TokenEndpoint 未解決で `IsEnabled=false`**。
  client-id/secret を入れても token が付かず 401。加えて `scripts/k8s-local-deploy.sh` の ast-secrets 生成に
  owner-auth の dev 既定が無い（service-auth は既定 `ai-stock-trading-svc`/`dev-only-service-secret` を持つ）。

## 課題（本作業で埋める穴）

dev レルムに owner マップの機密 client を追加し、helm 側で TokenEndpoint と ast-secrets の dev 既定を解決可能にして、
**マージ→realm 再インポートで Discord 制御コマンド（`/pause`・`/resume`・`/killswitch`・`/stage`）が通る**状態にする。
既定は Bot 無効（opt-in）を維持し、平文の本番秘密はコミットしない（dev プレースホルダのみ）。

## 実装方針（追加のみ・後方互換）

1. **realm-export.json**（`infra/keycloak/`）: confidential client **`ai-stock-trading-owner`** を追加。
   `publicClient:false`・`serviceAccountsEnabled:true`・`standardFlowEnabled:false`・`directAccessGrantsEnabled:false`・
   dev secret `dev-only-owner-secret`・`redirectUris:[]`・`webOrigins:[]`。`description` は 255 文字以内。
   `users` に service-account ユーザー `service-account-ai-stock-trading-owner`（`serviceAccountClientId` 指定・
   `realmRoles:["trading-owner"]`）を追加。**既存 client / ロール / ユーザーは不変**。
2. **helm templates/deployment.yaml**: notification に `Notifications__Discord__OwnerAuth__TokenEndpoint` を
   `{{ $g.authAuthority }}/protocol/openid-connect/token` として算出注入する（`Auth__Authority` と同一ソース＝
   `--set global.authAuthority` に追随）。`values.yaml` にリテラルを置かない（レビュー指摘の反映・非テンプレート値は
   authAuthority 変更に追随せず 401 が別経路で再発するため）。既存の client-id/secret 配線は不変。
3. **scripts/k8s-local-deploy.sh ＋ docker-compose.yml**: owner-auth の dev 既定を service-auth と同型で追加
   （`ClientId`=`ai-stock-trading-owner`・`ClientSecret`=`dev-only-owner-secret`・環境変数で上書き可）。
   docker-compose は TokenEndpoint を `KEYCLOAK_REALM` に追随させる。両ローカル経路（k8s-local / docker-compose）で
   realm 再インポートだけで制御コマンドが通るようにする。Bot 自体は `Enabled:false` 既定のため opt-in を維持。
4. **README 注記**: `infra/README.md` に owner client の説明とローカル反映手順（realm 再インポート）を追記。
   `deploy/helm/ai-stock-trading/README.md` の ast-secrets 表に owner-auth 行を追加。`docs/adr/README.md` に
   IADR-0098 の 1 行を追加。

## 受け入れ基準

- [x] `infra/keycloak/realm-export.json` に `ai-stock-trading-owner`（confidential・service-account）が追加され、
      service-account に `trading-owner` が割り当たる。既存 client（dev/svc）は不変。→ JSON 妥当性・差分レビュー。
- [x] `description` は 255 文字以内（216 文字）。
- [x] helm で `Notifications__Discord__OwnerAuth__TokenEndpoint`（AST レルム token エンドポイント）が
      `global.authAuthority` から導出され解決される。→ `helm template` で notification Deployment の env に出現。
- [x] `scripts/k8s-local-deploy.sh` の ast-secrets ／ `docker-compose.yml` に owner-auth の dev 既定が入り、
      再デプロイ／再インポートで OwnerAuth の `IsEnabled` 条件（client-id/secret/token-endpoint）が満たされる。
- [x] `helm lint --strict` OK・`helm template`（既定＋派生）OK。既定は Bot 無効/opt-in を維持。
- [x] 平文の本番秘密をコミットしない（dev プレースホルダ `dev-only-owner-secret` のみ）。

## スコープ外

- kill switch 解除フレーズ検証（[#223](https://github.com/endazon/ai-stock-trading/issues/223)・NotificationService コード）。
  本作業は realm-export.json＋helm の secret/env 配線＋docs に閉じ、kill switch ハンドラのロジックには触れない。
- 実ブラウザ / 実 Discord 疎通（マージ後にユーザーが realm 再インポートで確認）。
- MSP 側 realm（`microservices-platform`）は不変。本 client は AST レルムに閉じる。

## ローカル反映手順（PR/issue に明記）

1. `git pull` で develop を最新化し、`infra/keycloak/realm-export.json` を取り込む。
2. **docker-compose**: Keycloak の realm import は初回起動のみ有効。既存ボリュームを破棄して再インポート
   （`docker compose down -v keycloak` 相当 → `docker compose up -d keycloak` で `--import-realm`）。
3. **k8s-local（MSP 連結）**: realm を ConfigMap で配る構成なら ConfigMap 再作成 → keycloak Pod restart。
   `scripts/k8s-local-deploy.sh` を再実行して ast-secrets（owner-auth dev 既定）を更新。
4. Bot を有効化（`Notifications__Discord__Bot__Enabled=true` ＋ Token/多層認証）し、`/pause` 等で 200 応答を確認。
