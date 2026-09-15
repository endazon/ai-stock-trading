---
title: MSP 連結ローカル配備で AST の秘密情報・接続設定を ESO（Vault）所有にし、画面（基盤 SC-22 / SC-04）だけで PoC を立ち上げられるようにする
type: spec
status: draft
related_ids: [SC-04, FR-09, FR-14, ADR-0006, ADR-0038, IADR-0341, IADR-0060, IADR-0094, IADR-0102, IADR-0109, IADR-0283, IADR-0295, IADR-0322]
author: endazon (with Claude Code)
created: 2026-09-15
updated: 2026-09-15
plan_refs:
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md
  - planning:projects/ai-stock-trading/05_screens/01_screens.md
---

# 仕様書: 画面だけで立ち上がる ESO 配線（#795）

## 起点となる計画書（トレーサビリティ）

- 画面（SC）: SC-04（OpenD 認証操作＝検証コードの入力）。秘密情報の入力面は基盤の `MSP/SC-22`（秘密情報・接続設定の管理）
- 機能要求（FR）: FR-09 / FR-14（Discord 通知・双方向操作。環境固有 ID の供給経路が変わる）
- 関連 ADR: ADR-0006（稼働環境・Vault 秘匿）、ADR-0038（連結配備の認証レルム＝基盤レルム。values-local の前提）
- 起点 issue: #795（利用者指示 2026-09-15「PoC の立ち上げをすべて画面から」）。対になる基盤側は MSP#1477（別エージェントが並行実装）
- 設計判断: IADR-0341（新規）

## 目的・背景

基盤 SC-22 で Vault（`secret/ai-stock-trading/*`）へ書いた値が、MSP 連結のローカル配備（`VAULT=1 ESO=1`）で AST の Pod へ届かない。
穴は 3 つ（#795 本文）: ① chart の `externalSecrets.enabled` / `appSecrets.enabled` が values-local でも false、
② `moomoo-credentials` / `moomoo-rsa` はコンソールで作る前提、③ Discord の環境固有 ID 4 件は Helm values 経由で画面から入らない。

## 共通の契約（#795 / MSP#1477 で同一。本作業では変更しない）

| Vault KV（mount `secret`） | プロパティ | 同期先 Secret（ns ai-stock-trading） |
| --- | --- | --- |
| `ai-stock-trading/app-secrets` | 既存 15 キー＋新設 `discord-bot-guild-id` / `discord-bot-channel-id` / `discord-bot-allowed-user-ids` / `discord-bot-user-mapping` | `ast-secrets`（`dataFrom.extract`・キー名＝プロパティ名） |
| `ai-stock-trading/moomoo` | `login-account` / `login-pwd-md5` | `moomoo-credentials` |
| `ai-stock-trading/moomoo-rsa` | `opend_rsa.pem` | `moomoo-rsa` |

- BFF（MSP）は書き込み後に上記名の ExternalSecret へ force-sync 注釈を付ける → **ExternalSecret 名は同期先 Secret 名と同一であること**（現行テンプレートで確認済み: `metadata.name` が `moomoo-credentials` / `moomoo-rsa` / `ast-secrets`）。
- 消費側 Deployment の再起動は Stakater Reloader（MSP が ESO=1 で導入）。**OpenD には reload 注釈を付けず、`reloader.stakater.com/auto: "false"` で明示的に除外する**。

## 対象範囲

- 対象: `deploy/helm/ai-stock-trading/{values.yaml,values-local.yaml,templates/deployment.yaml,templates/external-secrets.yaml}`、
  `.github/workflows/helm.yml`、`scripts/k8s-local-deploy.sh` / `.test.sh`、`deploy/opend/README.md`、chart README、
  `docs/operations/vault-secrets-runbook.md`、`scripts/README.md`（行の追随）、IADR-0341 と索引
- 対象外: 基盤側（seed・BFF・Reloader 導入・SC-22 画面。MSP#1477）、.NET のコード（env 名は不変＝`DiscordBotOptionsReader` は無改修）、
  本番 values.yaml の有効化（ArgoCD 描画は不変）、dev 生 manifest（`deploy/opend/k8s/`）

## 設計

1. **values-local** で `externalSecrets.enabled=true` / `appSecrets.enabled=true`（store は既定の `vault-backend` / `ClusterSecretStore`）と `reloader.enabled=true`。
2. **ExternalSecret の apiVersion を `external-secrets.io/v1` へ**。基盤は ESO chart 2.8.0 を pin し「v1beta1 の提供を停止した版」と記録して自前の manifest を v1 へ移している（MSP `scripts/k8s-local-up.sh`）。v1beta1 のままでは連結ローカルで `no matches for kind` になり helm upgrade が失敗する。既定描画には ExternalSecret が無いので本番描画は不変。
3. **Discord ID 4 件**: `deployment.yaml` の notification で、`externalSecrets.enabled && appSecrets.enabled` のとき
   `Notifications__Discord__Bot__{GuildId,ChannelId,AllowedUserIds,UserMapping}` を `secretKeyRef{name: appSecrets.targetName, key: discord-bot-*, optional: true}` で描く。
   無効時は従来の values 経路（`discord.bot.*` の上書き）のまま。**両方に非空値がある（appSecrets 有効かつ discord.bot.* 非空）は描画時に止める**（黙って片方を無視しない。broker.tier の矛盾指定と同じ規律）。
4. **Reloader**: `reloader.enabled`（values.yaml 既定 false）のとき、AST の各 Deployment の `metadata.annotations` に
   `secret.reloader.stakater.com/reload: <消費する Secret 名のカンマ区切り>` を描く。名前は extraEnv の `secretKeyRef.name`・Discord 切替分・order-execution の moomoo 経路の RSA Secret から**描画時に導出**する（手書きの一覧を持たない）。消費 Secret が無いサービスには描かない。**`templates/opend.yaml` には reload 注釈を描かず、`reloader.enabled` のときだけ `reloader.stakater.com/auto: "false"` を描く**（監査指摘: 導入側の全体自動に対する多重防御。`ignore` は Secret / ConfigMap 側の注釈で Deployment には効かないため MSP#1478 の監査で改めた）。
5. **deploy script**: `AST_ESO`（`1` / `0` / 未設定＝プロファイルから導出）で分岐し、helm へ `externalSecrets.enabled` / `appSecrets.enabled` を常に明示する。
   - ESO モード: 事前確認（CRD `externalsecrets.external-secrets.io`・ClusterSecretStore が無ければ案内して中断）→ `sync_ast_secrets` を呼ばない → 管理外（`ownerReferences` に ExternalSecret が無い）既存 Secret があれば名前を挙げて中断（削除しない。`--adopt-existing-secrets` のときだけ 1 回警告して進む。監査指摘: 警告だけでは画面で先に値を入れる機会が無い）→ `DISCORD_BOT_*` / 前回リリースの `discord.bot.*` を引き継がない（使われない旨を警告）→ export 済みの鍵 env は無視する旨を警告（値は出さない）。
   - 非 ESO モード（`AST_ESO=0`）: 従来どおり（`sync_ast_secrets`・discord.bot.* 引き継ぎ）＋ helm へ両フラグ false。
6. **OpenD の待機**: `moomoo-credentials` は非 optional の `secretKeyRef`、`moomoo-rsa` は非 optional の secret volume。Secret 不在の間 Pod は
   `ContainerCreating`（volume の FailedMount）→ 作成後に kubelet の再試行で起動する（Deployment の再作成は不要）。本作業では稼働クラスタに触れないため**実測ではなく Kubernetes の既知挙動として文書化**する。

## 受け入れ基準

- [x] AC1: 既定描画（values.yaml のみ）が変更前とバイト等価（`diff` 0 行）。ExternalSecret・Reloader 注釈は 0 件
- [x] AC2: values-local 描画に ExternalSecret がちょうど 3 件（`ast-secrets` / `moomoo-credentials` / `moomoo-rsa`・`external-secrets.io/v1`・store `vault-backend`・Vault パスが契約どおり）
- [x] AC3: values-local 描画の notification に Discord ID 4 件が `ast-secrets` / 契約キー / `optional: true` の `secretKeyRef` で各 1 回だけ現れる。非 ESO 描画では従来の values 経路（`discord.bot.*` 上書き）が効く
- [x] AC4: appSecrets 有効かつ `discord.bot.*` 非空の描画は失敗する
- [x] AC5: values-local 描画で `ast-secrets` を消費する Deployment に Reloader 注釈が付き、`opend.enabled=true` でも OpenD には reload 注釈が付かず `reloader.stakater.com/auto: "false"` が付く。moomoo-sim の order-execution は `moomoo-rsa` を含む
- [x] AC6: `k8s-local-deploy.test.sh` が ESO / 非 ESO の両経路を固定する（ESO で `ast-secrets` を作成・パッチしない／管理外 Secret での中断・`--adopt-existing-secrets` での警告と非削除／discord.bot.* 非引き継ぎ／CRD 不在で中断／helm へのフラグ明示）。既存 79 件は緑のまま
- [x] AC7: 突然変異 3 種が赤になる: (a) OpenD に注釈 (b) 本番描画の変化 (c) ESO で `ast-secrets` を同期（加えて (d) ESO で discord.bot.* を渡す）
- [x] AC8: README（opend / chart）と Vault runbook が「画面（SC-22 で資格情報・RSA 生成・API キー・Discord ID、SC-04 で検証コード）→ フォールバックとしてコンソール」の順で読める
- [x] AC9: IADR-0341 を作成し索引へ登録。文書検査（trace-blocks / knowledge-graph / cross-repo-refs / plan-id-qualification / doc-links）は緑。`check-commit-messages.js`（`origin/develop..HEAD`・4 件）も適合

> 注: `node scripts/scripts.test.js` はこの Windows 環境で `spawnSync bash ENOENT` により途中終了する。**変更前の `origin/develop`（`d05e873f`）を
> 別ワークツリーで実行しても同じ位置（ok 322 件の直後）で同じ例外になる**ため、本変更に起因しない環境要因である。完走の確認は CI（Linux）に委ねる。

## 検証の証跡（2026-09-15・ワークツリー上の実行）

| 検査 | 変更前（赤） | 変更後（緑） |
| --- | --- | --- |
| `helm template ast <chart>`（既定＝本番） | — | 変更前と `cmp` 一致（sha256 先頭 `8b3378f29a0c32b1`） |
| `helm lint --strict`（既定 / `-f values-local.yaml`） | — | 両方 `1 chart(s) linted, 0 chart(s) failed` |
| helm.yml「Assert screen-only ESO wiring (#795)」 | exit 1「values-local の ExternalSecret が契約の 3 件でない（実測: 「」）」 | exit 0 |
| helm.yml 全 27 ステップ（`run:` を抽出してローカル実行） | — | pass 27 / fail 0 |
| `bash scripts/k8s-local-deploy.test.sh` | 90 passed / 31 failed | 121 passed / 0 failed |
| `dotnet test` NotificationService.Tests（回帰確認・.NET は無変更） | — | 合格 398 / 失敗 0 |
| trace-blocks / knowledge-graph --check / cross-repo-refs / plan-id-qualification / doc-links / reading-budget | — | すべて OK |

突然変異（実行後に元へ戻し、本番描画のバイト等価と 121/0 を再確認）:

| 変異 | 結果 |
| --- | --- |
| M1: OpenD の Deployment に `secret.reloader.stakater.com/reload` を足す | #795 ステップが赤「OpenD に Reloader 注釈が付いた」 |
| M2a: 本番 values で `reloader.enabled=true` | 本番描画が変化／#795 ステップが赤「既定描画に Reloader 注釈」 |
| M2b: 本番 values で `externalSecrets.enabled=true` | #795 ステップと既存「Assert fail-safe defaults」の 2 ステップが赤 |
| M3: ESO 所有でも `sync_ast_secrets` を呼ぶ | 116 passed / 5 failed |
| M4: ESO 所有でも `discord.bot.*` を渡す | 119 passed / 2 failed |

## 実装中に判明した事項

- **契約の「既存 15 キー」に `sec-edgar-user-agent` が含まれない。** AST の `AST_SECRET_KEYS` は 16 件で、基盤 develop の
  `deploy/bootstrap/sc22-secret-items.json`（`ast-app-secrets`: 書ける 7 件＋書けない 8 件）に同キーが無い。ESO 所有の経路では
  SEC EDGAR の User-Agent を画面から入れられず、SEC EDGAR だけが収集対象から外れる（fail-safe）。AST 側の配線は `dataFrom.extract`
  のため変更不要。**契約は本作業で変えず、MSP#1477 側と揃える事項として PR と IADR-0341 の残余リスクに記録する。**
- ExternalSecret の apiVersion `v1beta1` は基盤の ESO（2.8.0）で提供されない。受け口を有効化した時点で helm upgrade が落ちる潜在不具合だったため `v1` へ上げた（既定描画は不変）。
- helm.yml の新ステップの初版は `target:` 直下の説明コメント行を読み違えて赤になった（`grep -A1`）。検査側の誤りであり `-A3` に直した。

## テスト方針

- helm: `.github/workflows/helm.yml` に「#795」ステップを追加し、既存 2 ステップ（values-local に ExternalSecret 不在を要求していたもの・discord.bot.* を values-local で上書きしていたもの）を新しい前提へ追随。ローカルでは各ステップの `run:` を抽出して実行する。
- script: `k8s-local-deploy.test.sh` に T-795-* を追加（kubectl スタブへ CRD / ClusterSecretStore / Secret ごとの存在と ownerReferences を足す）。
- .NET: 変更なし（env 名不変）。NotificationService のテストは回帰確認として実行する。

## 母集合（是正・追随の対象）

走査（2026-09-15・ワークツリー `feat/ADR-0006-795-screen-only-eso-wiring` の起点 `d05e873f`）:

```
git grep -n -I "sync_ast_secrets\|appSecrets\|externalSecrets\.\|kind: ExternalSecret\|kubectl create secret generic moomoo\|DISCORD_BOT_GUILD_ID\|reloader" -- ':!.ai-context/specs' ':!CHANGELOG.md'
```

結果（ファイル別件数）: IADR-0060 1 / IADR-0094 1 / IADR-0102 1 / IADR-0109 1 / IADR-0295 3 / adr README 1 / `.env.example` 1 / helm.yml 13 /
`deploy/argocd/README.md` 1 / `deploy/argocd/appproject.yaml` 2 / chart README 11 / `templates/external-secrets.yaml` 12 / values-local 1 / values.yaml 2 /
`deploy/opend/README.md` 3 / `deploy/opend/k8s/{bootstrap-pod,rsa-secret.example,secret.example}.yaml` 各 1 / `docker-compose.yml` 1 /
`docs/infra/infra.md` 2 / `docs/operations/live-trading-cutover-runbook.md` 1 / `docs/operations/operations.md` 2 / `vault-secrets-runbook.md` 6 /
`docs/security/security.md` 1 / `k8s-local-deploy.sh` 4 / `.test.sh` 7。第 2 軸として `v1beta1`（テンプレート 3 件のみ）を引いた。

| 除外 | 理由 |
| --- | --- |
| `.ai-context/adr/IADR-0060` / `0094` / `0102` / `0109` / `0295` | 凍結記録。本文は書き換えず IADR-0341 から参照する（0102 の「values 経路」・0109 の「ESO 環境では env を使わない」は ESO 無効の経路として引き続き正しい） |
| `deploy/argocd/*` | 本番（ArgoCD＝values.yaml のみ）。既定 false は不変。`appproject.yaml` の許可は group/kind 単位で apiVersion 非依存 |
| `docs/operations/operations.md` / `live-trading-cutover-runbook.md` / `docs/security/security.md` | 本番（実弾解禁前提）の Vault 化の充足判定。ローカル連結プロファイルの有効化は本番の充足ではない（判定は不変） |
| `docs/infra/infra.md` 69・87 行 | 「2026-09-03 実測」と日付を持つ時点の実測記録。書き換えると当時の観測と食い違う |
| `deploy/opend/k8s/*.example.yaml` / `bootstrap-pod.yaml` | dev の生 manifest 経路（chart 外）の手動手順。フォールバックとして残す |
| `docker-compose.yml` / `.env.example` | docker compose 経路（k8s ではない）。`.env.example` は本環境のフックが読み取りを禁じており、grep の該当行（`DISCORD_BOT_GUILD_ID` の compose 変数）のみで判断した |

## 計画書との差異

- 差異: なし（計画 ADR-0006 の Vault 秘匿を、ローカル連結プロファイルで実際に使う配線。本番の充足判定は変えない）

## 未決事項

- なし（ESO 既存 Secret の取り込み挙動は ESO 文書で断定できないため、警告文は「値が置き換わる／同期が失敗する」のいずれにも読める形にし、事前に画面で値を入れてから手動削除する手順を案内する）
