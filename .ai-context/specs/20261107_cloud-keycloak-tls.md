---
title: クラウドの認可サーバー（Keycloak）への通信を TLS にする（取引ユニットの受け手の段。https の Authority・全 Worker に基盤の CA・起動で止める・移行の間の旧の発行元）
type: spec
status: accepted
related_ids: [NFR, NFR-05, ADR-0006, ADR-0080, IADR-0628, IADR-0621, IADR-0623, IADR-0612, IADR-0324, MSP:IADR-0693]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0080_cloud-poc-gate-in-cluster-credentials-and-pre-secret-reachability.md (決定 1 クラスタの中の資格情報と TLS の秘密鍵も OpenBao から供給する)
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-05)
---

# 仕様書: クラウドの認可サーバー（Keycloak）への通信を TLS にする（取引ユニットの受け手の段）

## 起点

- 所有者の裁定（2026-11-07）: 「OpenBao の PKI で TLS にする」。クラスタ内の認可サーバーへの通信を TLS にする。
- 基盤の側は MSP#2492（ブランチ `fix/NFR-cloud-keycloak-tls`・MSP:IADR-0693）。Keycloak を HTTPS（8443）だけで受け、サーバ証明書は OpenBao の
  `pki_cloud`（DB・ブローカと同じ CA）が出す。基盤の手順書 `docs/operations/cloud-keycloak-tls-runbook.md` の §3「取引ユニットの受け取り方」と段 0〜6。
- 本 PR は基盤の段 4（取引ユニットを https にする）に当たる。**MSP#2492 が先にマージされる前提**である。
- 非機能の番号: 計画の非機能要件表に通信の暗号化の番号は無い（`NFR-16` は差し替え・`NFR-18` はログの完全性で、基盤の番号とは意味が違う）。
  起点は裸の `NFR` とし、秘密の保管の `NFR-05` を併記する（クライアントの secret がトークンの取得で平文を流れなくなる）。
- 決定は [IADR-0628](../adr/IADR-0628_cloud-keycloak-tls-receiver.md)。

## 1. 現状（develop `c58d31173`。shallow ではない）

`helm template … -f values-cloud.yaml -f ci/values-cloud-digests.fixture.yaml` の描画（helm v4.2.1。CI と同じ版）を Deployment ごとに読んだ。

| Deployment | Keycloak の URL を持つ env | CA のマウント・`SSL_CERT_DIR` |
| --- | --- | --- |
| audit・configuration・risk-management | `Auth__Authority` | あり（DB・ブローカの TLS の部品として） |
| cost-control・information-collection・market-monitor・order-execution | `Auth__Authority`・`ServiceAuth__TokenEndpoint` | あり |
| report | 上の 2 つ＋`LlmGateway__Auth__Authority` | あり |
| backtest | `ServiceAuth__TokenEndpoint` | あり |
| trade-decision | `ServiceAuth__TokenEndpoint`・`LlmGateway__Auth__Authority` | あり |
| notification | `Notifications__Discord__OwnerAuth__TokenEndpoint` | あり |
| opend | なし | なし |

- URL はすべて `global.authAuthority` から chart が導く（values にリテラルは無い。IADR-0324 の構造）。トークンの取得先を別に https にする箇所は無い。
- CA のマウントの条件は `$infraCaMount := or $dbTls $mqTls`（chart 全体で 1 つ）。values-cloud はどちらも有効なので**いまは** 11 個とも持つが、
  認可サーバーの TLS とは結び付いていない（DB・ブローカの TLS を外すと CA も消える）。
- `SSL_CERT_DIR` はブローカの amqps の段だけが描く（ブローカは全 Worker が使う）。
- 受け手 `AuthExtensions.AddAiStockTradingAuth` は `RequireHttpsMetadata = false` 固定。`ValidIssuers` の口は無い。
- chart の NetworkPolicy は OpenD の入りだけ（`networkpolicy-opend.yaml`）。Keycloak への出の規則は無い（直すポートが無い）。
- CronJob（`tradingCycle.cronjob`）は `TOKEN_ENDPOINT` を Authority から導くが、values-cloud では無効。

## 2. 母集合（規則 9: 誤りの側の文字列で走査した）

- `grep -rn 'keycloak:8080\|8080/realms/platform'`（`.ai-context/` を除く全ファイル）: クラウドの値は `values-cloud.yaml` の 1 か所（`authAuthority`）だけ。
  他は既定・経路 B（`values-local.yaml`・`docker-compose.yml`・`appsettings.Development.json`・`.env.example`）、試験の固定値、既定の描画の検査
  （helm.yml の `CI_AUTH_AUTHORITY`・`scripts/helm-pod-security.test.js`・`scripts/helm-release-drift.js` の案内文）、文書（`docs/security/security.md` の経路 B の行）。
  既定・経路 B は変えないので対象外。
- 描画の中の Keycloak の URL（`value: "…://keycloak…"`）: values-cloud で 18 行。Deployment 11 個（上表）。
- values-cloud の描画を `--set global.authAuthority=http://…` で上書きして描く検査（規則 10: この変更で誤りになる記述）:
  helm.yml の「Assert values-cloud drops no env from prod default」と `scripts/helm-pod-security.test.js` の `render()`。どちらも `--set` が `-f` より
  常に勝つことで values-cloud の Authority を http へ戻していた（コメントの「後ろの args が勝つ」「values-cloud は明示済みで同じ値」は誤り）。
  本 PR で values-cloud に `authTls` を入れると描画が止まるため、レルムを宣言するプロファイルには上書きしない形へ直す。
- 基盤の Keycloak の入りの一覧（基盤 `deploy/cloud/infra/networkpolicy.yaml`）の 11 個は、上表の opend を除く 11 個と一致する。

## 3. 作るもの

1. chart: `global.authTls.enabled`（既定 false）。有効なら
   - Authority が `https://` でなければ描画で止める。`global.infraCa` が無ければ止める。
   - `$infraCaMount` に加える（全 Worker に CA。DB・ブローカの TLS を外しても付く）。
   - `SSL_CERT_DIR=/etc/ssl/certs:/etc/platform-infra-ca` は、ブローカの amqps の段が描かないときだけ描く（1 つだけ）。
   - 認証を持つサービス（`auth: true`）へ `Auth__RequireHttpsMetadata=true`。
2. chart: `global.authValidIssuers`（既定は空＝描かない）。与えたら認証を持つサービスへ `Auth__ValidIssuers`。
3. values-cloud: `authAuthority: https://keycloak:8443/realms/platform`・`authTls.enabled: true`。サービスを足すときに基盤の一覧も足す旨のコメント。
4. 受け手: `Auth:RequireHttpsMetadata`（未設定・false は従来・true で Authority が https でなければ登録の時点で例外・真偽値でなければ例外）と
   `Auth:ValidIssuers`（カンマ・空白区切り。空は足さない）。基盤の `ResolveRequireHttpsMetadata` と同じ型。
5. 描画の検査（helm.yml）と手順書 `docs/operations/cloud-keycloak-tls-runbook.md`。

## 4. 受け入れ基準

- [x] values-cloud の 11 個の Worker が、基盤の CA のマウントを 1 つ・`SSL_CERT_DIR` を 1 つ（値は `/etc/ssl/certs:/etc/platform-infra-ca`）持つ。DB・ブローカの TLS を外しても同じ。
- [x] values-cloud の Keycloak の URL はすべて `https://keycloak:8443/realms/platform` で始まり、`http://keycloak` は無い。トークンの取得先 8 件も https。
- [x] 認証を持つ 8 サービスに `Auth__RequireHttpsMetadata=true`。`Auth__ValidIssuers` は既定で描かず、与えたときだけ 8 サービスに描く。
- [x] `authTls` で http の Authority・CA の設定の欠落は描画で止まる。
- [x] 既定・values-local・values-grpc-measurement・values-local＋CronJob の描画は develop とバイト等価。
- [x] 受け手: 既定は false・追加の発行元なし。true で https でなければ起動で止まる（Authority なし＝コードの既定の http も止まる）。真偽値でなければ止まる。
- [x] 修正を外すと検査が赤になる（§5）。

## 5. 実測

- helm v4.2.1（公式の配布物。sha256 を配布元の値と照合）を手元に置き、helm.yml の chart ジョブの `run` を全段そのまま流した: 全段緑。
- バイト等価: develop の chart と本ブランチの chart で、既定・values-local・values-grpc-measurement・values-local＋`tradingCycle.cronjob.enabled=true` の描画の md5 が一致。
- 変異（新しい段「Assert values-cloud authorization server TLS」が赤になること）:

  | 変異 | 結果 |
  | --- | --- |
  | `$infraCaMount` から `$authTls` を外す | 赤（DB・ブローカの TLS なしで CA が無い Worker） |
  | 追加の `SSL_CERT_DIR` を消す | 赤（同上で `SSL_CERT_DIR` が 0） |
  | `SSL_CERT_DIR` の条件から `not $mqTls` を外す | 赤（`SSL_CERT_DIR` が 2 つ） |
  | values-cloud を元に戻す（http・`authTls` なし） | 赤 |
  | `Auth__RequireHttpsMetadata` を描かない | 赤（8 件中 0 件） |
  | values.yaml の既定を `authTls.enabled=true` にする | 赤（既定の描画が止まる） |
  | notification だけ CA のマウントを外す | 赤（そのサービスを名指し） |
  | 変異なし | 緑 |

- 受け手の試験 `AuthRequireHttpsMetadataTests`: 実装の `RequireHttpsMetadata` を false 固定へ戻すと true の場合が赤、例外を外すと起動で止まる場合が赤（§6 の手順で確かめた）。

## 6. 範囲外・残り

- クラスタでしか確かめられないこと（Keycloak の証明書の検証・トークンの取得・基盤の NetworkPolicy の 8443）は、基盤の段 4 の確かめで見る（手順書）。
- CronJob はクラウドで無効のまま（基盤の CA を持たず、基盤の入りの一覧にも無い）。有効にするなら両方を先に足す（手順書に記載）。
