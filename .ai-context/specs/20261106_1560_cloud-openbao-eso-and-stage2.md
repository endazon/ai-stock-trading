---
title: クラウド（values-cloud）の秘密を OpenBao から ESO で供給し、守りの段を段 2（強制）にする（ADR-0080 の取引ユニットの側）
type: spec
status: accepted
related_ids: [NFR-05, NFR-06, NFR-18, FR-10, FR-14, ADR-0006, ADR-0078, ADR-0079, ADR-0080, IADR-0623, IADR-0608, IADR-0610, IADR-0611, IADR-0613, IADR-0615, IADR-0620, IADR-0621, IADR-0622, IADR-0341, MSP:IADR-0689]
author: claude (Claude Code)
created: 2026-11-06
updated: 2026-11-06
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0080_cloud-poc-gate-in-cluster-credentials-and-pre-secret-reachability.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0078_cloud-poc-hetzner-eu-premises-and-migration-gate.md
---

# 仕様書: クラウドの秘密の OpenBao からの供給と、段 2 の強制（#1560）

## 起点

- #1560（クラウドへの移行の管理の issue）。§4 の残作業「OpenBao（ESO・unseal）をクラウドへ持ち込み、クラスタの中の資格情報と TLS の鍵を OpenBao で作る・OpenBao から供給する」のうち、**取引ユニット（AST）の側**。
- 計画 ADR-0080 決定 1: 本物の秘密（外から持ち込む資格情報・クラスタの中で乱数で作る資格情報・TLS の秘密鍵）は、OpenBao から供給されるまでクラウドへ置かない。k8s の Secret に作ってから移す形は採らない。
- #1560 §3: 段 1（記録だけ）と段 2（強制）の 2 段で作った守りは、**クラウドでは段 2 が強制になっていることが門の条件**である。
- 基盤（MSP）の側は MSP#2471（MSP の IADR-0689）。OpenBao・ESO の本体・OpenBao の role と policy・`cloud/shared/*` の値・`seed-secrets.sh`（`gen`・`gen-rsa`）・`put-external-secret.sh`（MSP の PR ②）は MSP が持つ。
- 先に入れた前提: #1561（ボットが kill switch の解除に版を添える。#1562）・#1552（ボットの確認コードの入力）・#1554（ボットの許可表）。

## 取り決め（MSP の IADR-0689 決定 6。基盤と取引ユニットの境界）

- ストアは名前空間ごとの `SecretStore openbao`（kubernetes 認証）。ClusterSecretStore は使えない（基盤の ESO が処理しない）。
- **取引ユニットが置くもの**: 名前空間 `ai-stock-trading` の `SecretStore openbao`・ServiceAccount `ast-eso-reader`・ExternalSecret・手順書。OpenBao の role は `ast-eso`（この ServiceAccount に結んである）。
- KV v2 のマウントは `cloud/`。property の名前は k8s の Secret のキーの名前と 1 対 1。
- 取引ユニットが読めるパス: `cloud/ast/*`（取引ユニットだけの値。キーは取引ユニットが決める）・`cloud/shared/ast-db`・`cloud/shared/ast-rabbitmq-users`・`cloud/shared/ast-client-secrets`・`cloud/shared/platform-infra-ca`。

## 決定（IADR-0623）

### 1. 秘密の一覧と供給元（values-cloud の描画を全件引いた）

描画（`helm template … -f values-cloud.yaml -f ci/values-cloud-digests.fixture.yaml`）が参照する Secret の全件を、ADR-0080 決定 1 の区分（1 = 外から持ち込む・2 = クラスタの中で乱数で作る・3 = TLS の鍵）と供給元に照らした。

| k8s の Secret（キー） | 区分 | OpenBao のパス（property） | 値を作る・入れる者 |
| --- | --- | --- | --- |
| `ast-db-credentials`（`username`・`password`） | 2 | `cloud/shared/ast-db`（`username`・`password`） | 基盤の `seed-secrets.sh`（OpenBao の中で乱数） |
| `ast-rabbitmq-users`（11 の利用者名） | 2 | `cloud/shared/ast-rabbitmq-users`（利用者名） | 同上 |
| `ast-secrets` の `service-auth-client-secret`・`llm-auth-client-secret`・`discord-owner-auth-client-secret` | 2 | `cloud/shared/ast-client-secrets`（`ai-stock-trading-svc`・`-llm-caller`・`-owner`） | 同上 |
| `ast-secrets` の `order-approval-signing-key` | 2（保存データではなく承認の印の鍵。サービスの間の認証にあたる） | `cloud/ast/ast-secrets`（同名） | 基盤の `seed-secrets.sh gen cloud/ast/ast-secrets order-approval-signing-key 32`（OpenBao の中で乱数・標準の base64） |
| `ast-secrets` のその他（Discord のトークン・webhook・確認のフレーズ・情報源の API キー・SEC EDGAR の UA・Discord の環境の ID） | 1 | `cloud/ast/ast-secrets`（同名） | オーナーが `put-external-secret.sh cloud/ast/ast-secrets <キー>` で標準入力から入れる |
| `ast-owner-totp`（`secret`） | 1 | `cloud/ast/ast-owner-totp`（`secret`） | オーナーが `scripts/ast-owner-totp-enroll.sh --live --openbao` で作る（決定 5） |
| `moomoo-credentials`（`login-account`・`login-pwd-md5`） | 1 | `cloud/ast/moomoo-credentials`（同名） | オーナーが `put-external-secret.sh` で入れる（パスワードは MD5 の小文字 16 進） |
| `moomoo-rsa`（`opend_rsa.pem`） | 2（OpenD とクライアントの間の通信の暗号化。クラスタの中で作る） | `cloud/ast/moomoo-rsa`（`opend_rsa.pem`） | 決定 6 |
| ConfigMap `platform-infra-ca` → Secret `platform-infra-ca`（`ca.crt`） | 秘密ではない（公開してよい CA の証明書） | `cloud/shared/platform-infra-ca`（`ca.crt`） | 基盤の `configure.sh` |
| `ast-operator-hold`（`enabled`・`reason`） | **秘密ではない**（決定 4） | — | オーナーが SSH から手で作る |

- `ast-secrets` の client ID（`service-auth-client-id`・`llm-auth-client-id`・`discord-owner-auth-client-id`）は秘密ではない。ExternalSecret の template に字面で書く（クラウドの realm の 3 つの clientId と同じ値）。
- 区分 3（TLS の鍵）は基盤の側にしかない（Postgres・RabbitMQ・LLM ゲートウェイ）。取引ユニットはクライアントとして CA で検証するだけで、TLS の秘密鍵を持たない。

### 2. chart に足すもの（既定の描画は変えない）

- `externalSecrets.openbaoStore.create`（既定 false）: 名前空間の `SecretStore`（名前は `externalSecrets.secretStoreRef.name`）と ServiceAccount（`automountServiceAccountToken: false`）を描く。`serviceAccountRef` に audience（既定 `vault`）を明示する（基盤の role は audience=vault を要求し、基盤の検査 `vault-k8s-auth-audience` も同じ形を求める）。kind が `SecretStore` でなければ描画で止める。
- `externalSecrets.deletionPolicy`（既定は空＝描かない）: クラウドは `Retain`（OpenBao が封印の間も、写した Secret を残して消費側が動く。MSP の IADR-0689 決定 5 と揃える）。
- `externalSecrets.dbCredentials`（既定 disabled）: `ast-db-credentials` を `username`・`password` で写す。
- `externalSecrets.ownerTotp`（既定 disabled）: `ast-owner-totp` を写す。
- `externalSecrets.infraCa`（既定 disabled）: CA の証明書を Secret `platform-infra-ca` に写す。消費側は `global.infraCa.secret` で Secret から mount する（既定は従来の ConfigMap）。
- `externalSecrets.appSecrets.sharedClientSecrets`・`clientIds`（既定は空）: `ast-secrets` に、基盤と共有の 3 つの secret を `data` で、client ID を template の字面で足す（`mergePolicy: Merge`。`cloud/ast/ast-secrets` の `dataFrom.extract` と合わせて 1 つの Secret）。
- `global.db.usernameSecret`（既定は空）: DB の利用者名を Secret から読む（`cloud/shared/ast-db` の利用者名は乱数の接尾辞つき。固定の `ai` ではない）。**既定と経路 B は従来どおり `ai` を描く**（helm.yml で固定する。オーケストレーターの指示）。

### 3. 段 2（強制）にする守り（values-cloud）

| 守り | 段 1 → 段 2 | values-cloud の書き方 | 前提（済み） |
| --- | --- | --- | --- |
| 注文の承認の印（IADR-0610） | `Report` → `Enforce` | `global.orderApprovalSigning.mode: Enforce` | 鍵は `cloud/ast/ast-secrets`（`gen`）。Enforce では鍵の Secret が必須（無ければ Pod が起動しない） |
| 所有者のトークンの aud / azp（IADR-0611） | `Report` → `Enforce` | `global.ownerTokenBinding.mode: Enforce` | クラウドの realm に `ai-stock-trading-api-audience` のスコープとマッパーが残り、`ai-stock-trading-owner` の既定のスコープに入る（基盤が確かめ済み） |
| ボットの許可表（IADR-0622） | `Report` → `Enforce` | `global.ownerBotScope.mode: Enforce` | クラウドの realm に `trading-bot` が残る（同上） |
| 危険な操作の第 2 要素（IADR-0615） | `Audit` → `Enforce` | risk-management・report の `OwnerSecondFactor__Mode: Enforce` | ボットが 6 桁を運ぶ（#1552） |
| kill switch の前提条件（IADR-0620） | `Report` → `Require` | risk-management の `RiskControls__KillSwitch__Precondition: Require` | ボットが版を添える（#1561） |

- 段 2 を values-cloud にだけ書く。`values.yaml`（既定）と `values-local.yaml`（経路 B）は段 1 のまま（手元の PoC は終わるが、経路 B の構成は残る）。
- クラウドはまだ稼働していない（サーバーを注文していない）。段 2 で始めても、止まる稼働は無い。

### 4. `ast-operator-hold` は管理の外に置く（秘密ではない）

- `ast-operator-hold` は**秘密ではない。SSH から入れる停止の切り替え**である（IADR-0613）。値は `enabled`（真偽）と `reason`（理由の文）だけで、認証にも保存データの保護にも使わない（ADR-0080 決定 1 の「同じ種類」に当たらない）。
- OpenBao と ESO を経ない理由: Discord を経ない停止は、OpenBao が封印のあいだや ESO が止まったときにも効く必要がある。OpenBao を経ると、停止の口が秘匿管理の稼働に依存する。
- 従来どおり chart・ESO・ArgoCD の管理の外で、オーナーが `kubectl` で作る・消す。消費側は optional で読む。

### 5. 所有者の TOTP の登録（`scripts/ast-owner-totp-enroll.sh --openbao`）

- 乱数 20 バイトの base32 のシークレットを作り、基盤の `put-external-secret.sh cloud/ast/ast-owner-totp secret` の**標準入力へだけ**流す（引数・環境変数・履歴・ディスクに残さない。0600 の一時ファイルは終了時に消す）。
- QR は従来どおり端末に出し、Enter で消す。
- 書いた後は、ExternalSecret `ast-owner-totp` に同期を求め（`force-sync` の注釈）、risk-management と report の Pod を作り直させる。
- 🔶 **仮: `put-external-secret.sh` の呼び方は MSP の PR ② で確定する。** 確定までは、呼び出しの前置き（スクリプトのパスとオーナー用の資格の引数）を環境変数 `AST_PUT_EXTERNAL_SECRET` で受ける（秘密を持たない。ファイルのパスだけ）。確定したら手順書とスクリプトを合わせる。
- 既に登録されているか（`--rotate` が要るか）は、同期済みの k8s の Secret の有無で見る。

### 6. OpenD の RSA の鍵（`cloud/ast/moomoo-rsa`）

- 手元の PoC の鍵は 1024 ビットである（AST の手順書 `deploy/opend/README.md` の 2b) と、経路 B で使った基盤の画面の生成器がどちらも 1024。2048 で OpenD が動いた記録は無い）。
- OpenBao の `gen-rsa` は 2048 未満を作れない。したがって**オーナーが手元で `openssl genrsa -traditional 1024` の出力を `put-external-secret.sh cloud/ast/moomoo-rsa opend_rsa.pem` へ直にパイプで流す**（鍵はディスクに書かない）。
- 2048 へ上げるかは、クラウドで OpenD が動いてから別に確かめる（残余）。

### 7. 手順書（`docs/operations/cloud-single-node-migration-runbook.md`）

- §1 の段 2・段 4 に、ADR-0080 の条件（OpenBao からの供給・突き合わせ・秘密なしの到達の確認）を入れる。
- §5 を、手で Secret を作る形から、OpenBao へ入れて ESO で写す形へ書き換える（決定 1 の表・入れる順・確かめ方）。
- §8 の到達の確認を §1 の段 4（秘密を入れる前）へ移し、`scripts/measure-region-latency.sh` の実際の用法（`--tcp-only <ホスト>:<ポート>`・`--host`・既定の試行回数 5 回以上）に合わせる。OpenD の接続先のホストとポートは、オーナーが moomoo の公式資料で確かめて書く（確かめられないときの扱いは ADR-0080 決定 2）。
- 突き合わせの手段: 描画から参照される Secret の一覧（決定 1 の表）と、`kubectl get externalsecret` の同期の状態を照らす。

## 母集合（規則 9）

- **values-cloud の描画が参照する Secret**: 上の表の 8 種（`ast-db-credentials`・`ast-rabbitmq-users`・`ast-secrets`・`ast-owner-totp`・`moomoo-credentials`・`moomoo-rsa`・`ast-operator-hold` と、ConfigMap/Secret `platform-infra-ca`）。`ast-secrets` のキーは描画で 15 本（client ID 3・client secret 3・HMAC 1・外の 8）。Discord の環境の ID 4 本は appSecrets の有効時に `ast-secrets` から読む（IADR-0341）。
- **段の切り替え**: `Auth:OwnerToken:Mode`・`Auth:OwnerBot:Mode`・`OrderApprovalSigning:Mode`・`OwnerSecondFactor:Mode`（2 サービス）・`RiskControls:KillSwitch:Precondition`。backend のコードから `"…:Mode"`・`"…:Precondition"` の構成キーを引いた（他は `FaultInjection:IndeterminateDispatch:Mode` で、試験の故障の注入。クラウドで使わない）。
- **ESO の受け口**: `templates/external-secrets.yaml`（moomoo の 2 つ・ブローカ・OpenD の認証サイドカー〔クラウドでは描かない〕・`ast-secrets`）。
- **除外**: 基盤の Secret（Postgres・RabbitMQ・Keycloak・LLM ゲートウェイの TLS ほか）は基盤の側。LLM の API キーは基盤の LLM ゲートウェイが持つ。

## 受け入れ基準

- [x] values-cloud の描画が参照する Secret のうち、`ast-operator-hold` を除くすべてに ExternalSecret（`SecretStore openbao`）がある。手で作る Secret が残らない（helm.yml の検査）。
- [x] 名前空間の `SecretStore openbao`（kubernetes 認証・role `ast-eso`・ServiceAccount `ast-eso-reader`）が描かれる。
- [x] DB の利用者名とパスワードが Secret から読まれ、接続文字列に固定の利用者名・リポジトリの既定のパスワードが現れない。
- [x] 5 つの守りが段 2 で描かれる。既定と経路 B の描画は段 1 のまま。
- [x] 既定（`values.yaml`）と経路 B（`values-local.yaml`）の描画は、この変更の前と同じ（バイト等価）。
- [x] TOTP の登録スクリプトの `--openbao` は、シークレットを標準入力でだけ渡し、引数・環境変数に載せない（試験）。
- [x] 手順書が ADR-0080 の条件（供給・突き合わせ・秘密なしの到達）と、入れる順を持つ。

## 範囲外

- 基盤の側（OpenBao・ESO の本体・role と policy・`put-external-secret.sh`）。MSP#2471 と MSP の PR ②。
- 段 2 で拒まれたときのボットの専用の文言（IADR-0615 の 2026-11-06 の追記の ②）。いまは認可の失敗と同じ汎用の文言で、段 2 でも操作は止まる側に倒れる。
- サーバーの注文・秘密の投入・稼働の確認（オーナー）。
