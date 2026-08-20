---
title: IADR-0121 URI 自体が資格情報である送信先は、専用の名前付き HttpClient で既定ログを外し宛先を伏せる
type: impl-adr
status: Accepted
related_ids: [FR-09, NFR]
author: endazon (with Claude Code)
created: 2026-07-31
updated: 2026-07-31
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
---

# IADR-0121: 資格情報を含む URI のログ秘匿は「専用クライアント＋既定ログ除去」で行う

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。

- 状態: Accepted
- 日付: 2026-07-31
- 決定者: endazon（利用者・[#289](https://github.com/endazon/ai-stock-trading/issues/289) 起票）/ Claude Code（起案）

## 起点・関連

- 関連する計画書 ID: FR-09（通知）、NFR（セキュリティ）
- 対象 Issue: [#289](https://github.com/endazon/ai-stock-trading/issues/289)（傘 [#279](https://github.com/endazon/ai-stock-trading/issues/279)）
- 関連する実装仕様書: [20260731_289_webhook-url-log-redaction](../specs/20260731_289_webhook-url-log-redaction.md)
- 関連 IADR: [IADR-0020](IADR-0020_notification-safe-outbound.md)（通知サービス・Discord Webhook 送信）、
  [IADR-0062](IADR-0062_discord-bot-gateway-and-authorization.md)（Discord Bot・名前付きクライアントと OwnerAuth）、
  [IADR-0109](IADR-0109_deploy-secret-preservation.md)（秘密を平文で置かない既存方針）、
  [IADR-0068](IADR-0068_live-quote-feed-finnhub-extraction.md)（Finnhub の API キーを URL クエリではなくヘッダーで渡す＝同種の判断）

## 背景・課題

Discord Webhook URL は「知っていれば認証なしで当該チャンネルへ投稿できる」＝**それ自体が資格情報**である
（失効手段は Webhook の再発行のみ）。`ast-secrets` に平文で置かず `secretKeyRef` 経由にするという秘匿方針は
デプロイ面では守られていたが、**ログ面で無効化されていた**。

`IHttpClientFactory` の既定ログはリクエスト URI を平文で出す。修正前のコードに本 PR の回帰テストを当てて実測すると、
1 回の送信で 3 箇所に URL 全体（パス末尾のトークンを含む）が出力される。

| カテゴリ | 種別 | 出力 |
| --- | --- | --- |
| `System.Net.Http.HttpClient.discord.LogicalHandler` | scope | `HTTP POST <webhook url>` |
| `System.Net.Http.HttpClient.discord.LogicalHandler` | Information | `Start processing HTTP request POST <webhook url>` |
| `System.Net.Http.HttpClient.discord.ClientHandler` | Information | `Sending HTTP request POST <webhook url>` |

Serilog は OTLP で collector へ送るため、これらは **Loki に蓄積**され保持期間中は検索可能になる。

なお issue はカテゴリを `…HttpClient.Default.*`（無名 `AddHttpClient()` が根因）としていたが、実測では
`…HttpClient.discord.*` である。`CreateClient("discord")` は**未登録の名前でもクライアントを生成し、その名前を
ログ名に使う**ため、送信は既に名前付きで行われていた。すなわち**「名前付きにする」ことは対策ではない**——
既定ログを外して初めて漏洩が止まる。この差は対策の選択に直結するため記録しておく。

## 決定

1. **Webhook 送信専用の登録点を切り出す**。`DiscordWebhookHttpClientExtensions.AddDiscordWebhookHttpClient()`
   （`ClientName = "discord"`）に集約し、`Program.cs` からは名前リテラルを消す。
2. **そのクライアントの既定ログを `RemoveAllLoggers()` で除去する**。scope（`HTTP POST <uri>`）も同時に消える。
3. **`RedactedUriHttpClientLogger`（`IHttpClientLogger`）に差し替える**。開始を Debug、応答を Information
   （HTTP ステータス・所要時間）、失敗を Warning（例外つき）で記録し、URI は **スキーム＋ホストのみ**に落として
   パス・クエリを一律 `/***` で伏せる。**目的は「URL を出さないこと」であって「ログを消すこと」ではない**。
4. **抑止は当該クライアントに閉じる**。`risk-kill-switch` / `risk-pause` / `risk-stage-gate` / `discord-owner-token`
   の既定ログは変更しない。
5. 部分開示（トークンだけマスクして ID は出す等）は**しない**。パスの構造は送信先の実装に依存し、
   「どこまでが秘密か」をアプリ側が正しく知り続けられる保証がないため、ホストより先は一律に伏せる。

## 根拠

- **構成ではなくコードで秘匿する**。`Logging:LogLevel` でカテゴリを `None` にする案は、構成を戻せば再発する
  （秘匿がデプロイ設定の正しさに依存する）。さらにカテゴリ名は名前付きクライアント名に連動するため、
  クライアント名を変えた瞬間に**無言で失効**する。`RemoveAllLoggers()` は登録と同じ場所にあり、
  クライアント名を変えても効き続ける。
- **既定ログ全体の無効化は副作用が広い**。他の 4 クライアントの可観測性まで落ちる。
- **クエリのみのマスクでは足りない**。Discord Webhook のトークンは**パス**（`/api/webhooks/<id>/<token>`）に載る。
  クエリ限定のスクラブ（OTel の既定 query redaction を含む）は本件に効かない。
- **観測性は落とさない**。issue は「障害切り分けができなくなる対策は採らない」と明記している。
  ステータス・所要時間・宛先ホストは残り、`DiscordWebhookNotificationSender` 自身の失敗ログ
  （`Discord Webhook 送信失敗: {Status}`）も従来どおり出る。

## 影響

- `Shared.Contracts`・イベント・DB/Migration・Helm/values/compose・環境変数はいずれも**不変**（構成キーを増やさない）。
- 実弾ゲート（閂 0〜4）・SIMULATE 既定は**差分ゼロ**（変更は notification-service に閉じる）。
- Webhook 未設定時の no-op も不変。
- 運用上の可視変化は 1 点のみ: Webhook 送信のログ行が
  `Sending HTTP request POST https://discord.com/api/webhooks/…` から
  `HTTP 応答: 204 POST https://discord.com/*** （…ms）` へ変わる。

## 残存リスク

`ObservabilityExtensions` の `AddHttpClientInstrumentation()` により、HTTP クライアントアクティビティの
`url.full` タグには**フル URL（パス込み）**が載る（ローカル実送信で実測）。したがって **トレース（Tempo）側には
Webhook URL が残る**。本 ADR はログ経路（Loki）に対する決定であり、トレース側の対策は共有 shim の計装
（全サービス共通）に手を入れるため [#313](https://github.com/endazon/ai-stock-trading/issues/313) で別途扱う。

## 代替案（棄却）

| 案 | 棄却理由 |
| --- | --- |
| 構成で `System.Net.Http.HttpClient.discord.*` を `None` | 構成依存＝再発可能・クライアント名変更で無言失効 |
| `IHttpClientFactory` の既定ログを全体 `None` | 他 4 クライアントの可観測性を巻き添えにする |
| クエリのみスクラブ | トークンはパスにあるため無効 |
| 送信ログを出さない（logger 差し替えなし） | 障害切り分けができなくなる（issue が明示的に禁止） |
| `HttpClient` を包む DelegatingHandler で URI を書き換える | 実リクエストの宛先を変えることになり送信自体が壊れる |
