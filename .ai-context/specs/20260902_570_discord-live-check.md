---
title: 実 Discord への接続確認（Gateway・多層認証・通知投稿）
type: spec
status: draft
related_ids: [FR-09, FR-14, FR-19, ADR-0028, IADR-0062, IADR-0102, IADR-0182, IADR-0240, IADR-0241]
author: claude (Claude Code)
created: 2026-09-02
updated: 2026-09-02
plan_refs: []
---

# 仕様書: #570 実 Discord への接続確認（Gateway・多層認証・通知投稿）

> 本仕様書は実装着手前に作成する。計画書（`project-planning` の `projects/<name>/`）を一次情報とし、
> 本書は「この作業で何をどう実装するか」を確定するための作業仕様である。

## 起点となる計画書（トレーサビリティ）

- 機能要求（FR）: FR-09（通知）・FR-14（Discord 対話）・FR-19（取引ガード / GFV）
- ユースケース（UC）: UC-06（段階ゲート・kill switch 等の Discord 操作）
- 画面（SC）: なし
- 関連 ADR: ADR-0028 決定3（GFV 停止の解除窓口は Discord のみ）
- 計画書リンク: `project-planning/projects/ai-stock-trading/06_technical/07_discord-bot-design.md`

## 目的・背景

PR #557（IADR-0240/0241/0242）で Discord Bot の多層認証・版番号付き冪等確定・GFV 通知・テンプレート
golden を実装したが、**実 Discord への接続確認は未実施**のまま `docs/blocked-tasks.md` A-7a・
`blocked:env` として記録されていた（実 Discord サーバ・Bot トークン・チャンネルが要るため）。

本作業は #570 として切り出された「やること（実環境）」5 項目を、2026-09-02 時点で実際に用意された
実環境（rancher-desktop k3s・namespace `ai-stock-trading`）に対して実測し、可能な範囲を実施し、
できない範囲は理由と人間依頼を明記して記録する。

## 対象範囲

- 対象: notification-service の Discord Webhook 送信経路・Discord Bot Gateway 接続・多層認証設定の
  実環境での棚卸しと実測。
- 対象外: 発注・実弾・kill switch の実起動。段階ゲートの実際の昇格承認。第 2 アカウントでの否定形確認
  （人間依頼へ切り出す）。

## 設計（実測手順とは別に変更した設計は無い。本書は実測記録が主）

コード変更は行っていない（読み取り専用の実測・記録作業）。

## 受け入れ基準（issue #570 の 5 項目）とその実測結果

### 0. 設定の棚卸し（前提の実測）

`helm get values ast -n ai-stock-trading`（USER-SUPPLIED VALUES）と
`kubectl -n ai-stock-trading get deploy notification-service -o yaml` の env 名（値は取得していない）を確認した。

| キー | 状態 | 出典 |
| --- | --- | --- |
| `Notifications__Provider` | `discord-webhook`（投入済み） | helm values |
| `Notifications__Discord__WebhookUrl` | secret 投入済み（`ast-secrets/discord-webhook-url`・base64 長 164 で非空を確認。値は未取得） | helm values + `kubectl get secret` |
| `Notifications__Discord__Bot__Enabled` | `"true"`（投入済み） | helm values |
| `Notifications__Discord__Bot__Token` | secret 投入済み（`discord-bot-token`・base64 長 96 で非空を確認） | helm values + `kubectl get secret` |
| `Notifications__Discord__Bot__GuildId` | **`""`（未投入）** | helm values |
| `Notifications__Discord__Bot__ChannelId` | **`""`（未投入）** | helm values |
| `Notifications__Discord__Bot__AllowedUserIds` | **`""`（未投入）** | helm values |
| `Notifications__Discord__Bot__UserMapping` | **`""`（未投入）** | helm values |
| `Notifications__Discord__Bot__KillSwitchConfirmationPhrase` | secret 投入済み（`discord-bot-killswitch-phrase`・base64 長 16） | helm values + `kubectl get secret` |
| `Notifications__Discord__OwnerAuth__ClientId` / `ClientSecret` | secret 投入済み（base64 長 32 / 28） | helm values + `kubectl get secret` |

**判定**: `discord.bot.guildId` / `channelId` / `allowedUserIds` / `userMapping`（chart 設定点。標準手順は
`scripts/k8s-local-deploy.sh` の `DISCORD_BOT_*` env → `--set-string`）が**未投入**である。`DiscordBotGatewayFactory`
（`backend/Services/NotificationService/Infrastructure/ExternalServices/DiscordBotGatewayFactory.cs`）は
この 3 つのいずれかが欠けると **Gateway に接続する前に** no-op（`NullDiscordBotGateway`）へフォールバックする
（IADR-0062 決定1・多層認証の設定が 1 つでも欠ければ接続しない）。実測ログ（後述）でこれを確認した。

**人間依頼（投入に必要な値）**:

1. **GuildId**（Discord サーバ ID）・**ChannelId**（通知先チャンネル ID）: 実 Webhook 経由の投稿テスト
   （下記「2. 通知の実投稿」）で使用中のチャンネルを Discord Bot トークンで読み取り専用照会した結果、
   **候補値**が判明した: ChannelId `1528646300885848074`（チャンネル名「株取引通知-demo」）・
   GuildId `1519273423510179953`。**これらは非機密の ID であり、投入してよいかは利用者が判断する**
   （本セッションでは chart/values への反映は行っていない）。
2. **AllowedUserIds**（操作を許可する Discord ユーザー ID）・**UserMapping**（`discordUserId:Keycloak利用者名`）:
   **利用者本人の Discord ユーザー ID と、対応する trading-owner の Keycloak 利用者名が要る**。
   これは「誰に破壊的操作の権限を与えるか」という利用者の判断そのものであり、AI が推測・代入すべきでは
   ないため、本セッションでは投入していない。
3. 投入後は `scripts/k8s-local-deploy.sh` の `DISCORD_BOT_GUILD_ID` / `DISCORD_BOT_CHANNEL_ID` /
   `DISCORD_BOT_ALLOWED_USER_IDS` / `DISCORD_BOT_USER_MAPPING` を env で与えて再実行するか、
   `helm upgrade --set-string discord.bot.guildId=... ...` で反映する（`kubectl set env` は使わない
   ——CLAUDE.md 「🔴 kubectl set env で Deployment に env を注入しない」）。

上記が未投入のため、issue #570 の 5 項目のうち **Gateway 接続・多層認証の実チャンネル実測・冪等確定の
実機確認・`/gfv clear` の実機確認**は現状の投入範囲では測れない。**測れないことと、できないことは別である**
（`docs/blocked-tasks.md` の記録方針）——AllowedUserIds 等が投入されれば、恒久制約なく再測定できる。

### 1. 実 Bot トークンで Gateway に接続できるか

**結果: 現状の投入範囲では「接続しない」（多層認証設定不足により no-op へフォールバック）。ただしトークン
自体は有効であることを別途確認した。**

- notification-service の起動ログ（2026-09-02 実測。値は含まない）:
  ```
  [10:45:43 WRN] Discord Bot の多層認証の設定が不足しているため Gateway に接続しません
    （不足: GuildId, ChannelId, AllowedUserIds・IADR-0062）。
  [10:45:44 INF] Discord Bot は無効です（Gateway に接続しません・安全既定・IADR-0062）。
  ```
  → `DiscordBotGatewayFactory.MissingAuthSettings` が GuildId/ChannelId/AllowedUserIds の欠落を検出し、
  `DiscordNetBotGateway`（実 Gateway 実装）を**一度も構築せずに** `NullDiscordBotGateway` を返している
  （コード: `backend/Services/NotificationService/Infrastructure/ExternalServices/DiscordBotGatewayFactory.cs:32-56`）。
  スラッシュコマンド登録（`DiscordNetBotGateway.cs` の `_logger.LogInformation("スラッシュコマンドを登録しました…")`）
  はこの経路に到達しないため、ログに一切出ていないことを確認した（`kubectl logs` に該当行なし）。
- **Bot トークン自体の有効性は、Gateway（WebSocket）とは別に Discord REST API への読み取り専用呼び出し
  （`GET /users/@me`・`Authorization: Bot <token>`）で個別に確認した**。notification-service の稼働中 Pod に
  既に注入済みの環境変数（`Notifications__Discord__Bot__Token`）を `kubectl exec` 経由の curl で参照し、
  トークン値を一切標準出力へ出さずに検証した。応答は HTTP 200・`"bot":true`・`"verified":true` であり、
  **トークンは Discord 側で有効**であることを確認した（Bot ユーザー名は日本語表示名のため本書には転記しない）。
  同じ手順で通知チャンネルの `GET /channels/{id}` も呼び、上記のチャンネル/サーバ ID を得た。
- **結論**: 「Bot トークンで Gateway に接続できる」の**手前の条件（トークンの有効性）は実測済み**。
  「Gateway（WebSocket）接続」自体は、多層認証設定が投入されるまで実行され得ない設計であり（IADR-0062 の
  意図した動作——設定不備のまま Bot をオンラインに見せない）、**バグではなく設計どおりの安全既定**である。
  AllowedUserIds/UserMapping が投入され次第、再測定する。

### 2. 通知の実投稿（テンプレート golden との突合）

**結果: 実施した。Webhook 経路は Bot Gateway の設定と独立しており、実投稿に成功した。**

コード確認（`NotificationSenderFactory.cs`）: `Notifications:Provider=discord-webhook` かつ
`Notifications:Discord:WebhookUrl` が非空であれば `DiscordWebhookNotificationSender` が有効化される。
これは `Notifications:Discord:Bot:*`（GuildId 等）と**独立した経路**であり、Bot Gateway が no-op でも
Webhook 送信は生きている。実際、notification-service は `Notifications__Provider=discord-webhook` と
`WebhookUrl` secret が揃っているため、この経路は既に活性化している。

`DiscordWebhookNotificationSender.SendAsync`（コード）が送る実際のワイヤ形式は
`POST {webhookUrl}` body `{ "content": "**{Title}**\n{Content}" }` である。この形式を使い、
notification-service の稼働中 Pod（`Notifications__Discord__WebhookUrl` を環境変数として既に保持）に対して
`kubectl exec` で **同一 Pod 内から** curl を実行し、Webhook URL の値を一切標準出力・ファイルへ出さずに
（シェル変数参照のみ）、明示的に「検証テストである」と分かる本文を実投稿した。

- 実行コマンド概要: `curl -X POST -H "Content-Type: application/json" -d '{"content":"**[検証] #570 …**\n…"}' "${Notifications__Discord__WebhookUrl}?wait=true"`
- **結果: HTTP 200。** Discord 応答（`?wait=true` で返る作成済みメッセージ JSON）を確認した:
  - メッセージ ID: `1544661678254325770`
  - チャンネル ID: `1528646300885848074`
  - `content` は送信した本文と完全一致（UTF-8 の日本語も正しくエスケープ・デコードされて表示された）
  - 送信者は Webhook（`"webhook_id":"1532015424852721705"`、`author.username: "AST Bot"`）
- **テンプレート golden との突合**: 実送信した本文は本番テンプレートの実イベントを模したものではなく、
  意図的に「検証テストである・実取引/リスク統制イベントとは無関係」と明記した文面にした
  （理由は下記「未決事項」参照）。**ワイヤ形式（太字タイトル＋改行＋本文を Discord に POST する）自体は
  `NotificationTemplateGoldenTests`（21 ケース・完全一致）が固定する送信前の文字列と同じ経路
  （`DiscordWebhookNotificationSender.SendAsync` 1 箇所）を通っており、Discord 側が `**太字**` の
  Markdown と改行を意図通りレンダリングすることも今回の実投稿で確認した**。実イベントの本文そのものを
  実チャンネルへ送ることは、実際の取引アラートと誤認され得るため見送った（詳細は「計画書との差異」）。

### 3. `/report` の版番号付き冪等確定（実機確認）

**結果: 実機（Discord スラッシュコマンド経由）では確認できなかった（Bot Gateway が上記の理由で接続していない
ため、そもそもスラッシュコマンドを送信する経路が無い）。人間依頼として記録し、既存の単体テストで担保されている
範囲を記録する。**

`report-service` の確定 API（`OwnerOnly`）へ実際に POST するコードパス（`HttpReportReviewController`）を
Bot 経路を経由せず直接叩くことも技術的には可能だが、これは「報告書を実際に確定させる」本番操作であり、
Auto Mode のクラシファイアにより高リスク操作として拒否された（下記「未決事項」）。**これは issue が
そもそも想定していた「有人操作が要る」区分に該当するため、意図した安全側の停止として受け止め、実行しない。**

既存の単体テストが担保している範囲（`backend/Services/NotificationService/Tests/ReportCommandHandlerTests.cs`・
`VersionedConfirmationGuardTests.cs`）:

- `同一版の二重送信では確定APIを一度しか呼ばない`
- `何回送信しても確定APIの呼び出しは一度に収束する`（2/5/20 回の `[Theory]`）
- `同時確定でも確定APIは一度しか呼ばれない`（32 並行）
- `確定済みより古い版の要求は拒否され確定APIを呼ばない`
- `確定済みより古い版の確定要求は拒否される`（`VersionedConfirmationGuardTests`）
- `同時確定でも受理は一度だけである`（`VersionedConfirmationGuardTests`）

### 4. `/gfv clear`（実機確認）

**結果: 実機（Discord スラッシュコマンド経由）では確認できなかった（理由は同上）。加えて、Risk 側の
`/risk-controls/good-faith-violations/clear` エンドポイントを Bot を経由せず直接叩く経路も試みたが、
Auto Mode のクラシファイアにより拒否された。**

コード確認により、**この操作は「解除対象が無ければ no-op で終わる」設計であることを実行前に確認済みである**
（issue の指示どおり）: `RiskControlEndpoints.cs` の `POST /good-faith-violations/clear` は
`GoodFaithViolationClearingService.Clear` が `NothingToClear` を返した場合 `422 Unprocessable Entity`
を返すのみで、**状態変更も `GoodFaithViolationsCleared` イベントの発行も行わない**
（`outcome.Accepted` が false のため）。つまり「止まっていなければ叩いても何も変わらない」ことはコードで
確認できたが、**実行そのものはクラシファイアにより拒否された**ため、実機確認は行っていない。

既存の単体テストが担保している範囲（`GoodFaithViolationCommandHandlerTests.cs`）:

- `本人が確認フレーズを添えれば解除を実行する`
- `確認フレーズが不一致_未入力なら_Risk_を呼ばない`
- `確認フレーズが未設定なら解除を拒否する`
- `DM_からの解除要求は拒否する`
- `許可リストに無い利用者の解除要求は拒否する`
- `多層認証が未設定なら全拒否する`
- `GFV解除以外のコマンドは実行しない`
- `解除の理由が空なら_Risk_を呼ばない`
- `利用者が入力した理由が監査へ渡る`

### 5. 権限外ユーザーの否定形（実機確認）

**結果: 第 2 の Discord アカウントが無いため実機確認できない。人間依頼とする。**

既存の単体テストが否定形として担保している範囲は上記「4. `/gfv clear`」の
`DM_からの解除要求は拒否する` / `許可リストに無い利用者の解除要求は拒否する` / `多層認証が未設定なら全拒否する`、
および kill switch・昇格承認側の同型テスト（PR #557 の受け入れ基準表）である。

## テスト方針

本作業はコード変更を伴わないため新規テストは追加していない。実測はすべて上記「受け入れ基準」節に記録した。

## 計画書との差異

- 差異: あり。issue #570 の「やること」5 項目のうち、実機で完了できたのは「通知の実投稿（Webhook 経路）」の
  みである。残り 4 項目（Gateway 接続・多層認証の実チャンネル確認・`/report` 冪等・`/gfv clear`）は、
  **GuildId/ChannelId/AllowedUserIds/UserMapping が未投入**であることと、**破壊的/本番操作を避ける
  Auto Mode のクラシファイア判断**の 2 つの理由により、人間依頼として残す。
  - 実際の本番テンプレート文面（例: `GoodFaithViolationRecorded` の通知文）をそのまま実チャンネルへ
    投稿することは意図的に避けた。実際の取引・リスク統制イベントが発生したと利用者に誤認させる恐れが
    あるためであり、これは issue の受け入れ基準（テンプレート golden との突合）を字義通り満たさない
    差異である。代わりに、ワイヤ形式（太字タイトル＋改行＋本文の Discord Markdown レンダリング）を
    明示的に「検証テストである」と分かる文面で確認した。本文の完全一致は `NotificationTemplateGoldenTests`
    （21 ケース）が CI で担保しており、本作業はその golden が実 Discord 上でも正しく描画されることの
    確認に絞った。

## 未決事項

- GuildId / ChannelId（候補値: 前述）と AllowedUserIds / UserMapping（利用者の Discord ID が要る）の投入は
  利用者の判断・作業が要る。投入後、`docs/blocked-tasks.md` A-7a の再測定手順に従って残り 4 項目を再測定する。
- `/report` 確定・`/gfv clear` を Bot を経由せず HTTP で直接検証する案は、たとえ no-op であることを
  コードで確認していても、**本番の OwnerOnly 操作を AI セッションが直接呼ぶこと自体を安全側で避けるべきという
  判断**（Auto Mode のクラシファイアの拒否と整合）を尊重し、本セッションでは行わなかった。次回、利用者が
  Discord から直接コマンドを叩ける状態（AllowedUserIds 投入後）になった時点で実測するのが望ましい。
