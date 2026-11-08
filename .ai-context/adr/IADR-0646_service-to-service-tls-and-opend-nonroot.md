---
title: IADR-0646 取引ユニットのサービス間の HTTP を、基盤の OpenBao の PKI が発行したサービス証明書で TLS にする（Off → Listen → Call → Enforce の段）。クラウドの OpenD は非 root（段 3）と読み取り専用のルートで動かし、配布物が書く場所を emptyDir へ寄せる
type: impl-adr
status: Accepted
related_ids: [NFR, NFR-05, ADR-0006, ADR-0024, ADR-0080, IADR-0631, IADR-0609, IADR-0639, IADR-0621, IADR-0623, IADR-0632, IADR-0060]
author: claude (Claude Code)
created: 2026-11-08
updated: 2026-11-08
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md （実行基盤）
  - planning:projects/ai-stock-trading/07_adr/ADR-0024_opend-unattended-restart-conditional.md （OpenD の再起動の最小化）
---

# IADR-0646: サービス間の HTTP の TLS と、クラウドの OpenD の非 root 化

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定（同じ稼働前の硬化の 2 点を、同じ段の作法でまとめて決める）。

- 状態: Accepted
- 日付: 2026-11-08
- 決定者: 所有者の裁定（2026-11-07「medium も low も全部直してから稼働する」）・claude（方式・段・既定値）

## 起点・関連

- 非機能（多層防御・通信の秘匿）。Refs 非公開の指摘（AST）。計画の非機能要件表に該当の番号が無いものは裸の `NFR`、秘匿の扱いは NFR-05。
- 計画 ADR-0006（単一ノードの k3s）・ADR-0024（OpenD の再起動の最小化）・ADR-0080（秘密は OpenBao から ESO で写す）。
- 上書きする既存の決定（本文は凍結のまま）:
  - IADR-0631 の「クラウドの OpenD は root のまま（段 1）」→ **クラウドは段 3（非 root）と読み取り専用のルート**。段 1 の形（ALL を落として DAC_OVERRIDE・FOWNER だけ）は切り戻しの形として残す。
  - IADR-0631「残る範囲」の「同じノードの Pod の間の通信は平文の HTTP のまま」→ **TLS にする段を持つ**（クラウドの既定は基盤の前提が入るまで Off）。
  - IADR-0639 の「基盤の CA を信頼するのは基盤の宛先だけ」→ **取引ユニットのサービス（サービス・所有者のトークンを載せる呼び出し先）も同じ CA を信頼する**。外部 API は変わらない。
- 作業仕様書: `.ai-context/specs/20261108_s2s-tls-opend-nonroot.md`（母集合・実測・受け入れ基準）。
- 手順書: `docs/operations/cloud-service-tls-runbook.md`（段の切り替え・基盤への依頼）・`docs/operations/cloud-network-policy-runbook.md`（OpenD の切り戻し・PVC の持ち主の移し替え）。

## 文脈

1. サービス間の HTTP（11 サービス・`*__BaseUrl` の宛先 18 個）は平文の 8080 で、サービスのトークンと所有者のトークンを載せている。メッシュ（mTLS）は入れていない（IADR-0314。クラウドの注入は無効）。
2. 基盤は Postgres・RabbitMQ・Keycloak・LLM ゲートウェイの TLS を、OpenBao の PKI（`pki_cloud`。CA の鍵は OpenBao の外へ出ない）の発行と ESO の `VaultDynamicSecret` で揃えている（基盤の MSP:IADR-0689 決定 4）。LLM ゲートウェイは Kestrel の `Kestrel__Certificates__Default__Path / KeyPath` と `ASPNETCORE_URLS=https://…` だけで受け口を TLS にしている（コードの変更なし）。
3. 取引ユニットの呼び手の HttpClient は、宛先が基盤のときだけ基盤の CA を信頼する（`.TrustPlatformInfraCa()`。IADR-0639）。取引ユニットの宛先には付いていない。
4. クラウドの OpenD は段 1（root）で動く。イメージは uid 10001 の利用者と HOME `/home/opend` を用意済みで、chart は段 3 の分岐を持つ（IADR-0609）。root の OpenD が要る権限（DAC_OVERRIDE・FOWNER）は、uid 10001 が持つ `/opt/opend` へ root が書くためだけに要る（IADR-0631 の実測）。

## 決定

### 決定 1: サービス間の HTTP の TLS は `global.serviceTls.mode` の 4 段で入れる（既定 Off）

| 段 | 受け側（Kestrel・Service・containerPort） | 呼び手（env の宛先） | probe・NetworkPolicy |
| --- | --- | --- | --- |
| `Off`（既定） | 平文の 8080 だけ（描画は従来とバイト等価） | 平文 | 8080 |
| `Listen` | 8080 と TLS の口（既定 8443）の両方 | 平文のまま | probe は 8080・方針は両方 |
| `Call` | 両方（平文は戻しのために残す） | `http://<サービス>-service:8080` → `https://<サービス>-service:8443`（描画が書き換える） | probe は 8080・方針は両方 |
| `Enforce` | TLS の口だけ | TLS | probe は TLS の口（scheme HTTPS）・方針は TLS の口だけ |

- 呼び手の書き換えは描画が行う（values の `*__BaseUrl` は平文の字面のまま。段を 1 か所で切り替え、写し忘れを作らない）。書き換えるのは宛先が `services` に在る取引ユニットのサービスのときだけ。
- 稼働中の通信を止めない順序: 受け側が両方で受けた後に呼び手を替え（Call）、呼び手が全て TLS になった後に平文を閉じる（Enforce）。各段は 1 回の helm で戻せる。
- gRPC（h2c）の口・呼び手とは併用できない（描画で止める）。クラウドは gRPC を使わない。平文の口を残さないため、TLS の gRPC は必要になった時に別の決定で足す。
- Enforce と CronJob（平文の 8080 を叩く）は併用できない（描画で止める）。クラウドは CronJob を使わない。

### 決定 2: サービス証明書は基盤の PKI が発行し、ESO が名前空間の受け口と role で写す

- サービスごとに `VaultDynamicSecret`（`pki_cloud/issue/<サービス>-service`）と `ExternalSecret`（Secret `ast-<サービス>-service-tls`。`tls.crt`・`tls.key`）を描く。鍵は OpenBao の中で作られる。
- SAN は短い名前・名前空間つき・`.svc`・`.svc.cluster.local` の 4 つ（基盤の role の `allowed_domains` と同じ形）。
- 呼び出しの受け口・CA・認証は名前空間の SecretStore と同じ（`externalSecrets.openbaoStore`。TLS の 8443・ConfigMap `openbao-ca`・role `ast-eso`・ServiceAccount `ast-eso-reader`）。
- 発行の期限は 2160h（90 日）、ESO の作り直しは 720h（30 日）。Kestrel が作り直した証明書を読むのは Pod の作り直しの後とみなし、手順書で作り直す（クラウドに Reloader は無い）。
- 🔴 **前提は基盤の側の 2 つ**（このリポジトリでは入れられない）: PKI の role `<サービス>-service`（名前空間 `ai-stock-trading`）の 11 個と、role `ast-eso` の policy へのその 11 個の発行の許可。前提が入るまでクラウドは Off のまま（Listen 以上では Secret が無いと Pod が起動しない＝黙って平文へ戻らない）。
- Secret は非 optional でマウントする。鍵のモードは Worker の段に合わせる（段 3 は fsGroup ＋ 0440）。

### 決定 3: 呼び手は、サービス・所有者のトークンを載せる HttpClient で基盤の CA を信頼する

- `AddAiStockTradingServiceToken`（サービスのトークン）と `AddDiscordOwnerToken`（所有者のトークン）が `.TrustPlatformInfraCa()` を付ける。これらの HttpClient の宛先は取引ユニットのサービスだけである（外部 API はトークンを載せない）。
- CA の場所（`PlatformInfraCa__CertificatePath`）があるときだけ効く。chart は Off 以外の段で全 Worker に CA をマウントする。判定は IADR-0639 と同じ（名前の不一致・CA なしは拒む。平文や検証なしへ倒れない）。
- 外部 API の HttpClient（市況・為替・情報源・Discord の Webhook）には付かない（試験が陰性対照で固定する）。

### 決定 4: クラウドの OpenD は段 3（非 root）と読み取り専用のルートで動かす

- `values-cloud.yaml`: `opend.podSecurity.stage: 3`・`opend.home: /home/opend`・`opend.podSecurity.readOnlyRootFilesystem: true`。段 3 は seccomp・権限昇格なし・ケーパビリティを全て落とす（戻すものは無い）・RSA 鍵は 0440。
- 読み取り専用のルート（新しい opt-in。段 3 でだけ使える）: 入口の処理（`deploy/opend/entrypoint.sh`）が env `OPEND_WORK_DIR`（emptyDir）に `/opt/opend` の写しを作り（`OpenD.xml` と mount された RSA 鍵のディレクトリは写さない）、`OpenD.xml` をそこへ書いてそこから OpenD を起動する。HOME と `/tmp` も emptyDir。デバイス信頼の PVC は HOME の下に張る。
- PSA の enforce は baseline のまま。restricted へ上げるのは、実機の OpenD で段 3 を確かめた後（移行の手順書 §9）。
- 実 OpenD の実行ファイルでは確かめていない（配布物は取り込みの前に手元に無い）。初日の確認項目に足し、落ちたら 2 段で戻す（まず読み取り専用のルートだけを外す → それでも落ちたら段 1）。
- 稼働中の root の OpenD から移すときは、PVC のデバイス信頼のファイル（root の持ち物・0600）の持ち主を先に 10001 へ移す（手順書。local-path の PVC は fsGroup の持ち主の変更を受けない）。

## 採らなかった案

- **メッシュの mTLS**: 単一ノードで Istio の制御面とサイドカーを足す重さ・PSA の baseline を満たさない注入（IADR-0609）・OpenD の除外の扱いに比べ、Kestrel の設定だけで足りる。
- **一度に Enforce へ切り替える**: 呼び手と受け側の作り直しの順で一時的に通信が止まる。段を分ければ止まらない。
- **cert-manager**: 基盤は OpenBao の PKI と ESO に揃えている。2 つ目の発行の仕組みを持たない。
- **全 HttpClient に基盤の CA を足す（ConfigureHttpClientDefaults）**: 外部 API でも基盤の CA を信頼することになり、IADR-0639 に反する。
- **OpenD の段 3 だけ（読み取り専用のルートなし）**: 非 root にはなるが、配布物が書く場所が rootfs に残る。PSA の restricted には要らないが、書ける場所を emptyDir に限る方が狭い。落ちたときの第一の戻し先として残す。

## 残余

- 基盤の前提（PKI の role 11 個・`ast-eso` の発行の許可）が入るまで、クラウドのサービス間の HTTP は平文のまま（段 Off）。基盤への依頼は手順書にある。
- 所有者のクライアントのアクセストークンの寿命を短くする案は基盤の realm の設定であり、本決定の外（基盤への依頼に挙げる）。
- 証明書の作り直しの後の読み直しは Pod の作り直しに頼る（Kestrel が配られた Secret の差し替えを読み直すかは実機で確かめる）。
- OpenD の段 3 と読み取り専用のルートは実機で確かめる（初日の確認項目）。
