---
title: IADR-0098 Discord Bot 制御コマンドの OwnerAuth は AST レルムの専用 confidential client `ai-stock-trading-owner`（trading-owner 単独）で認証し、helm では TokenEndpoint を明示して IsEnabled を成立させる
type: impl-adr
status: Accepted
related_ids: [FR-10, FR-14, UC-06, UC-07, ADR-0007, ADR-0009]
author: endazon (with Claude Code)
created: 2026-07-20
updated: 2026-07-20
plan_refs:
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0007_kill-switch-authz.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0009_pause-resume.md
---

# IADR-0098: Discord Bot 制御コマンドの OwnerAuth は AST レルムの専用 confidential client でクロスに認証する

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-20
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: **FR-10**（リスク統制・kill switch）、**FR-14**（双方向 Bot）、UC-06（段階ゲート運用）、
  UC-07（一時停止/再開）、**ADR-0007**（kill switch 認可＝利用者のみ）、**ADR-0009**（pause/resume）
- 対象 Issue: [#226](https://github.com/endazon/ai-stock-trading/issues/226)（live 検証で判明したギャップ）。`Refs #226`
- 関連する実装仕様書: [20260720_226_owner-realm-client](../specs/20260720_226_owner-realm-client.md)
- 関連 IADR:
  [IADR-0062](IADR-0062_discord-bot-gateway-and-authorization.md)（Bot が kill switch を trading-owner 専用クライアントで叩く owner 配線の起点＝`DiscordOwnerAuthExtensions`。**本 ADR はその dev レルム側の client 実体を補う**）、
  [IADR-0051](IADR-0051_service-to-service-auth.md)（s2s client_credentials 基盤・`ai-stock-trading-svc`/`trading-service`。owner とは別クライアント/別ロール）、
  [IADR-0093](IADR-0093_kb-writer-cross-realm-s2s.md)（クロス**レルム** s2s。本 ADR は同一 AST レルム内で完結する点が対照的）

## 背景・課題

Discord Bot の制御コマンド（`/pause`・`/resume`・`/killswitch`・`/stage`）は、[IADR-0062] の owner 配線
（`DiscordOwnerAuthExtensions`・`Http{KillSwitch,Pause,StageGate}Controller`）が、AST レルムの owner マップ機密
クライアント（`trading-owner` ロールの service-account・client_credentials）で RiskManagement の **OwnerOnly**
エンドポイントを叩く設計である。実コードで裏取りした認可要件:

- kill switch / pause・resume / `/status`: OwnerOnly＝レルムロール **`trading-owner` 単独**（`RiskControlEndpointsTests`。
  `trading-service` は 403）。
- stage 遷移: OwnerOnly＝**`trading-owner` 単独**（`StageGateEndpointsTests`）。
- kill switch/pause/stage を細分する個別ロールは**存在しない**。認可はすべて OwnerOnly の一枚岩。

しかし live 検証で **401/403** になった。故障は二段:

1. **dev レルムに owner クライアントが不在**: `infra/keycloak/realm-export.json` の client は
   `ai-stock-trading-dev`(public) と `ai-stock-trading-svc`(trading-service) のみ。owner トークンの発行先が無い。
2. **helm で TokenEndpoint が未解決**: `values.yaml` は `discord-owner-auth-client-id`/`-secret` を `ast-secrets`
   経由で配線済みだが、notification サービスは `auth: true` を持たず `Auth__Authority` が注入されない上、
   `Notifications__Discord__OwnerAuth__TokenEndpoint` も未設定。`ServiceAuthOptions.IsEnabled` は ClientId・
   ClientSecret・**TokenEndpoint** の三点が揃うことを要求するため、token エンドポイント欠落で `IsEnabled=false` →
   トークンを付けない → Risk が 401（fail-safe）。

## 決定

### 1. AST レルムに owner マップの専用 confidential client `ai-stock-trading-owner` を追加する
`infra/keycloak/realm-export.json` に client **`ai-stock-trading-owner`** を追加する:
`publicClient:false`・`serviceAccountsEnabled:true`・`standardFlowEnabled:false`・`directAccessGrantsEnabled:false`・
dev secret `dev-only-owner-secret`・`redirectUris:[]`・`webOrigins:[]`。service-account ユーザー
`service-account-ai-stock-trading-owner` を追加し、**realm role `trading-owner` のみ**を割り当てる（最小権限）。
既存の client / ロール / ユーザーは不変（追加のみ）。

- **owner と service を別クライアントに分ける**理由: [IADR-0051] の `ai-stock-trading-svc` は `trading-service`
  （OwnerOrService read 系のみ）で、書き込み系 OwnerOnly は 403 になる。Bot の制御は「利用者の代理」＝
  `trading-owner` が要る。両者を同一クライアントに相乗りさせると service 経路が過剰権限を持つため、[IADR-0062]
  の設計どおり owner 専用クライアントを分離する。
- **KB（[IADR-0093]）と対照的に、これは AST レルム内で完結する**。制御先 RiskManagement は AST レルム
  （`ai-stock-trading`）の Authority で検証するため、owner クライアントも同一 AST レルムに置く（issuer 一致）。
  クロスレルムにする必要は無い。

### 2. helm では OwnerAuth の TokenEndpoint を明示する（notification に auth:true は付けない）
`values.yaml` の notification extraEnv に `Notifications__Discord__OwnerAuth__TokenEndpoint`
（= `http://keycloak:8080/realms/ai-stock-trading/protocol/openid-connect/token`＝global `authAuthority` と一致）を
**追加する**。notification に `auth: true` を付けて `Auth__Authority` を注入し導出させる案は採らない。

- 理由: notification は OwnerOnly の**受け口を持たない**（inbound JWT 検証が不要な worker）。`auth: true` は
  inbound 認証設定を増やす副作用があり、本課題（outbound owner トークンの発行）には不要。TokenEndpoint を
  明示するのが最小・最安全（`IsEnabled` の三点目を直接満たす）。値は global `authAuthority` と機械的に一致する
  AST レルムの token エンドポイントで、環境が変われば同時に追随する（単一の realm 名で決まる）。

### 3. dev の資格は k8s-local-deploy スクリプトの ast-secrets 既定で解決する。平文の本番秘密は置かない
`scripts/k8s-local-deploy.sh` の `ast-secrets` 生成に owner-auth の dev 既定を [IADR-0051] の service-auth と
同型で追加する（`discord-owner-auth-client-id`=`ai-stock-trading-owner`・`discord-owner-auth-client-secret`=
`dev-only-owner-secret`・環境変数で上書き可）。dev secret は realm-export.json の値と一致する使い捨てプレースホルダで、
本番秘密は Vault/Secret（[IADR-0094] / #24）から注入する（コミットしない）。

### 4. 既定は Bot 無効（opt-in）を厳密保持する
owner クライアント・TokenEndpoint・ast-secrets 既定が揃っても、Bot 自体は
`Notifications__Discord__Bot__Enabled=false` 既定で Gateway に接続しない（[IADR-0062]）。有効化には Token と
多層認証（GuildId/ChannelId/AllowedUserIds/UserMapping・kill switch 確認フレーズ）がすべて要る。本 ADR は
「owner トークンが 401 で失敗する」故障を除くだけで、制御コマンドの発火可否は既存の opt-in ゲートに委ねる。

## 検討した代替案

- **A: `ai-stock-trading-svc`（trading-service）に trading-owner も足す** — 却下。service 経路（自動処理・
  生成 AI）が OwnerOnly の書き込み権限を持ち、[IADR-0051] の最小権限（自動は承認できない）を崩す。
- **B: notification に `auth: true` を付けて Auth:Authority から TokenEndpoint を導出させる** — 却下（決定2の理由）。
  inbound 認証設定を増やす副作用があり、本課題に不要。
- **C: owner クライアントを MSP レルムに置く** — 却下。制御先 RiskManagement は AST レルムで検証するため、
  MSP レルム発行トークンは issuer 不一致で 401 になる（[IADR-0093] とは逆向きの整合）。

## 影響・リスク

- 既定挙動は不変（Bot 無効・ast-secrets 空既定なら owner トークンは付かず現行の 401/no-op）。
- realm-export.json は **dev 専用**。本番へは import しない（`infra/README.md` の警告に従う）。dev secret は
  使い捨てで、漏洩しても dev レルムの owner service-account が使えるだけ（本番の秘密は別管理）。
- `Shared.Contracts`・アプリコードは不変（realm-export.json＋helm＋docs に閉じる）。#223（kill switch 解除
  フレーズ＝NotificationService コード）とは領域が分離しており競合しない。
- ローカル反映は realm 再インポート（docker-compose はボリューム破棄／k8s は ConfigMap 再作成＋restart）が要る。
  手順は仕様書と README に明記する。
