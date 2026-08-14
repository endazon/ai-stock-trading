---
title: Vault 秘匿参照（External Secrets）opt-in 手順 Runbook
type: runbook
status: draft
related_ids:
  - ADR-0006
  - NFR
  - IADR-0060
  - IADR-0094
  - IADR-0107
  - IADR-0109
author: endazon (with Claude Code)
created: 2026-07-19
updated: 2026-07-28
plan_refs:
  - "../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md"
---

# Runbook: Vault 秘匿参照（External Secrets）の opt-in 配線

> 起点: [ADR-0006](../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md)（Vault 秘匿）/
> [IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md) 決定4（受け口）/ [IADR-0094](../adr/IADR-0094_local-infra-observability-gitops.md)（#24）。
>
> ⚠️ **既定は k8s Secret 直運用（Vault 非依存）。本手順は opt-in で、有効化しない限り現行挙動は変わらない。**
> **平文の秘密を Git にコミットしない。** 実際に Vault へ鍵が載り ESO が同期して初めて「Vault 化」は充足する。

## 現状（既定・Vault 非依存）

- API 鍵群は Secret `ast-secrets`（手動作成の out-of-band）に置き、各サービスが
  `secretKeyRef`（`optional: true`）で参照する。moomoo 資格情報・RSA 鍵も同様に手動 Secret。
- この状態で `externalSecrets.enabled=false`（既定）＝`ExternalSecret` は一切描画されない（fail-safe）。

## 対象の秘匿情報と Vault パス

| 用途 | 同期先 Secret | Vault KV パス（既定） | プロパティ／吸い上げ方 |
| --- | --- | --- | --- |
| API 鍵群（finnhub / SEC / EDINET / FRED / Discord / s2s / KB 等） | `ast-secrets` | `ai-stock-trading/app-secrets` | `dataFrom.extract`（**プロパティ名 = Secret キー名**） |
| moomoo 資格情報 | `moomoo-credentials` | `ai-stock-trading/moomoo` | `login-account` / `login-pwd-md5`（MD5・平文パスワード不可） |
| moomoo RSA 秘密鍵 | `moomoo-rsa` | `ai-stock-trading/moomoo-rsa` | `opend_rsa.pem` |

`ast-secrets` の**プロパティ名は Secret キー名と一致**させる（`deployment.yaml` の `secretKeyRef.key`）:
`finnhub-api-key` / `edinet-subscription-key` / `fred-api-key` / `marketdata-finnhub-api-key` /
`service-auth-client-id` / `service-auth-client-secret` / `kb-auth-client-id` / `kb-auth-client-secret` /
`discord-webhook-url` / `discord-bot-token` / `discord-bot-killswitch-phrase` /
`discord-owner-auth-client-id` / `discord-owner-auth-client-secret`。

> **`fred-api-key` は日本株取引の必須前提**（基準通貨〔USD〕への換算レート源＝FRED `DEXJPUS` の**逆数**・#262 /
> #364 / [IADR-0107](../adr/IADR-0107_base-currency-conversion.md) /
> [IADR-0152](../adr/IADR-0152_usd-base-currency-migration.md)）。欠けると JPY 建て銘柄は判断前に全件見送りになる
> （米国株は無影響）。**#364 で基準通貨が USD へ移行し、必須となる市場が US 株から日本株へ入れ替わった。**
> 他の API 鍵と違い「無ければ当該ソースが無効」で済まないため、日本株を回す環境では
> 投入必須として扱う。詳細は [chart README「為替換算」](../../deploy/helm/ai-stock-trading/README.md)。
>
> **既定（Vault 非依存）の手動 Secret では `scripts/k8s-local-deploy.sh` が env 未設定のキーに触れない**
> （投入済みの値を保持する・#263 / [IADR-0109](../adr/IADR-0109_deploy-secret-preservation.md)）。
> ESO 同期を有効化した環境では `ast-secrets` は `ExternalSecret` が所有するため、値の投入は Vault 側で行い、
> スクリプトの env は使わない（両者を併用すると所有が割れる）。

## 前提

1. **External Secrets Operator（`external-secrets.io` CRD）** と **Vault ストア**（`SecretStore`/`ClusterSecretStore`）が
   クラスタに導入済みであること。ローカル（経路B）では **MSP 側の共有 stand-up**（別 PR・MSP/IADR-0077）が入れる。
   CRD 未導入で有効化すると `ExternalSecret` の apply が失敗する。
2. Vault に上表のパスで KV が格納済みであること（**Git には載せない**。投入は Vault CLI/UI で人手または CD で行う）。

## 手順（opt-in 有効化）

1. Vault へ鍵を投入（例・値はダミー禁止／実値は端末外に出さない）:

   ```sh
   vault kv put ai-stock-trading/app-secrets \
     finnhub-api-key=... service-auth-client-id=... service-auth-client-secret=... # 以下必要な鍵のみ
   vault kv put ai-stock-trading/moomoo login-account=... login-pwd-md5=...
   vault kv put ai-stock-trading/moomoo-rsa opend_rsa.pem=@opend_rsa.pem
   ```

2. チャートの opt-in を有効化（`secretStoreRef.name` は導入済みストア名に合わせる）:

   ```sh
   helm upgrade --install ai-stock-trading deploy/helm/ai-stock-trading \
     -n ai-stock-trading \
     --set externalSecrets.enabled=true \
     --set externalSecrets.appSecrets.enabled=true \
     --set externalSecrets.secretStoreRef.name=vault-backend \
     --set externalSecrets.secretStoreRef.kind=ClusterSecretStore
   ```

   `secretStoreRef.name` が空だと描画時に停止する（誤有効化の防止）。

3. 同期の確認:

   ```sh
   kubectl -n ai-stock-trading get externalsecret
   kubectl -n ai-stock-trading get secret ast-secrets moomoo-credentials moomoo-rsa
   ```

## 切り戻し

- `--set externalSecrets.enabled=false`（既定）へ戻すと `ExternalSecret` は削除され、手動 Secret 直運用へ戻る。
  `creationPolicy: Owner` のため ESO 管理の Secret は ExternalSecret 削除で消える。手動 Secret を再作成すること。

## fail-safe の要点

- 既定オフ＝現行挙動を変えない。欠けた鍵は同期されず、消費側 `optional: true` で許容（起動は継続）。
- 平文の鍵は values / manifest / docs に置かない（`dataFrom.extract` で Vault から吸い上げる）。

## Tier 3（対象外）

- Hetzner 実環境の Vault 本番運用（unseal・監査・ローテーション）・[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md) の
  実弾解禁前提としての「秘匿情報の Vault 化」実充足は実基盤依存（[`docs/infra/infra.md`](../infra/infra.md) の Tier 境界）。
