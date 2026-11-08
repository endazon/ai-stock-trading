---
title: 取引ユニットのサービス間の HTTP を TLS にする段（Off → Listen → Call → Enforce）と、クラウドの OpenD を非 root（段 3）・読み取り専用のルートで動かす
type: spec
status: accepted
related_ids: [NFR, NFR-05, ADR-0006, ADR-0024, ADR-0080, IADR-0646, IADR-0631, IADR-0609, IADR-0639, IADR-0623, IADR-0632]
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md (単一ノードの k3s)
  - planning:projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md (OpenD の再起動の最小化)
---

# 仕様書: サービス間の HTTP の TLS と、クラウドの OpenD の非 root 化

## 起点

- 所有者の裁定（2026-11-07「medium も low も全部直してから稼働する」）。Refs 非公開の指摘（AST）。非機能 NFR（多層防御）・NFR-05（秘匿）。
- 決定は [IADR-0646](../adr/IADR-0646_service-to-service-tls-and-opend-nonroot.md)。

## 1. 現状（develop `20249ea15`）

| 項目 | 中身 | 場所 |
| --- | --- | --- |
| 受け口 | 全 Worker が `ASPNETCORE_URLS=http://+:8080`。Service も 8080 だけ | `templates/deployment.yaml`・`templates/service.yaml` |
| 呼び手の宛先 | `values-cloud.yaml` の `*__BaseUrl` が `http://<サービス>-service:8080`（18 個） | `values-cloud.yaml` |
| 呼び手の CA | 基盤の宛先（認可サーバー・LLM ゲートウェイ・ブローカ）だけが基盤の CA を信頼する | `PlatformInfraCaTrust.cs`（IADR-0639） |
| メッシュ | 注入は無効（`mesh.sidecarInjection.enabled: false`） | `values.yaml` |
| 基盤の PKI | `pki_cloud`（OpenBao）。基盤の 4 つの受け口の証明書を ESO の `VaultDynamicSecret` で写す | 基盤の `deploy/cloud/vault/eso/tls-*.yaml` |
| OpenD | クラウドは段 1（root）・ALL を落として DAC_OVERRIDE・FOWNER。イメージは uid 10001・`/home/opend` を用意済み。段 3 の分岐あり | `values-cloud.yaml`・`templates/opend.yaml`・`deploy/opend/Dockerfile` |
| gRPC | クラウドは使わない（`grpcPort` の宣言なし） | `values-cloud.yaml` |

## 2. 母集合（規則 9: 誤りの側の字面で全文書を走査した結果）

- `http://[a-z-]+-service:8080` の字面（values）: `values-cloud.yaml` 18 個（書き換えの対象）・`values.yaml`・`values-local.yaml`（Off のまま。対象外）。
- `ASPNETCORE_URLS`・`port: 8080` の字面（templates）: `deployment.yaml`（受け口・probe）・`service.yaml`・`networkpolicy-services.yaml`（入り・出）。`opend.yaml` の認証サイドカー（クラウドで無効。BFF が呼ぶ口で取引ユニットの呼び手ではない。対象外）。`cronjob.yaml` の `target`（クラウドで無効。Enforce との併用を描画で止める）。
- 取引ユニットの宛先を持つ HttpClient（backend）: 全てが `AddAiStockTradingServiceToken`（18 個）か `AddDiscordOwnerToken`（10 個）を通る（`AddHttpClient(` の全走査。外部 API の `marketdata`・`fx`・`collection`・Discord の Webhook・Stage 0 の外部取得はトークンを載せない）。
- 平文を前提にした検査: `scripts/helm-network-policy.test.js`（env の口と方針の口の一致。Off のまま通り、Enforce でも通ることを新しい試験で見る）・`.github/workflows/helm.yml` の `port: 8080` の字面の検査（既定・ローカルの描画。Off のまま）。
- OpenD の段 1 を前提にした記述・検査: `scripts/helm-network-policy.test.js`（DAC_OVERRIDE・FOWNER を期待）・`scripts/helm-pod-security.test.js`（values-cloud の OpenD は段 1）・`docs/operations/cloud-network-policy-runbook.md`・`docs/operations/cloud-single-node-migration-runbook.md` §9・`deploy/opend/README.md`。
- 除外: `.ai-context/specs/`・凍結の IADR の本文（point-in-time の記録）。

## 3. 受け入れ基準

| # | 基準 | 試験 |
| --- | --- | --- |
| 1 | 既定・ローカル・クラウドの今の値（Off）の描画は従来と同じ（TLS の口・証明書・発行を描かない） | `helm-service-tls.test.js` 1・既存の描画の検査（helm.yml） |
| 2 | Listen: 受け口が 8080 と 8443、Kestrel の証明書と非 optional の Secret の mount、probe は 8080、呼び手は平文のまま、方針は両方の口、サービスごとの発行（path・SAN） | `helm-service-tls.test.js` 2 |
| 3 | Call: 2 に加えて、取引ユニットの宛先に平文が残らない | 同 2・変異 |
| 4 | Enforce: 受け口・Service・方針・probe は TLS の口だけ。呼び手の一覧と env の一致がそのまま通る | 同 2・3 |
| 5 | 段の値域・引用符の無い Off・口 8080・gRPC との併用・Enforce と CronJob・CA の欠けは描画で止まる | 同 4 |
| 6 | サービス・所有者のトークンを載せる HttpClient は、CA の場所があるときだけ基盤の CA で検証し、外部の HttpClient には付かない | `PlatformInfraCaDestinationWiringTests`・`ServiceTlsCaTrustWiringTests`（通知）・報告書・取引判断の配線の試験 |
| 7 | values-cloud の OpenD は段 3（uid 10001・HOME `/home/opend`）・読み取り専用のルート・作業の場所・HOME・`/tmp` は emptyDir・PVC は HOME の下 | `helm-pod-security.test.js`・`helm-network-policy.test.js` 1 |
| 8 | 段 1 へ戻した描画（切り戻し）は従来の形（root・DAC_OVERRIDE・FOWNER だけ） | `helm-network-policy.test.js` 1・変異 |
| 9 | OpenD の読み取り専用のルートは段 1 では止まり、作業の場所は `/opt/opend`・HOME・`/tmp` の外 | `helm-pod-security.test.js` |
| 10 | 入口の処理は配布物を作業の場所へ写し（`OpenD.xml`・RSA 鍵のディレクトリは写さない）、再起動で配布物の項目を置き換え、OpenD が書いたものは残す | `entrypoint.test.sh` T-646-01 |

陰性対照: 修正を外す（トークンの拡張の `.TrustPlatformInfraCa()` を外す・入口の処理の除外を外す・描画物の 1 か所を戻す）と、上の試験が赤になることを確かめた（実測は PR の本文）。

## 4. タスク

1. chart: `global.serviceTls`（values）・`templates/_service-tls.tpl`・`deployment.yaml`・`service.yaml`・`networkpolicy-services.yaml`・`external-secrets.yaml`。
2. backend: `ServiceAuthExtensions.AddAiStockTradingServiceToken`・`DiscordOwnerAuthExtensions.AddDiscordOwnerToken` に `.TrustPlatformInfraCa()`。
3. OpenD: `opend.podSecurity.readOnlyRootFilesystem`・`workDir`（values・`opend.yaml`）・`entrypoint.sh` の `OPEND_WORK_DIR`・`values-cloud.yaml` の段 3。
4. 試験: `scripts/helm-service-tls.test.js`（helm.yml に登録）・既存の 2 つの描画の試験・`entrypoint.test.sh`・.NET の配線の試験。
5. 文書: 手順書 `docs/operations/cloud-service-tls-runbook.md`（新規）・通信の制限の手順書（OpenD の切り戻し・PVC の持ち主）・移行の手順書（§5.4・§8・§9）・chart の README・`deploy/opend/README.md`・`scripts/README.md`・IADR の索引。

## 5. 利用者・基盤の作業（リポジトリの外）

- 基盤（MSP）: PKI の role `<サービス>-service`（名前空間 `ai-stock-trading`）11 個と、role `ast-eso` の policy への発行の許可（手順書の「前提」）。所有者のクライアントのアクセストークンの寿命の短縮（任意。realm の設定）。
- 利用者: 段の切り替え（手順書）・OpenD の段 3 の初日の確認（移行の手順書 §8）と PSA の restricted への引き上げ（§9）。
