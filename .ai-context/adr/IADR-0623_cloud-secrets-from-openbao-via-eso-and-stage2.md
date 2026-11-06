---
title: IADR-0623 クラウド（values-cloud）の秘密は、名前空間の SecretStore openbao から ESO で写す（手で作る Secret を残さない。例外は秘密ではない停止の切り替えだけ）。守りの段は段 2（強制）で始める
type: impl-adr
status: Accepted
related_ids: [NFR-05, NFR-06, NFR-18, FR-10, FR-14, ADR-0006, ADR-0078, ADR-0079, ADR-0080, IADR-0608, IADR-0610, IADR-0611, IADR-0613, IADR-0615, IADR-0620, IADR-0621, IADR-0622, IADR-0341, IADR-0094, MSP:IADR-0689]
author: claude (Claude Code)
created: 2026-11-06
updated: 2026-11-06
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0080_cloud-poc-gate-in-cluster-credentials-and-pre-secret-reachability.md (決定 1・2・3)
  - planning:projects/ai-stock-trading/07_adr/ADR-0078_cloud-poc-hetzner-eu-premises-and-migration-gate.md (決定 5・6)
---

# IADR-0623: クラウドの秘密の OpenBao からの供給と、段 2 の強制

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: **Accepted**
- 日付: 2026-11-06
- 決定者: オーケストレーター（骨子の承認 2026-11-06: 秘密の一覧と段 2 の表・PR の分け方・TOTP の登録は OpenBao へ・`ast-operator-hold` は管理の外）／ 基盤（MSP の IADR-0689 決定 6 の取り決め）／ Claude Code（起案・実装）

## 起点・関連

- issue: #1560（クラウドへの移行の管理）。基盤の側: MSP#2471（MSP の IADR-0689）。
- 前提: IADR-0608（values-cloud の重ね方・DB のパスワードの Secret）・IADR-0621（基盤への TLS）・IADR-0341 / IADR-0094（ESO の受け口）。段の守り: IADR-0610・0611・0615・0620・0622。
- 実装仕様書: [`.ai-context/specs/20261106_1560_cloud-openbao-eso-and-stage2.md`](../specs/20261106_1560_cloud-openbao-eso-and-stage2.md)

## コンテキストと課題

- 計画 ADR-0080 決定 1 は、外から持ち込む資格情報・クラスタの中で乱数で作る資格情報・TLS の秘密鍵を、OpenBao から供給されるまでクラウドへ置かないと定める。k8s の Secret に作ってから移す形は採らない。
- values-cloud の初版（IADR-0608）は、秘密を手で作る k8s の Secret から読んでいた（`externalSecrets.enabled: false`）。
- 段 1（記録だけ）と段 2（強制）の 2 段で作った守りは、どれも既定が段 1 である。#1560 §3 は「クラウドでは段 2 が強制になっていること」を移行の門の条件とする。
- 基盤は、名前空間ごとの `SecretStore openbao` と、KV v2 の `cloud/` の置き場の境界を取り決めた（MSP の IADR-0689 決定 6）。取引ユニットの名前空間のストア・ServiceAccount・ExternalSecret は取引ユニットが置く。

## 検討した選択肢

1. **chart に名前空間の SecretStore と ExternalSecret を足し、values-cloud で有効にする（採用）** — 既定と経路 B の描画は変えない（値で切り替える）。描画で秘密の参照と同期先を突き合わせられる。
2. 基盤の側に取引ユニットの名前空間の ExternalSecret を置く — 取り決め（取引ユニットの名前空間の物は取引ユニットが置く）に反し、Secret の名前・キーと消費側の参照が別のリポジトリに分かれる。採らない。
3. 手で作る k8s の Secret を残し、値だけ OpenBao から手で写す — ADR-0080 決定 1 の「k8s の Secret に作ってから移す形は採らない」と同じ問題（写すまでゲートの外に秘密がある・写した値が古くなる）。採らない。
4. 段 2 を別の PR に分ける — 骨子の承認では kill switch の前提条件だけを後の PR にしてよいとしたが、ボットの対応（#1561）が先に入ったので本体に含める。段 2 を values-cloud にだけ書き、既定と経路 B は段 1 のまま。

## 決定

1. **秘密の一覧と供給元。** values-cloud の描画が参照する Secret の全件を、ADR-0080 決定 1 の区分と OpenBao のパスに照らした（表は実装仕様書の決定 1）。`ast-operator-hold` を除くすべてを ExternalSecret で写す。
   - 基盤と共有の値（`cloud/shared/ast-db`・`ast-rabbitmq-users`・`ast-client-secrets`・`platform-infra-ca`）は基盤が作る。取引ユニットだけの値は `cloud/ast/<k8s の Secret の名前>`、property は Secret のキーの名前。
   - `ast-secrets` は 1 つの ExternalSecret で、`cloud/ast/ast-secrets` の全キー（`dataFrom.extract`）と、基盤と共有の 3 つの機密クライアントの secret（`data`。キーは clientId）を合わせる。client ID は秘密ではないので template の字面で足す（`mergePolicy: Merge`）。
   - 注文の承認の印の鍵は、基盤の `gen`（OpenBao の中で乱数・標準の base64・在るキーは上書きしない）で `cloud/ast/ast-secrets` の `order-approval-signing-key` に作る（32 バイト）。
2. **chart の部品**（既定はどれも無効・空。既定と経路 B の描画はバイト等価）:
   - `externalSecrets.openbaoStore`: 名前空間の `SecretStore`（vault の provider・KV v2・kubernetes 認証）と ServiceAccount（`automountServiceAccountToken: false`）。`serviceAccountRef` に audience（既定 `vault`）を明示する（基盤の role は audience=vault を要求する）。`secretStoreRef.kind` が `SecretStore` でなければ描画で止める。
   - `externalSecrets.deletionPolicy`: クラウドは `Retain`（OpenBao が封印のあいだも写した Secret が残り、消費側が動く。MSP の IADR-0689 決定 5 と揃える）。
   - `externalSecrets.dbCredentials`・`ownerTotp`・`infraCa`: `ast-db-credentials`（`username`・`password`）・`ast-owner-totp`（`secret`）・CA の写し（Secret `platform-infra-ca`）。
   - `global.db.usernameSecret`: DB の利用者名を Secret から読む（基盤は利用者名を乱数の接尾辞つきで作る）。`passwordSecret` と一緒にだけ受ける。既定は与えない＝`global.db.username`（経路 B は `ai`）。
   - `global.infraCa.secretName`: CA を Secret から mount する。`configMap` との併用は描画で止める。
3. **段 2 で始める守り**（values-cloud だけ。既定と経路 B は段 1）:
   - `global.orderApprovalSigning.mode: Enforce`（IADR-0610）・`global.ownerTokenBinding.mode: Enforce`（IADR-0611）・`global.ownerBotScope.mode: Enforce`（IADR-0622）。
   - risk-management・report の `OwnerSecondFactor__Mode: Enforce`（IADR-0615）。
   - risk-management の `RiskControls__KillSwitch__Precondition: Require`（IADR-0620）。
   - 前提は揃っている: ボットが確認コードを運ぶ（#1552）・版を添える（#1561）。クラウドの realm に owner の aud のスコープとマッパー、`trading-bot` が残る（基盤が確かめ、試験で固定した）。
4. **`ast-operator-hold` は管理の外に置く。** 秘密ではない。SSH から入れる停止の切り替えである（IADR-0613。値は `enabled` と `reason` だけで、認証にも保存データの保護にも使わない＝ADR-0080 決定 1 の「同じ種類」に当たらない）。OpenBao を経ると、停止の口が秘匿管理の稼働（封印・ESO）に依存するので、従来どおりオーナーが `kubectl` で作る・消す。
5. **所有者の TOTP は OpenBao へ入れる。** `scripts/ast-owner-totp-enroll.sh --live --openbao` は、シークレットを基盤の `put-external-secret.sh cloud/ast/ast-owner-totp secret` の標準入力へだけ流し（引数・環境変数・履歴・ディスクに残さない）、ESO に同期を求めて写し終えてから risk-management と report を作り直させる。基盤の呼び方は MSP の PR ② で確定する（それまでは前置きを環境変数 `AST_PUT_EXTERNAL_SECRET` で受ける）。
6. **OpenD の RSA の鍵は 1024 ビットで、オーナーが手元から入れる。** 手元の PoC の鍵は 1024 ビット（手順書の作り方と、経路 B で使った基盤の画面の生成器がどちらも 1024）。2048 で OpenD が動いた記録は無く、OpenBao の `gen-rsa` は 2048 未満を作れない。オーナーが `openssl genrsa -traditional 1024` の出力を `put-external-secret.sh cloud/ast/moomoo-rsa opend_rsa.pem` へ直にパイプで流す（鍵をディスクに書かない）。
7. **検査**: helm.yml の段「Assert values-cloud secrets from OpenBao and stage 2」が、描画の参照する Secret がすべて（`ast-operator-hold` を除く）ExternalSecret の同期先であること、ストアの形（role・ServiceAccount・audience）、利用者名の Secret、段 2、既定と経路 B の利用者名 `ai`・段 1・部品の不在を確かめる。

## 結果

- クラウドでは、取引ユニットが読む秘密はすべて OpenBao から写される。手で作る Secret は `ast-operator-hold`（秘密ではない）だけになる。
- OpenBao と ESO が無いクラスタで values-cloud を当てると、ExternalSecret・SecretStore の CRD が無く apply が失敗する（基盤の手順で ESO を先に立てる）。
- 段 2 で始めるため、鍵・TOTP・前提条件が揃わない操作は止まる側に倒れる（注文の承認の鍵が無ければ発行元と受け手の Pod が起動しない。TOTP が無ければ 7 操作は 403。版の無い解除は 428）。

## 残余・フォローアップ

1. 基盤の `put-external-secret.sh`（MSP の PR ②）の呼び方が確定したら、手順書と TOTP の登録スクリプトを合わせる（仮の印を外す）。
2. 段 2 で拒まれたときのボットの専用の文言（IADR-0615 の 2026-11-06 の追記の ②）。いまは認可の失敗と同じ汎用の文言である。
3. OpenD の RSA の鍵を 2048 ビットへ上げられるかは、クラウドで OpenD が動いてから確かめる（上げられれば `gen-rsa` で OpenBao の中で作れる）。
4. Secret の書き換えで消費側を作り直す仕組み（Reloader）はクラウドに無い。値を替えたら手で `rollout restart` する（手順書）。
