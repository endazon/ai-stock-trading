---
title: クラウドの名前空間のストア（SecretStore openbao）を、基盤の OpenBao の TLS の口（https）へ向け、受け口の CA で検証する（取引ユニットの段）
type: spec
status: in-progress
related_ids: [NFR, NFR-05, ADR-0080, IADR-0623, IADR-0631, MSP:IADR-0696, MSP:IADR-0689]
author: claude (Claude Code)
created: 2026-11-07
updated: 2026-11-07
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (NFR-05)
---

# 仕様書: 名前空間のストアを基盤の OpenBao の TLS の口へ向ける（取引ユニットの段）

## 起点

- 所有者の裁定（2026-11-07）: 「OpenBao も TLS にする」。基盤の OpenBao の受け口を TLS にし、ESO は受け口の CA で検証する。
- 基盤の側は MSP のブランチ `fix/NFR-cloud-openbao-tls`（MSP:IADR-0696）。受け口は TLS の 8443 だけ。CA の証明書（公開してよい）は、基盤の `tls-cert.sh` が
  この名前空間に ConfigMap `openbao-ca`（キー `ca.crt`）として置く。基盤の手順書 `docs/operations/cloud-openbao-tls-runbook.md` の段 3 にあたる。
- 本 PR は **基盤の PR の後に当てる**前提である（基盤の併存の段〔平文と TLS の両方を受ける〕の後に、このストアを https にする）。
- 非機能の番号: 計画の非機能要件表に通信の暗号化の番号は無い。起点は裸の `NFR` とし、秘密の保管の `NFR-05` を併記する（ESO が写す値の全部が流れる通信）。
- 決定は [IADR-0631](../adr/IADR-0631_cloud-openbao-store-tls.md)。

## 1. 現状（develop `08634c6ab`）

| 場所 | 現状 |
| --- | --- |
| `values-cloud.yaml` | `externalSecrets.openbaoStore.server: http://openbao.openbao.svc:8200` |
| `templates/external-secrets.yaml` | SecretStore の `provider.vault` に CA の指定の口が無い。宛先の形を検めない |
| `helm.yml`「Assert values-cloud secrets from OpenBao and stage 2」 | role・ServiceAccount・audience を検める。宛先と CA は検めない |

## 2. 母集合（規則 9）

- `openbaoStore`・`http://openbao`・`openbao.openbao`・`8200` を `deploy/helm/`・`.github/workflows/`・`docs/operations/`・`scripts/` で走査した。
  - 直すもの: `values-cloud.yaml`（宛先・CA）・`values.yaml`（既定の口）・`templates/external-secrets.yaml`（描画と検め）・`helm.yml`（描画の検め）・
    chart の README・移行の手順書 §5.4。
  - 除外: `scripts/k8s-local-deploy*.sh`（連結ローカル。基盤の ClusterSecretStore を使い、本 PR の対象の外）・`.ai-context/specs/`（凍結）。

## 3. 受け入れ基準

| # | 基準 | 確かめ |
| --- | --- | --- |
| AC1 | values-cloud の SecretStore は `https://openbao.openbao.svc:8443` で、`caProvider`（ConfigMap `openbao-ca`・キー `ca.crt`）を持つ。検証を外す指定が無い | `helm.yml` の段 |
| AC2 | `openbaoStore.create=true` で宛先が https でない・`caProvider` が欠ける（名前・キー・型）と描画で止まる | 同（陰性対照） |
| AC3 | 既定・values-local の描画は変わらない（SecretStore を描かない） | 同（既存の検め） |
| AC4 | 適用の順序（基盤の併存の段 → 基盤のストア → 本 chart → 基盤が平文を閉じる）と、名前空間を新しく作るときの CA の置き方を手順書に書く | `docs/operations/cloud-single-node-migration-runbook.md` §5.4 |
