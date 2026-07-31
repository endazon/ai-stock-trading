---
title: AST Discord Webhook URL をアプリログへ出さない（送信専用 HttpClient の既定ログ抑止と宛先秘匿）
type: spec
status: accepted
related_ids: [FR-09, NFR]
author: endazon (with Claude Code)
created: 2026-07-31
updated: 2026-07-31
plan_refs:
  - ../../planning/projects/ai-stock-trading/02_requirements/01_requirements.md
---

# 仕様書: Discord Webhook URL のログ秘匿

> [#289](https://github.com/endazon/ai-stock-trading/issues/289)（security・傘 [#279](https://github.com/endazon/ai-stock-trading/issues/279)）。

## 起点となる計画書・課題（トレーサビリティ）

- 機能要求（FR）: FR-09（通知）／NFR（セキュリティ・秘密情報の取り扱い）
- 関連 IADR: [IADR-0020](../adr/IADR-0020_notification-safe-outbound.md)（通知サービス・Discord Webhook 送信）／
  [IADR-0062](../adr/IADR-0062_discord-bot-gateway-and-authorization.md)（Discord Bot・OwnerAuth の名前付きクライアント）／
  [IADR-0109](../adr/IADR-0109_deploy-secret-preservation.md)（秘密は平文で置かず `secretKeyRef` 経由とする既存方針）／
  本作業で新規 [IADR-0121](../adr/IADR-0121_credential-bearing-uri-log-redaction.md)
- 対象 Issue: [#289](https://github.com/endazon/ai-stock-trading/issues/289)（`Closes #289`）

## 現状（この変更の直前・実コードと実測で確定）

`Notifications:Provider=discord-webhook` で実送信が有効なとき、`DiscordWebhookNotificationSender` の
`PostAsJsonAsync(webhookUrl, …)` に対して `IHttpClientFactory` の**既定ログ**が URL を平文で出力する。
本作業の回帰テスト（`RecordingLoggerProvider` でログ出力を捕捉）を修正前のコードに当てて**実測**した出力：

| # | カテゴリ | レベル | 出力 |
| --- | --- | --- | --- |
| 1 | `System.Net.Http.HttpClient.discord.LogicalHandler` | scope | `HTTP POST https://discord.com/api/webhooks/<id>/<token>` |
| 2 | `System.Net.Http.HttpClient.discord.LogicalHandler` | Information | `Start processing HTTP request POST https://…/<token>` |
| 3 | `System.Net.Http.HttpClient.discord.ClientHandler` | Information | `Sending HTTP request POST https://…/<token>` |

Serilog は OTLP で otel collector へ送るため（`ConfigureAiStockTradingSerilog`）、この 3 行は **Loki に蓄積**される。
Discord Webhook URL は「知っていれば認証なしで当該チャンネルへ投稿できる」＝**それ自体が資格情報**であり、
`ast-secrets` へ平文で置かない・`secretKeyRef` 経由にするという既存の秘匿方針が**ログ側で無効化されていた**。

### issue 記載との差異（実測による訂正）

issue は根因を `Program.cs:33` の無名 `AddHttpClient()`（カテゴリ `…HttpClient.Default.*`）としていたが、
実際の送信は `CreateClient("discord")` で解決されるため、**カテゴリは `…HttpClient.discord.*`** である。
`IHttpClientFactory` は未登録の名前でもクライアントを生成し、ログ名にはその名前を使う。
無名 `AddHttpClient()` は `IHttpClientFactory` を登録していただけで、**Webhook 送信には使われていない**。
「送信は名前付きクライアントで行われているが、既定ログが有効なので URL が出る」が正しい理解であり、
対策の要点は「名前付きにすること」ではなく「そのクライアントの既定ログを外すこと」にある。

## 目的

1. Webhook URL がアプリログに平文で現れない（scope・メッセージのいずれにも）。
2. 送信の成否・HTTP ステータス・所要時間は残す（障害切り分けを落とさない）。
3. 抑止は Webhook 送信クライアントに閉じる（他の名前付きクライアントの可観測性を落とさない）。
4. Webhook 未設定（`Notifications__Discord__WebhookUrl` 空）の既定動作は不変（no-op・送信しない）。

## 設計

### 1. 送信専用の名前付きクライアントを構成点として切り出す

`DiscordWebhookHttpClientExtensions.AddDiscordWebhookHttpClient()`（`ClientName = "discord"`）を新設し、
`Program.cs` の `AddHttpClient()` ＋ `CreateClient("discord")` をこれに置き換える。
クライアント名は文字列リテラルの散在をやめて定数化する（登録側と解決側の取り違えを型で防ぐ）。

### 2. 既定ログを外し、宛先を伏せた logger に差し替える

```csharp
services.AddHttpClient(ClientName)
    .RemoveAllLoggers()
    .AddLogger(sp => new RedactedUriHttpClientLogger(...));
```

- `RemoveAllLoggers()` … `LoggingScopeHttpMessageHandler` / `LoggingHttpMessageHandler` を外す。
  上表の 3 行（scope を含む）が消える。
- `RedactedUriHttpClientLogger`（`IHttpClientLogger`） … 開始を Debug、応答を Information（ステータス・所要時間）、
  失敗を Warning（例外つき）で記録する。URI は `Redact()` で **スキーム＋ホストのみ**に落とし、
  パスとクエリは一律 `/***` で伏せる（Discord Webhook はトークンが**パス**に載るため、部分開示もしない）。

### 3. 採らなかった案

| 案 | 却下理由 |
| --- | --- |
| ログカテゴリ `System.Net.Http.HttpClient.discord.*` を `None` に落とす（appsettings） | 構成で無効化できる＝**構成を変えれば再発する**。秘匿がコードではなく設定の正しさに依存する。カテゴリ名は名前付きクライアント名に連動するため、名前を変えた瞬間に無言で失効する |
| `IHttpClientFactory` の既定ログ全体を `None` | 副作用が広い。risk-kill-switch / risk-pause / risk-stage-gate / discord-owner-token の可観測性まで落ちる |
| URL のクエリのみマスク | Discord Webhook のトークンは**クエリではなくパス**に載る。クエリだけ伏せても漏洩は止まらない |
| 送信ログを一切出さない | 「障害切り分けができなくなる対策は採らない」（issue 明記） |

## 受け入れ基準とテスト（`DiscordWebhookHttpClientTests`）

| # | 受け入れ基準 | テスト |
| --- | --- | --- |
| 1 | Webhook URL が平文で現れない | `Webhook_送信のログに_URL_が平文で現れない`（URL 全体・トークン片の双方を否定） |
| 2 | 送信成否のログは残る | 同上（`https://discord.com/***` と `204` を**肯定**で固定。空振り検出を兼ねる）／`送信失敗時はステータスがログに残る`（`429`） |
| 3 | 応答が返らない失敗（接続拒否・DNS 失敗・タイムアウト）でも URL が出ない | `送信が例外で失敗しても_URL_は現れず失敗が記録される`（`IHttpClientLogger.LogRequestFailed` 経路。`HTTP 送信失敗` と伏せた宛先を**肯定**で固定） |
| 4 | 他クライアントの挙動不変 | `他の名前付きクライアントの既定リクエストログは変わらない`（`risk-kill-switch` の URI がログに**出ること**を固定） |
| 5 | Webhook 未設定時の no-op 不変 | 既存 `NotificationSenderFactoryTests`（変更なし・そのまま緑） |

テストは本番と同じ登録（`AddDiscordWebhookHttpClient()`）を使い、**一次ハンドラだけ**を差し替える
（`ConfigurePrimaryHttpMessageHandler`）。ログ経路は本番と同一のまま検証するため、
「テスト用に別配線を組んだので本番は守られていない」が起こらない。

Loki 側（受け入れ基準の 2 点目）は、アプリが出力するログそのものを捕捉して検証している。
Serilog → OTLP → collector → Loki は出力された内容をそのまま運ぶため、出力に無いものは蓄積され得ない。

## 影響範囲

| 面 | 差分 |
| --- | --- |
| `Shared.Contracts` / イベント | なし |
| DB / Migration | なし |
| Helm / values / compose / 環境変数 | なし（構成キーを増やさない） |
| 実弾ゲート（閂 0〜4）・SIMULATE | 差分ゼロ（notification-service に閉じる） |
| 他サービス | なし |

## 残存リスク（本 PR の対象外・実測済み）

`ObservabilityExtensions` は `AddHttpClientInstrumentation()` を登録しており、.NET の HTTP クライアント
アクティビティは `url.full` タグに**フル URL（パスを含む）**を載せる。ローカル HTTP リスナへの実送信で実測した：

```
System.Net.Http|POST|url.full=http://localhost:18289/api/webhooks/wh-test-id/wh-test-token
```

つまり**トレース（Tempo）側には Webhook URL が残る**。これはログ経路（Loki）とは別の出口であり、
対策は共有 shim（全 11 サービスのトレース計装）に手を入れることになるため本 issue のスコープ外とし、
追跡用に [#313](https://github.com/endazon/ai-stock-trading/issues/313) を起票して扱う。#289 の受け入れ基準はいずれもログを対象としており、本 PR で充足する。
