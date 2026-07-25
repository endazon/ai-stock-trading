---
title: IADR-0102 Discord Bot の環境固有 ID は chart の values 経路で与える（kubectl set env を使わない）
type: impl-adr
status: Accepted
related_ids: [FR-09, FR-14, UC-06, ADR-0006]
author: endazon (with Claude Code)
created: 2026-07-25
updated: 2026-07-25
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
  - ../../planning/projects/ai-stock-trading/03_usecases/01_usecases.md
  - ../../planning/projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md
---

# IADR-0102: Discord Bot の環境固有 ID は chart の values 経路で与える（`kubectl set env` を使わない）

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-25
- 決定者: endazon（利用者・マージ判断）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-09（通知）、FR-14（Discord からの操作＝双方向 Bot）、UC-06（緊急停止＝Bot 経由の kill switch）、
  ADR-0006（ホスティング・インフラ＝デプロイ構成/GitOps の文脈）
- 対象 Issue: [#245](https://github.com/endazon/ai-stock-trading/issues/245)
- 関連する実装仕様書: [20260725_245_discord-ids-via-values](../specs/20260725_245_discord-ids-via-values.md)
- 関連 IADR: [IADR-0062](IADR-0062_discord-bot-gateway-and-authorization.md)（双方向 Bot・多層認証・空既定＝全拒否）、
  [IADR-0098](IADR-0098_owner-realm-client.md)（Bot 制御コマンドの OwnerAuth・token エンドポイントの template 注入）、
  [IADR-0100](IADR-0100_route-b-values-local-standing-config.md)（経路B の `values-local.yaml`・**本番バイト等価**の原則）、
  [IADR-0058](IADR-0058_helm-chart-ci-gate.md)（Helm chart の CI ゲート＝派生描画の検査）、
  [IADR-0052](IADR-0052_k8s-helm-chart-shared-infra.md)（経路B のローカル k8s デプロイ）

## 背景・課題

Discord Bot の多層認証（IADR-0062 決定2）は 4 つの**環境固有 ID** を要求する。

| 設定キー | 意味 | 空のときの挙動 |
| --- | --- | --- |
| `Notifications:Discord:Bot:GuildId` | 運用サーバー（ギルド）ID | 全拒否 |
| `Notifications:Discord:Bot:ChannelId` | 運用チャンネル ID | 全拒否 |
| `Notifications:Discord:Bot:AllowedUserIds` | 許可ユーザー ID（カンマ区切り） | 全拒否 |
| `Notifications:Discord:Bot:UserMapping` | `discordUserId:利用者名` のカンマ区切り | 対応の無いユーザーは操作不可 |

これらは**非機密**（サーバー/チャンネル/ユーザーの識別子であり、認証情報ではない）だが、chart では
`values.yaml`・`values-local.yaml` ともに**空既定**であり（IADR-0100 決定5）、**値を与える設定点が chart に無かった**。

結果として実運用では `kubectl set env deploy/notification-service ...` で注入することになる。ところが
`kubectl set env` は当該フィールド（`spec.template.spec.containers[].env`）を**フィールドマネージャ `kubectl-set` の
所有**にするため、次回 `scripts/k8s-local-deploy.sh` の `helm upgrade` が同じフィールドを Helm 所有として apply
しようとして**所有権競合（`conflict with "kubectl-set"`）で失敗する**（本 Issue の実発生事象）。

根本原因は「設定点が chart に無いこと」であり、競合はその症状である。**env の所有者を Helm 一者に戻す**のが根治。

## 決定

1. **chart に設定点 `discord.bot.*` を新設する。** トップレベル（`moomoo` / `tradingCycle` と同じ位置づけ）に
   `guildId` / `channelId` / `allowedUserIds` / `userMapping` の 4 値を置き、**すべて空既定**とする。
   - 空既定は IADR-0062 の安全既定（空＝「全許可」ではなく**全拒否**）をそのまま維持する。設定点の追加は
     認可の緩和ではない。
2. **テンプレートは「env の追加」ではなく「`extraEnv` の値の上書き」として実装する。**
   `templates/deployment.yaml` は notification に限り、`discord.bot.*` の**非空値**を対応する env 名
   （`Notifications__Discord__Bot__{GuildId,ChannelId,AllowedUserIds,UserMapping}`）の値へ差し替える。
   - **理由**: これらの env は `values.yaml` / `values-local.yaml` の `extraEnv` に既に空値で存在する。追加方式では
     同名 env が二重に描画され、どちらが効くかを kubelet のエントリ順に委ねることになる。値の置換なら
     **描画される env の集合・順序が変わらない**。
   - **理由（本番バイト等価）**: 空既定では一切差し替えないため、`helm template ast <chart>`（＝ArgoCD が描画する
     本番）と `-f values-local.yaml` の描画は**変更前とバイト等価**のままである（IADR-0100 の原則を維持）。
3. **値の供給は `scripts/k8s-local-deploy.sh` の env → `--set-string` とする。** 環境変数名は既存慣例
   （`DISCORD_BOT_TOKEN` 等）に合わせ `DISCORD_BOT_GUILD_ID` / `DISCORD_BOT_CHANNEL_ID` /
   `DISCORD_BOT_ALLOWED_USER_IDS` / `DISCORD_BOT_USER_MAPPING`。未設定＝空＝差し替えなし（fail-safe）。
   - **`--set` ではなく `--set-string`**: Discord snowflake は 18〜19 桁で、`--set` では float64 に解釈され
     `1.234567890123456e+18` に化ける（ID が壊れると全拒否側に倒れるが、原因が分かりにくい）。
   - **`,` と `\` はエスケープしてから渡す**: `AllowedUserIds` / `UserMapping` はカンマ区切りであり、helm の `--set`
     パーサはカンマを要素区切りとして解釈する。スクリプト側で `sed 's/[\\,]/\\&/g'` 相当の退避を行う。
4. **機密は values 経路に載せない。** Bot Token・kill switch 確認フレーズ・OwnerAuth クライアント資格情報は従来どおり
   `ast-secrets` の `secretKeyRef`（`optional: true`）のままとする。本決定が values 化するのは**非機密 ID のみ**。
5. **`kubectl set env` を運用手順から排除する。** chart README と作業仕様書に「ID は env → `--set-string` で渡す。
   `kubectl set env` は使わない」と明記し、既に競合している場合の解消（`KEY-` で当該 env を削除してから再 helm）も
   併記する。

## 根拠・比較検討

| 案 | 内容 | 評価 |
| --- | --- | --- |
| **A（採用）** | `discord.bot.*` を設定点にし、template が `extraEnv` の値を上書き。スクリプトが env→`--set-string` | Helm が env を単独所有＝競合が起きない。空既定で本番バイト等価。env の二重定義なし |
| B | `--set services.notification.extraEnv[4].value=...` で直接指定 | リスト**添字**依存。`extraEnv` の要素を 1 つ足しただけで別のキーを書き換える事故になる。却下 |
| C | template で 4 env を**追記**（`extraEnv` はそのまま） | 同名 env が二重定義される。描画差分も出る（本番バイト等価が崩れる or 追記位置の議論が必要）。却下 |
| D | `values.yaml` から 4 要素を除き template 側へ移す | 本番描画の env 順序が変わる＝バイト等価が崩れる。却下 |
| E | `kubectl set env` を続け、helm 実行前に毎回剥がす | 標準手順が手作業に依存し、忘れると `helm upgrade` が失敗する。症状対処であり根治でない。却下 |
| F | ID も `ast-secrets`（Secret）に入れる | 非機密を Secret に混ぜると「Secret にあるものは機密」という運用の判別が濁る。値の可視性も落ちる。却下 |

## 影響・結果

- **良くなること**: `kubectl set env` を使わずに ID を与えられ、再デプロイでも値が保持される。フィールドマネージャ
  競合（`conflict with "kubectl-set"`）が構造的に起きない。ID の設定が chart の CI・レビューの内側に入る。
- **変わらないこと**: アプリコード（`DiscordBotOptions`・認可ロジック）は不変。空＝全拒否の安全既定も不変。
  本番 `values.yaml` の描画はバイト等価。実弾 OFF・SIMULATE 前提（IADR-0060）に不関与。
- **注意**: 手で `helm --set-string` を打つ場合、カンマを含む値（`AllowedUserIds` / `UserMapping`）は自分で
  `\,` にエスケープする必要がある（スクリプト経由なら自動）。

## 検証

`helm.yml`（IADR-0058: helm バイナリのみで完結）に以下を追加する。

1. `--set-string discord.bot.*` を与えた描画で notification の該当 env に値が入り、指数表記（`e+`）に化けていない。
2. 4 値とも空の `--set-string` を与えた描画が**既定描画とバイト一致**（空指定が描画を変えない担保）。
3. 上書き描画で該当 env 名の出現回数が各 1（二重定義しない）。
4. 該当 env は notification-service の Deployment にのみ現れる。
5. `-f values-local.yaml` ＋ `--set-string` でも 1 と同じ結果になり、`Broker__Provider=paper`・`opend`/`ExternalSecret`
   不在（実弾/危険既定 OFF）が維持される。
