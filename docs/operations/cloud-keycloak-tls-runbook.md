---
title: 運用 Runbook — クラウドの認可サーバー（Keycloak）への通信を TLS にする（取引ユニットの受け取り方・移行の段・旧の発行元を外す時期）
type: runbook
status: draft
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
---
<!-- trace:
ids: [NFR, NFR-05]
adrs: [ADR-0006, ADR-0080]
iadrs: [IADR-0629, IADR-0621, IADR-0623, IADR-0612, MSP:IADR-0693]
specs: [20261107_cloud-keycloak-tls]
issues: [MSP#2492]
-->

# 運用 Runbook: クラウドの認可サーバー（Keycloak）への通信を TLS にする

> **運用仕様書（[operations.md](operations.md)）の下位にあたる手順書である。** クラウドへの移行の全体は[クラウド移行 Runbook](cloud-single-node-migration-runbook.md)にある。
>
> 🔴 **本書の手順はすべてオーナーが行う。** 配備・クラスタの操作は、AI（コーディングエージェント）は行わない。
>
> **状態を `draft` に留めている理由**: クラウドのノードで移行をまだ流していない。

## 何が変わるか

基盤（MSP）は、クラウドの Keycloak を HTTPS（8443）だけで受ける形にする。サーバ証明書は基盤の OpenBao の PKI が出し、DB・ブローカと**同じ CA** である。
取引ユニットは、その受け手の側を `values-cloud.yaml` の値で切り替える。

| 項目 | 平文の構成（旧） | TLS の構成（`values-cloud.yaml`） |
| --- | --- | --- |
| Authority（`global.authAuthority`） | `http://keycloak:8080/realms/platform` | `https://keycloak:8443/realms/platform` |
| トークンの取得先 | Authority から導く（http） | Authority から導く `https://keycloak:8443/realms/platform/protocol/openid-connect/token`（`ServiceAuth__TokenEndpoint`・Discord の `Notifications__Discord__OwnerAuth__TokenEndpoint`・LLM ゲートウェイの `LlmGateway__Auth__Authority`。values にリテラルを書かない） |
| メタデータの取得 | 平文を許す | `Auth__RequireHttpsMetadata=true`。**https でない Authority では認証を持つサービスが起動で止まる** |
| サーバ証明書の検証 | なし | 基盤の CA（Secret `platform-infra-ca`。ESO が `cloud/shared/platform-infra-ca` から写す既存の写し）を `/etc/platform-infra-ca` へマウントし、`SSL_CERT_DIR=/etc/ssl/certs:/etc/platform-infra-ca`（既定のストアを先に残す） |
| CA を持つ Pod | DB・ブローカの TLS を使う Pod | **Keycloak を呼ぶ 11 個の Worker すべて**（DB・ブローカを使わない Pod も。CA のマウントと `SSL_CERT_DIR` は 1 つだけ） |

- 切り替えは `global.authTls.enabled`（既定 `false`）。`values-cloud.yaml` だけが `true` にする。既定（`values.yaml`）と経路 B（`values-local.yaml`）の描画は変わらない（バイト等価。CI の helm.yml が固定する）。
- `global.authTls.enabled=true` で Authority が `https://` でない・CA の設定（`global.infraCa`）が無いときは、描画で止まる（黙って平文へ戻らない）。
- 取引ユニットの chart には Keycloak への出の NetworkPolicy は無い（出は絞っていない）。直すポートは無い。
- CronJob（`tradingCycle.cronjob`）はクラウドでは無効である。基盤の CA を持たず、基盤の Keycloak の入りの一覧にも無い。クラウドで有効にするなら、両方を先に足す。

## 移行の段（基盤の手順書の段 0〜6 との対応）

基盤の手順書（クラウドの Keycloak の TLS の Runbook）の段 0〜3 を基盤の側で先に済ませる。取引ユニットの作業は**段 4** と**段 6** である。

| 段 | 誰が | 何をするか |
| --- | --- | --- |
| 0〜2 | 基盤 | PKI の role・証明書の写し・Keycloak を併存（8080 と 8443 の両方）にする |
| 3 | 基盤 | 基盤の受け手（LLM ゲートウェイ）を https にする（移す間だけ旧の発行元も受ける） |
| **4** | **取引ユニット** | **本書の §段 4** |
| 5 | 基盤 | 平文の口（8080）を閉じる |
| **6** | **取引ユニット**（と基盤） | **本書の §段 6**（旧の発行元の併記を外す） |

### 段 4: 取引ユニットを https にする

前提: 基盤の段 2 が済み、Keycloak が 8080 と 8443 の両方で受けている（`kubectl -n platform-infra get svc keycloak -o jsonpath='{.spec.ports[*].port}'` に 8443 がある）。
前提: 名前空間 `ai-stock-trading` の Secret `platform-infra-ca` が同期している（`kubectl -n ai-stock-trading get externalsecret platform-infra-ca`）。

1. 移す間だけ、旧の発行元（平文の口で取られたトークンの iss）も受ける形で配備する。

   ```sh
   helm upgrade --install ast deploy/helm/ai-stock-trading \
     -f deploy/helm/ai-stock-trading/values-cloud.yaml -f <digest のファイル> \
     --set-string global.authValidIssuers=http://keycloak:8080/realms/platform    # 移す間だけ（段 6 で外す）
   ```

   - 認証を持つ 8 サービスに `Auth__ValidIssuers` が描かれる。発行元の検証は弱めない（メタデータの発行元に足すだけ）。
2. 確かめ:
   - `kubectl -n ai-stock-trading get deploy` の 11 サービスが Ready（https でない Authority なら起動で止まる）。
   - Pod の env に平文の Keycloak の URL が残っていない:
     `kubectl -n ai-stock-trading get deploy -o yaml | grep -c 'http://keycloak:8080'` が、`Auth__ValidIssuers` の 8 行だけ（値の中の旧の発行元）。
   - 各サービスのログに、メタデータの取得の失敗（`IDX20803`）・トークンの取得の失敗が出ない。出たら CA のマウント・`SSL_CERT_DIR`・基盤の NetworkPolicy の 8443 を確かめる。
   - 取引の判断・報告書・Discord の操作が 401 にならない。
3. 基盤の段 5（平文を閉じる）へ渡す。

### 段 6: 旧の発行元の併記を外す

**いつ**: 基盤の段 5（平文を閉じる）が済み、**トークンの寿命（realm の既定）が過ぎてから**。平文の口で取られたトークンが残っていない時点である。基盤の受け手の併記を外す（基盤の段 6）のと同じ時期に行う。

1. `--set-string global.authValidIssuers=…` を付けずに、段 4 の 1 を流し直す。
2. 確かめ: `kubectl -n ai-stock-trading get deploy -o yaml | grep -c Auth__ValidIssuers` が 0。`grep -c 'http://keycloak'` も 0。

### 戻し方

- 段 4〜5 の間（Keycloak は併存のまま）: `values-cloud.yaml` の前の版（http の Authority・`authTls` なし）で helm を流し直す。平文の呼び手はそのまま動く。
- 基盤の段 5 の後: 平文の口は無い。取引ユニットだけを平文へ戻すことはできない（基盤が併存の段へ戻すのが先）。原因（CA・到達・発行元）を直して段 4 を流し直す。

## Keycloak を呼ぶサービスを足すとき

🔴 **基盤の Keycloak の入りは、取引ユニットの 11 個の Pod（label `app`）の 8443 だけを許す**（基盤の `deploy/cloud/infra/networkpolicy.yaml`）。

- Keycloak を呼ぶ Worker（Authority・トークンの取得先を持つもの）を足したら、**基盤（MSP）へその一覧に足すよう依頼する**。足さなければ、そのサービスの起動・呼び出しが落ちる。
- 取引ユニットの CI（helm.yml の「Assert values-cloud authorization server TLS」）は、CA を持つ Worker の集合を同じ 11 個の一覧と比べる。足すとこの検査が赤になるので、一覧を直すときに基盤への依頼も出す。
- 新しい Worker は chart の `services` に足せば CA のマウントと `SSL_CERT_DIR` が付く（個別の設定は要らない）。

## 証明書の更新

- サーバ証明書の作り直しは基盤の側（ESO が半年ごと・Keycloak が 1 時間ごとに読み直す）。取引ユニットの Pod の作り直しは要らない。
- 基盤が **CA** を作り直したら、`cloud/shared/platform-infra-ca` の更新を ESO が写した後に、取引ユニットのサービスを作り直す（`kubectl -n ai-stock-trading rollout restart deploy`）。DB・ブローカと同じ手順である（[クラウド移行 Runbook](cloud-single-node-migration-runbook.md) §5.4）。
