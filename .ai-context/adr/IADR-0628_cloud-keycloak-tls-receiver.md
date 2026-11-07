---
title: IADR-0628 クラウドの認可サーバー（Keycloak）への通信を TLS にする受け手の段。values-cloud は https の Authority と global.authTls で、全 Worker に基盤の CA を信頼ストアへ足し、認証を持つサービスは https でない Authority で起動を止める。移行の間だけ旧の発行元を足す口を持つ
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-05, ADR-0006, ADR-0080, IADR-0621, IADR-0623, IADR-0612, IADR-0324, MSP:IADR-0693]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0080_cloud-poc-gate-in-cluster-credentials-and-pre-secret-reachability.md
---

# IADR-0628: クラウドの認可サーバー（Keycloak）への通信の TLS（取引ユニットの受け手の段）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。

- 状態: **Accepted**
- 日付: 2026-11-07
- 決定者: 所有者の裁定（2026-11-07「OpenBao の PKI で TLS にする」）・Claude Code（方式）・オーケストレーター（受け取り方は基盤の手順書 §3 に沿う、の指示）

## 起点・関連

- 基盤の側: MSP#2492・MSP:IADR-0693（Keycloak を HTTPS 8443 だけで受ける。証明書は OpenBao の `pki_cloud`＝DB・ブローカと同じ CA）。本 IADR はその手順書の段 4（取引ユニットを https にする）。
- 前提: IADR-0621（基盤への接続の TLS。CA は `SSL_CERT_DIR` に足す）・IADR-0623（CA の写しは ESO が Secret `platform-infra-ca` に作る）・
  IADR-0612（Authority は配備の values で明示）・IADR-0324（Keycloak の URL は `global.authAuthority` の 1 つから導く）。
- 作業仕様書: [`.ai-context/specs/20261107_cloud-keycloak-tls.md`](../specs/20261107_cloud-keycloak-tls.md)（現状・母集合・実測）。
- 手順書: `docs/operations/cloud-keycloak-tls-runbook.md`。

## コンテキストと課題

- 基盤はクラウドの Keycloak を HTTPS だけにする。取引ユニットは Authority を http に固定しており、受け手は `RequireHttpsMetadata = false` 固定だった。
- 取引ユニットの 11 個の Worker はどれも Keycloak を呼ぶ（認証を持つ 8 個は鍵の取得、7 個はトークンの取得、notification は Discord の所有者のトークン）。
  基盤の Keycloak の入りは、この 11 個（label `app`）の 8443 だけを許す。
- いまは 11 個とも CA を持つが、それは DB・ブローカの TLS の部品としてであり（`$infraCaMount := or $dbTls $mqTls`）、認可サーバーの TLS とは結び付いていない。
- 移す間（基盤の段 2〜5）は Keycloak が 8080 と 8443 の両方で受け、発行元は引いた URL で決まる。平文で取られたトークンの iss は http になる。

## 決定

### 決定 1 — chart の切り替え `global.authTls.enabled`（既定 false）。values-cloud だけが有効にする

- 有効なら: Authority が `https://` でなければ描画で止める。`global.infraCa` が無ければ止める（黙って平文へ戻らない）。
  全 Worker に基盤の CA をマウントする（`$infraCaMount` に加える。DB・ブローカの TLS を外しても付く）。
  `SSL_CERT_DIR=/etc/ssl/certs:/etc/platform-infra-ca` は、ブローカの amqps の段が同じ値を描かないときだけ描く（1 つだけ。二重に足さない）。
  認証を持つサービス（`auth: true`）へ `Auth__RequireHttpsMetadata=true` を渡す。
- values-cloud: `global.authAuthority: https://keycloak:8443/realms/platform`。トークンの取得先（`ServiceAuth__TokenEndpoint`・Discord の OwnerAuth・
  LLM ゲートウェイの Authority）は従来どおり Authority から導く（values にリテラルを置かない。IADR-0324）。
- 既定・values-local・compose は変えない（描画はバイト等価。CI の helm.yml が固定する）。
- 採らなかった: 認可サーバーの CA を DB・ブローカの部品に任せる（いまの形のまま） — DB・ブローカの TLS の切り戻し（false は切り戻し専用）で、
  認可サーバーの検証まで黙って外れる。

### 決定 2 — 受け手は `Auth:RequireHttpsMetadata` を読み、true で Authority が https でなければ登録の時点で止める

- 未設定・false は従来どおり。true なら JwtBearer の `RequireHttpsMetadata` を true にし、Authority が https の絶対 URL でなければ例外
  （サービスの起動で止まる。JwtBearer 自身の拒否は最初の要求の時点で、起動では止まらない）。真偽値でない値も例外（黙って false に倒さない）。
  基盤の `ResolveRequireHttpsMetadata` と同じ型にする。取引ユニットには `Auth:MetadataAddress` の口が無いので、見るのは Authority だけである。
- サーバ証明書の検証はシステムの信頼ストア（`SSL_CERT_DIR`）が行う。JwtBearer の鍵の取得・サービスの資格のトークンの取得・Discord の所有者のトークンの取得の
  どれもが、追加の配線なしに同じ検証を受ける（基盤の MSP:IADR-0693 決定 3・IADR-0621 のブローカと同じ型）。

### 決定 3 — 移行の間だけ旧の発行元を足す口 `Auth:ValidIssuers`（chart は `global.authValidIssuers`。既定は空）

- 与えたら JwtBearer の `ValidIssuers` に入れる。JwtBearer はメタデータの発行元を常に受理集合へ併合するので、これは「足す」だけで発行元の検証は弱めない。
- 段 4 では `--set-string global.authValidIssuers=http://keycloak:8080/realms/platform` を付けて配備し、**基盤の段 5（平文を閉じる）の後、トークンの寿命が過ぎてから**
  付けずに流し直す（基盤の段 6 と同じ時期。手順書 §段 6）。values-cloud には書かない（外し忘れを作らない）。

### 決定 4 — Keycloak を呼ぶ Worker の一覧を描画の検査で固定する

- helm.yml の「Assert values-cloud authorization server TLS」は、CA と `SSL_CERT_DIR` を持つ Worker の集合を 11 個の一覧と比べる。
  Worker を足すとこの検査が赤になる。そのときに基盤の Keycloak の入りの一覧（MSP の `deploy/cloud/infra/networkpolicy.yaml`）にも足すよう基盤へ依頼する
  （足さなければ、そのサービスの起動・呼び出しが落ちる。values-cloud・chart の README・手順書に書いた）。
- 取引ユニットの chart に Keycloak への出の NetworkPolicy は無い（出は絞っていない）。直すポートは無い。

## 結果

- 良い影響: クラウドでトークンの取得（クライアントの secret を含む）と鍵の取得が TLS になり、サーバ証明書を基盤の CA で検証する。
  https でない Authority は起動で止まる。CA の付与が DB・ブローカの TLS の切り替えから独立する。
- 悪い影響: 移行に段 4・段 6 の 2 回の配備が要る。Worker を足すときに基盤への依頼が要る。
- 試験の側: values-cloud の描画を http の Authority で上書きしていた 2 つの検査（helm.yml の「values-cloud drops no env」・`scripts/helm-pod-security.test.js`）は、
  レルムを宣言するプロファイルを上書きしない形へ直した（`--set` は `-f` より常に勝つ。従来のコメント「後ろの args が勝つ」は誤りだった）。

## 残余

1. クラスタでしか確かめられないこと（証明書の検証・トークンの取得・基盤の NetworkPolicy の 8443）は、基盤の段 4 の確かめで見る。
2. CronJob（`tradingCycle.cronjob`）はクラウドで無効のまま。基盤の CA を持たず、基盤の入りの一覧にも無い。有効にするなら両方を先に足す。
3. 基盤の CA はシステムの信頼ストアに入る（IADR-0621 のブローカと同じ。外の宛先の検証にも効く）。CA の鍵は OpenBao の外へ出ない（MSP:IADR-0693 残余 2）。
