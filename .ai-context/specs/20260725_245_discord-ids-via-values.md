---
title: Discord Bot の環境固有 ID を values 経路で設定可能にし kubectl set env のフィールドマネージャ競合を根絶する
type: spec
status: review
related_ids: [FR-09, FR-14, UC-06, ADR-0006]
author: endazon (with Claude Code)
created: 2026-07-25
updated: 2026-07-25
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
  - planning:projects/ai-stock-trading/03_usecases/01_usecases.md
  - planning:projects/ai-stock-trading/07_adr/ADR-0006_hosting-hetzner.md
---

# 仕様書: Discord Bot の環境固有 ID を values 経路で設定可能にする

> Issue [#245](https://github.com/endazon/ai-stock-trading/issues/245)。**デプロイ構成（chart の設定点）の追加**であって、
> 機能追加でも認可の緩和でもない。実弾 triple-latch（`Broker__Provider=paper` / `Broker:Moomoo:TrdEnv=simulate` /
> 起動時 real 拒否・[IADR-0060](../adr/IADR-0060_opend-production-cutover-gates.md)）と SIMULATE 前提には一切触れない。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-09（通知）、FR-14（Discord からの操作＝双方向 Bot・多層認証）
- ユースケース（UC）: UC-06（緊急停止＝Bot 経由の kill switch 操作）
- ADR: ADR-0006（ホスティング・インフラ＝デプロイ構成/GitOps の文脈）
- 関連 IADR:
  - [IADR-0062](../adr/IADR-0062_discord-bot-gateway-and-authorization.md)（双方向 Bot・多層認証・**空既定＝全拒否**の安全既定）
  - [IADR-0098](../adr/IADR-0098_owner-realm-client.md)（Bot 制御コマンドの OwnerAuth・token エンドポイントの template 注入）
  - [IADR-0100](../adr/IADR-0100_route-b-values-local-standing-config.md)（経路B の `values-local.yaml` 恒常設定・**本番バイト等価**）
  - [IADR-0058](../adr/IADR-0058_helm-chart-ci-gate.md)（Helm chart の CI ゲート＝派生描画の検査）
  - 本作業で新規 [IADR-0102](../adr/IADR-0102_discord-env-ids-via-values.md)
- 対象 Issue: [#245](https://github.com/endazon/ai-stock-trading/issues/245)

## 目的・背景

Discord Bot の環境固有 ID（`GuildId` / `ChannelId` / `AllowedUserIds` / `UserMapping`）は**非機密**だが、chart では
`values.yaml` / `values-local.yaml` ともに**空既定**（IADR-0100 決定5）であり、値を与える設定点が chart に無かった。
その結果、実運用では次の手順で注入することになる。

```bash
kubectl set env deploy/notification-service -n ai-stock-trading Notifications__Discord__Bot__GuildId=...
```

**問題（本セッションで実発生）**: `kubectl set env` は当該 env をフィールドマネージャ `kubectl-set` の所有にする。
次回 `scripts/k8s-local-deploy.sh` が回す `helm upgrade` は同じフィールド（`spec.template.spec.containers[].env`）を
Helm の所有として apply しようとするため、**server-side apply の所有権競合**（`conflict with "kubectl-set"`）で
`helm upgrade` が失敗する。回避には毎回手で env を剥がす必要があり、標準手順が壊れる。

**本質**: 環境固有 ID の設定点が chart に無いため、chart の外（`kubectl set env`）で env を書き換える運用が生まれ、
「env の所有者が Helm と kubectl に割れる」状態を作っている。**設定点を chart 内に用意し、Helm に env を単独所有させる**
のが根治である。

## 方針

### 決定（詳細は [IADR-0102](../adr/IADR-0102_discord-env-ids-via-values.md)）

1. **chart に `discord.bot.*` の設定点を新設する**（トップレベル。`moomoo` / `tradingCycle` と同じ位置づけ）。
   `guildId` / `channelId` / `allowedUserIds` / `userMapping` の 4 値・**すべて空既定**。
2. **テンプレートは「env 名でのオーバーライド」として適用する**。`templates/deployment.yaml` の `extraEnv` 描画で、
   notification に限り `discord.bot.*` の**非空値だけ**を対応する env 名の値へ差し替える。
   - **env を追加しない**（既存の `extraEnv` 要素の値を置換する）。同名 env の二重定義を作らないため。
   - **空既定は何も差し替えない** → 既定描画（本番）と `values-local` 描画は**バイト等価**のまま。
3. **`scripts/k8s-local-deploy.sh` が env → `--set-string` で渡す**。環境変数名は既存慣例（`DISCORD_BOT_TOKEN` 等）に
   合わせて `DISCORD_BOT_GUILD_ID` / `DISCORD_BOT_CHANNEL_ID` / `DISCORD_BOT_ALLOWED_USER_IDS` /
   `DISCORD_BOT_USER_MAPPING`。未設定＝空＝差し替えなし（no-op の fail-safe）。
   - `--set-string`（`--set` ではない）: 巨大な数値 ID（Discord snowflake は 18〜19 桁）は `--set` だと float64 に
     解釈され `1.234567890123456e+18` に化ける。
   - **カンマ・バックスラッシュはエスケープする**: `AllowedUserIds` / `UserMapping` はカンマ区切りであり、helm の
     `--set` パーサはカンマを要素区切りとして解釈するため、値中の `,` と `\` を `\` でエスケープしてから渡す。
4. **secret（Token / kill switch フレーズ / OwnerAuth）は従来どおり `ast-secrets` の `secretKeyRef`**。本変更は
   **非機密 ID のみ**を values 経路に載せる（機密を values/`--set` に平文で置かない）。

### 非目標（やらないこと）

- 認可ロジック・`DiscordBotOptions` の C# 実装には触れない（**アプリコードの変更は無い**）。空＝全拒否の安全既定
  （IADR-0062 決定2）は不変。
- `values.yaml`（本番・ArgoCD が描画する唯一の values）の**描画結果**を変えない。設定点（空既定のキー）を足すだけ。
- 機密の values 化はしない。

## 変更対象

| ファイル | 変更 |
| --- | --- |
| `deploy/helm/ai-stock-trading/values.yaml` | トップレベル `discord.bot.{guildId,channelId,allowedUserIds,userMapping}`（空既定）を追加 |
| `deploy/helm/ai-stock-trading/templates/deployment.yaml` | notification の `extraEnv` 値を `discord.bot.*` の非空値で上書き |
| `deploy/helm/ai-stock-trading/values-local.yaml` | 4 ID のコメントを「`discord.bot.*`／env で与える」旨へ更新（値は空既定のまま） |
| `scripts/k8s-local-deploy.sh` | `DISCORD_BOT_*` env → `--set-string discord.bot.*`（カンマ/バックスラッシュ escape） |
| `deploy/helm/ai-stock-trading/README.md` | 設定点・env 表・`kubectl set env` 禁止と競合時の解消手順 |
| `.github/workflows/helm.yml` | 描画検査（ID が入る／空既定はバイト等価／`--set-string` の空指定も等価） |
| `docs/adr/IADR-0102_*.md` | 実装 ADR（新規） |

## 受け入れ基準

| # | 基準 | 検証 |
| --- | --- | --- |
| 1 | `--set-string discord.bot.guildId=...` 等で notification の該当 env に値が入る | `helm.yml`「Assert discord.bot.* overrides notification env」 |
| 2 | 空既定（未指定）の描画は変更前と**バイト等価**（本番 `values.yaml` 描画・`values-local` 描画とも） | 変更前後の `helm template` の diff（ローカル実測）＋ `helm.yml`「空 `--set-string` は既定描画と一致」 |
| 3 | 上書きは notification のみに効き、他サービスの env を汚さない | `helm.yml`（描画の Deployment 単位検査） |
| 4 | 上書きは `extraEnv` の値**置換**であり、同名 env を二重に描画しない | `helm.yml`（該当 env 名の出現回数 = 1） |
| 5 | 値を与えても実弾/危険既定は OFF（`Broker__Provider=paper`・`opend`/`ExternalSecret` 不在） | `helm.yml` 既存アサート＋本変更の派生描画 |
| 6 | `values-local` プロファイルにも上書きが効く（経路B の標準手順で使える） | `helm.yml`（`-f values-local.yaml` ＋ `--set-string` の描画） |
| 7 | 機密（Token/フレーズ/OwnerAuth）は values に載らず `secretKeyRef` のまま | 描画に `secretKeyRef` が残ることを確認・gitleaks green |
| 8 | README/spec に「ID は env で渡す・`kubectl set env` は使わない」と競合時の解消手順が載る | 本仕様書・chart README |

## テスト（TDD）

CI ゲートは `helm.yml`（IADR-0058: helm バイナリのみで完結・実 API サーバ非依存）。本変更で追加する検査:

1. **上書きが効く**: `--set-string discord.bot.guildId=<18 桁のダミー ID>` 等 4 値を与えた描画で、notification の
   `Notifications__Discord__Bot__{GuildId,ChannelId,AllowedUserIds,UserMapping}` にその値が現れる。
   snowflake が指数表記に化けていないこと（`e+` を含まないこと）も同時に検査する。
2. **空既定はバイト等価**: `helm template ast $CHART` と `helm template ast $CHART --set-string discord.bot.guildId=""
   ...`（4 値とも空）が**バイト一致**する。空指定が誤って `value: ""` 以外を作らないことの担保。
3. **二重定義しない**: 上書き描画で該当 env 名の出現回数が各 1 回（`env` の重複エントリを作らない）。
4. **notification 限定**: 上書き描画で該当 env は notification-service の Deployment にのみ現れる。
5. **`values-local` でも効く**: `-f values-local.yaml` ＋ `--set-string` の描画で 1 と同じ結果になり、かつ
   `Broker__Provider=paper`・`opend`/`ExternalSecret` 不在が維持される。

ローカル実測（TDD の赤→緑）:

- 実装前に `helm template`（既定 / `values-local`）の出力を保存 → 実装後に diff が空（受け入れ基準2）。
- 実装前は `--set-string discord.bot.guildId=...` を与えても描画が変わらない（設定点が無い＝赤）→ 実装後は値が入る（緑）。

.NET コードの変更は無いため `dotnet format` / `dotnet test` は該当なし（chart・スクリプト・ドキュメントのみ）。

## 運用手順（README にも記載）

```bash
export DISCORD_BOT_GUILD_ID=...            # サーバー（ギルド）ID
export DISCORD_BOT_CHANNEL_ID=...          # 運用チャンネル ID
export DISCORD_BOT_ALLOWED_USER_IDS=...    # 許可ユーザー ID（カンマ区切り）
export DISCORD_BOT_USER_MAPPING=...        # "discordUserId:keycloak利用者名" のカンマ区切り
scripts/k8s-local-deploy.sh
```

**`kubectl set env` は使わない**（使うと次回の `helm upgrade` が `conflict with "kubectl-set"` で失敗する）。
既に `kubectl set env` で注入して競合している場合の解消:

```bash
kubectl set env deploy/notification-service -n ai-stock-trading \
  Notifications__Discord__Bot__GuildId- Notifications__Discord__Bot__ChannelId- \
  Notifications__Discord__Bot__AllowedUserIds- Notifications__Discord__Bot__UserMapping-
scripts/k8s-local-deploy.sh
```

（`KEY-` は当該 env の削除。削除により `kubectl-set` の所有が外れ、以後は Helm が単独所有する。）

## リスクと緩和

| リスク | 緩和 |
| --- | --- |
| ID を値として渡すと本番描画が変わる | 空既定＋非空時のみ差し替え。CI が既定描画のバイト等価を検査（基準2） |
| snowflake ID の数値化け | `--set-string` を使用。CI が `e+` 不在を検査 |
| カンマ区切り値が helm パーサで分割される | スクリプトで `,` `\` をエスケープ。README に手動 `--set` 時の注意を記載 |
| 認可が緩む | 空＝全拒否（IADR-0062）を維持。値を与えるのは利用者の明示操作のみ。アプリコード不変 |
| 機密が values/`--set` に露出 | 対象は**非機密 ID のみ**。Token/フレーズ/OwnerAuth は `secretKeyRef` 据置。gitleaks green |
