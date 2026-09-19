---
title: Discord への送信を allowed_mentions で締める（出口 1 箇所でメンション注入を止める）
type: spec
status: accepted
related_ids: [FR-09, FR-14, UC-06, IADR-0020, IADR-0062, IADR-0240, IADR-0359]
author: endazon (with Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md (FR-09「重要事象の通知」/ FR-14「Discord からの操作」)
---

# 仕様書: Discord 送信の `allowed_mentions` を明示する（#867）

## 起点

- [#867](https://github.com/endazon/ai-stock-trading/issues/867)（[#861](https://github.com/endazon/ai-stock-trading/issues/861) の監査が実測）。
- Discord Webhook 送信（`DiscordWebhookNotificationSender`）は `new { content }` だけを送り、
  **`allowed_mentions` を指定していない**。通知本文には外部由来の文字列が無加工で載る
  （銘柄名・LLM の散文・ニュースの見出し・利用者が入力した是正理由・確定者名 `onBehalfOf`）。
  `onBehalfOf` の値域 `\A[A-Za-z0-9._@+-]{1,64}\z` はメール形式の利用者名のため `@` を許しており、
  **`@everyone` / `@here` が本文へそのまま載る**。
- **権限昇格ではない**（#867 の評価）。この値を送れるのは owner クライアントの secret を持つ者だけである。
  多層防御として、**値域を各所で締めるのではなく出口 1 箇所で締める**。

## 母集合（着手前に自分で引いた。2026-09-19・`origin/develop` = `c53876d4`）

引き方（追跡下の全ファイル。`bin/` `obj/` は git の追跡外）:

```
git grep -nE "\b(SendMessageAsync|SendFileAsync|SendFilesAsync|RespondAsync|RespondWithFileAsync|\
RespondWithFilesAsync|RespondWithModalAsync|RespondWithPremiumRequiredAsync|FollowupAsync|\
FollowupWithFileAsync|FollowupWithFilesAsync|ModifyOriginalResponseAsync|ModifyMessageAsync|\
ReplyAsync|ExecuteWebhookAsync|CreateDMChannelAsync|CreateThreadAsync)\b"
```

**一致は 23 箇所で、すべて `DiscordNetBotGateway.cs` にある**（他ファイルは 0 件。上記パターンに含めていない
`DeferAsync`（本文を持たない）が別に 8 箇所ある。
`OrderExecutionService` / `ReportService` の `ModifyAsync` は注文訂正であり Discord ではない＝母集合から除く）。
素の HTTP で Discord へ POST する箇所は `DiscordWebhookNotificationSender.cs` の 1 箇所だけである
（`git grep -i "discord.*api\|PostAsJsonAsync"`。他のヒットはテストの URL 定数と秘匿化処理のテスト）。

| # | 場所 | 本文の出所 | 判定 |
| --- | --- | --- | --- |
| 1 | `DiscordWebhookNotificationSender.SendAsync`（素の HTTP） | 全通知（銘柄名・LLM の散文・確定者名） | **直す**（`allowed_mentions: {parse: []}`） |
| 2 | `DiscordNetBotGateway` の `RespondAsync`（本文つき・11 箇所） | 定数文言 ＋ ハンドラの整形済み `Message`（段階ゲートの警告等） | **直す**（ラッパー経由） |
| 3 | 同 `FollowupAsync`（7 箇所） | ハンドラの整形済み `Message`（`ReportResponseTextOf` 等） | **直す**（ラッパー経由） |
| 4 | 同 `ModifyOriginalResponseAsync`（1 箇所） | 同上（編集でも本文は解釈し直される） | **直す**（ラッパー経由） |
| 5 | 同 `DeferAsync`（8 箇所） | 本文なし（応答の予約） | 対象外 |
| 6 | 同 `RespondWithModalAsync`（3 箇所） | モーダルのラベルは本コードの定数のみ | 対象外 |
| 7 | 同 `SocketAutocompleteInteraction.RespondAsync(IEnumerable<AutocompleteResult>)`（1 箇所） | 入力補完の候補。**メッセージではない**（Discord のメンション解釈を経ない） | 対象外 |
| 8 | `NullDiscordBotGateway` / `LoggingNotificationSender` | 送信しない（安全既定・ログのみ） | 対象外 |
| 9 | Bot の DM・スレッド・埋め込み（embed）・ファイル添付 | **経路が存在しない**（`SendMessageAsync` / `CreateDMChannelAsync` / `CreateThreadAsync` / `*WithFileAsync` は 0 件） | 対象外 |

**意図してメンションを必要としている経路は 1 つも無い**（本文はすべて本人向け ephemeral 応答と運用通知であり、
`@` を付ける文言はコード上に存在しない）。よって**全経路で「何もメンションしない」を既定**にできる。

`deploy/` `infra/` `scripts/` `.github/` にも Discord へ投稿する処理は無い
（`git grep -il discord -- deploy infra scripts .github` の 12 ファイルはいずれも helm の値・環境変数名・
Secret 名・その描画検査であり、送信コードではない）。

## 決定（詳細は [IADR-0359](../adr/IADR-0359_discord-allowed-mentions-at-the-exit.md)）

1. **方針を 1 箇所に置く**（`DiscordMentionPolicy`）。Webhook 用（素の JSON）と Bot 用（`AllowedMentions`）の
   2 つの形を同じファイルが持つ。
2. **Bot の本文つき応答はラッパー（`DiscordInteractionResponses`）経由に寄せる**。既定で方針が載る。
3. **文字列のサニタイズ（`@` の置換）はしない。** 表示を壊すし取りこぼす。本文は無加工のまま、発火だけを止める。
4. **機械検査は置かない**（運用標準「同型の事故が 2 回起きたら検査器」の 1 回目）。ラッパーは迂回可能であり、
   その残余リスクを IADR へ記録する。

## 🔴 実装上の落とし穴（実測で判明）

**Discord.Net の `AllowedMentions.None` は使えない。** 名前に反して `AllowedTypes` が **null** であり、
REST モデルへ写すと `parse: null` になる。空配列 `parse: []` になるのは
`new AllowedMentions(AllowedMentionTypes.None)` のほうである。

実測（`Discord.Rest.EntityExtensions.ToModel` を反射で呼んだ。Discord.Net 3.20.1・実送信なし）:

```
--- AllowedMentions.None -> Discord.API.AllowedMentions
    Parse: IsSpecified=True Value=null
    Roles: IsSpecified=True Value=[]
    Users: IsSpecified=True Value=[]
--- new AllowedMentions(AllowedMentionTypes.None) -> Discord.API.AllowedMentions
    Parse: IsSpecified=True Value=[]      ← これが「一切解釈しない」の正規形
    Roles: IsSpecified=True Value=[]
    Users: IsSpecified=True Value=[]
```

この差は**テストで固定した**（`ライブラリの_AllowedMentions_None_は_AllowedTypes_が_null_であり使わない`）。
将来ライブラリ側が直っても本リポジトリは明示形を使い続ける。

## 受け入れ基準 → テストの写像

| 受け入れ基準（#867） | テスト |
| --- | --- |
| Webhook の送信本文に `allowed_mentions` が付き `parse` が空 | `DiscordWebhookNotificationSenderTests.Webhook_は_allowed_mentions_の_parse_を空で送る`（電線の文字列 `"allowed_mentions":{"parse":[]}` も固定） |
| `@everyone` を含む値が本文に載っても解釈されない形で送られる | 同 `メンション文字列を含む本文でも_本文は無加工で_parse_は空のまま送られる`（`@everyone` / `@here` / `<@&ロールID>` / `<@ユーザーID>` の 4 形） |
| Bot（Gateway）の応答にも同じ穴が無い | `DiscordMentionPolicyTests` の 4 本（初回応答・ボタンつき・追送・差し替え）。`IDiscordInteraction` のフェイクが引数を捕捉する（**実 Discord へは送らない**） |
| 既存の見た目・内容は変わらない | 本文・`ephemeral`・ボタンの引数はラッパーがそのまま渡す（`NotificationService.Tests` 479 件が緑） |

## 影響範囲

- `backend/Services/NotificationService/Infrastructure/ExternalServices/DiscordMentionPolicy.cs`（新規）
- 同 `DiscordInteractionResponses.cs`（新規）
- 同 `DiscordWebhookNotificationSender.cs`
- 同 `DiscordNetBotGateway.cs`（19 箇所をラッパーへ寄せる）
- `backend/Services/NotificationService/Tests/Infrastructure/ExternalServices/DiscordMentionPolicyTests.cs`（新規）
- 同 `DiscordWebhookNotificationSenderTests.cs`

**`NotificationService/Features/Notifications/NotificationFormatter.cs` は触らない**
（[#866](https://github.com/endazon/ai-stock-trading/issues/866) が並行して編集中。本件は送信側で完結する）。

## 残余リスク

- **実 Discord への投稿では検証していない**（#867 の「`allowed_mentions` 未指定の Webhook が `@everyone` を
  実際に通知するかは未検証」は解消していない）。本 PR が固定したのは**電線に載る形**である。
  Discord の API 仕様上 `parse: []` は「一切解釈しない」であり、未指定より狭い。
- **ラッパーは迂回できる**（IADR-0359 決定 4）。新しい送信経路が素の `RespondAsync` を呼べば穴が開く。
