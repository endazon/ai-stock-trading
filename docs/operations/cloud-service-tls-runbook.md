---
title: 運用 Runbook — クラウドの取引ユニットのサービス間の HTTP を TLS へ切り替える（Off → Listen → Call → Enforce）
type: runbook
status: draft
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
---
<!-- trace:
ids: [NFR, NFR-05]
adrs: [ADR-0006, ADR-0080]
iadrs: [IADR-0646, IADR-0639, IADR-0631, IADR-0623, IADR-0632, MSP:IADR-0689]
specs: [20261108_s2s-tls-opend-nonroot]
issues: []
-->

# 運用 Runbook: サービス間の HTTP を TLS へ切り替える

> **運用仕様書（[operations.md](operations.md)）の下位にあたる手順書である。** クラウドへの移行の全体は[クラウド移行 Runbook](cloud-single-node-migration-runbook.md)にある。
>
> 🔴 **本書の手順はすべてオーナーが行う。** 配備・クラスタの操作は、AI（コーディングエージェント）は行わない。
>
> **状態を `draft` に留めている理由**: クラウドのノードでまだ流していない。

## 何が変わるか

`global.serviceTls.mode` を 1 段ずつ上げる。`values-cloud.yaml` の今の値は `"Off"`（従来どおり平文の 8080 だけ）。

| 段 | 受け側 | 呼び手 | probe・通信の制限 |
| --- | --- | --- | --- |
| `Off` | 平文の 8080 | 平文 | 8080 |
| `Listen` | 8080 と 8443（TLS）の両方 | 平文のまま | probe は 8080・制限は両方の口 |
| `Call` | 両方 | `https://<サービス>-service:8443`（chart が書き換える） | probe は 8080・制限は両方の口 |
| `Enforce` | 8443 だけ | TLS | probe は 8443（HTTPS）・制限は 8443 だけ |

- サービス証明書は基盤の OpenBao の PKI が発行し、ESO が Secret `ast-<サービス>-service-tls`（`tls.crt`・`tls.key`）へ写す。鍵は OpenBao の中で作られる。
- 呼び手は受け側の証明書を基盤の CA（Secret `platform-infra-ca`）で確かめる。名前の照合は外さない。
- 🔴 `Listen` 以上では、証明書の Secret が無いと Pod が起動しない（平文へ黙って戻らない。旧の Pod が残る）。

## 前提（基盤の作業。基盤の担当へ依頼する）

このリポジトリでは入れられない。両方が入るまで `"Off"` のままにする。

1. **PKI の role を 11 個足す**: 基盤の `deploy/cloud/vault/lib.sh` の `OB_PKI_ROLES` へ、次の `<サービス>-service:ai-stock-trading` を足し、稼働中の OpenBao へ `root-token.sh apply-pki-roles` で書く。
   - `audit` `backtest` `configuration` `cost-control` `information-collection` `market-monitor` `notification` `order-execution` `report` `risk-management` `trade-decision`（それぞれ `-service` を付ける）。
2. **role `ast-eso` に発行を許す**: 基盤の `deploy/cloud/vault/policies/ast-eso.hcl` に、上の 11 個の `pki_cloud/issue/<サービス>-service` へ `create`・`update` を足し、稼働中の OpenBao へ書く。
   - ワイルドカードにしない（名指しの 11 個）。
3. （任意）所有者のクライアントのアクセストークンの寿命を短くする（基盤の realm の設定）。

## 段 0: 描画の確かめ（手元）

```
for m in Listen Call Enforce; do
  helm template ast deploy/helm/ai-stock-trading -f deploy/helm/ai-stock-trading/values-cloud.yaml -f <§4 のファイル> --set global.serviceTls.mode=$m > /dev/null
done
node scripts/helm-service-tls.test.js
```

## 段 1: Listen（場の時間外）

1. 新規発注の一時停止（pause）を入れる。
2. `helm upgrade ast deploy/helm/ai-stock-trading -f values-cloud.yaml -f <§4 のファイル> --set global.serviceTls.mode=Listen`
3. 確かめる:
   - `kubectl -n ai-stock-trading get externalsecret | grep service-tls` の 11 個がすべて `SecretSynced`。
   - 全 Pod が Ready（probe は平文の 8080 のまま）。
   - 各サービスのログに `Now listening on: https://[::]:8443` が出ている。証明書の読み込みの失敗が無い。
4. 一時停止を解く。

## 段 2: Call（場の時間外）

1. 一時停止を入れる。
2. `helm upgrade … --set global.serviceTls.mode=Call`
3. 確かめる:
   - 全 Pod が Ready。
   - 各サービスのログに、取引ユニットの相手への呼び出しの証明書の検証の失敗（`The remote certificate is invalid`・`RemoteCertificateNameMismatch`）が無い。
   - 発注執行の口座照会・取引判断の 1 周（判断の記録まで）・日報の照会・Discord のボットの読むだけのコマンドが通る。
4. 一時停止を解く。

## 段 3: Enforce（場の時間外）

1. 一時停止を入れる。
2. `helm upgrade … --set global.serviceTls.mode=Enforce`
3. 確かめる:
   - 全 Pod が Ready（probe は 8443 の HTTPS）。
   - 各サービスのログの `Now listening on` が `https://[::]:8443` だけ。
   - 段 2 の 3 の流れが通る。
4. 一時停止を解く。
5. **確かめた段を `values-cloud.yaml` の `global.serviceTls.mode` に書く**（`--set` を付け忘れた helm で平文へ戻らないように。変更は PR で入れる）。

## 証明書の作り直し

- 発行の期限は 90 日（`global.serviceTls.pki.ttl`）。ESO は 30 日ごと（`refreshInterval`）に作り直す。
- 作り直した後は、読み直させるために 11 サービスを作り直す（クラウドに Reloader は無い）:
  `kubectl -n ai-stock-trading rollout restart deploy -l 'app.kubernetes.io/part-of=ai-stock-trading,app!=opend'`
  - 🔴 OpenD（`deploy/opend`）は作り直さない（SMS 認証済みのセッションが切れる）。先に `kubectl -n ai-stock-trading get deploy -l 'app.kubernetes.io/part-of=ai-stock-trading,app!=opend'` で 11 個だけが出ることを確かめる。
- 期限の確かめ: `kubectl -n ai-stock-trading get secret ast-risk-management-service-tls -o jsonpath='{.data.tls\.crt}' | base64 -d | openssl x509 -noout -enddate`
- 基盤が CA を作り直したら、`platform-infra-ca` の写しが更新された後に、ESO を force-sync して証明書を作り直させ、11 サービスを作り直す。

## 戻し方

- 1 段ずつ戻す（Enforce → Call → Listen → Off）。どの段も、1 つ前の段へ戻す helm で通信は止まらない。
- `Enforce` で呼び出しが落ちたら: `helm upgrade … --set global.serviceTls.mode=Call`（平文の 8080 が開き、probe も 8080 へ戻る）。
- `Call` で証明書の検証が落ちたら: `helm upgrade … --set global.serviceTls.mode=Listen`（呼び手が平文へ戻る）。
- 証明書が発行されない（`Listen` で新しい Pod が `ContainerCreating` のまま）なら: `--set global.serviceTls.mode=Off`。旧の Pod が残っているので、通信は止まっていない。

## よくある失敗

| 症状 | 原因 | 対処 |
| --- | --- | --- |
| ExternalSecret `ast-*-service-tls` が `SecretSyncedError`（permission denied） | 基盤の前提 2（`ast-eso` の発行の許可）が無い | 基盤へ依頼する。それまで `Off` |
| 同じく（unknown role） | 基盤の前提 1（PKI の role）が無い | 同上 |
| 新しい Pod が `ContainerCreating`（`secret "ast-…-service-tls" not found`） | 証明書がまだ写されていない | ExternalSecret の状態を見る。直るまで旧の Pod が残る |
| 呼び手のログに `RemoteCertificateNameMismatch` | 宛先の名前が SAN に無い | 宛先は `<サービス>-service`（短い名前）のままにする |
| 呼び手のログに `RemoteCertificateChainErrors` | 基盤の CA の写しが古い | `platform-infra-ca` を force-sync し、呼び手を作り直す |
