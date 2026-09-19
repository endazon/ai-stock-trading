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

## 母集合（2026-09-19・`origin/develop` = `a1421b33`）

🔴 **API 名を記憶で列挙しない。ライブラリのアセンブリから反射で導出する**
（`.claude/rules/traceability.repo.md` 規則 9。**初版は記憶で 16 種を列挙し、`CreatePostAsync` /
`CreatePostWithFile(s)Async`〔フォーラム投稿〕・`UpdateAsync`〔`SocketMessageComponent` のボタン応答で
本文を差し替える定番 API〕・`ModifyAsync` / `Respond` を落としていた**。#867 の監査が反射で引き直して
検出した。結論は変わらなかったが、**引き方が記憶依存だった**ので以下の形へ是正する）。

### 手順 1: 送信 API の名前をライブラリから導出する

`Discord.Net.Core` / `Discord.Net.Rest` / `Discord.Net.WebSocket`（いずれも 3.20.1）の公開型を走査し、
**`AllowedMentions` を引数に取る public メソッド**と **`AllowedMentions` を持つ public プロパティ**を列挙する。
「メンションを載せられる API」の定義そのものであり、人の記憶を介さない。実行結果:

```
# AllowedMentions を引数に取る public メソッド名（14 種）
CreatePostAsync            CreatePostWithFileAsync    CreatePostWithFilesAsync
FollowupAsync              FollowupWithFileAsync      FollowupWithFilesAsync
ReplyAsync                 Respond                    RespondAsync
RespondWithFileAsync       RespondWithFilesAsync      SendFileAsync
SendFilesAsync             SendMessageAsync

# AllowedMentions を持つ public プロパティ（1 件。Modify / Update 系の担い手）
MessageProperties.AllowedMentions
```

**この導出は `DeferAsync` / `RespondWithModalAsync` / `RespondWithPremiumRequiredAsync` を自動的に外す**
——それらは `AllowedMentions` を取らない＝メンションを載せる場所が無いからである
（初版は同じ結論を人の判断で書いていた。導出に任せるほうが強い）。

### 手順 2: 導出した名前＋ `MessageProperties` の担い手で全走査する

```
git grep -n --untracked -E "\b(CreatePostAsync|CreatePostWithFileAsync|CreatePostWithFilesAsync|\
FollowupAsync|FollowupWithFileAsync|FollowupWithFilesAsync|ReplyAsync|Respond|RespondAsync|\
RespondWithFileAsync|RespondWithFilesAsync|SendFileAsync|SendFilesAsync|SendMessageAsync|\
ModifyAsync|ModifyOriginalResponseAsync|UpdateAsync|AllowedMentions)\b" -- '*.cs'
```

（`--untracked` が要る。未追跡の新規ファイルを落とすと**自分が今書いたコードが母集合から消える**。
`bin/` `obj/` は git の追跡外なので自動的に除かれる。）

### 手順 3: 素の HTTP で Discord へ投げる箇所

```
git grep -n --untracked -iE "discord.*api|PostAsJsonAsync|PostAsync\(" -- '*.cs'
git grep -il discord -- deploy infra scripts .github
```

| # | 場所 | 本文の出所 | 判定 |
| --- | --- | --- | --- |
| 1 | `DiscordWebhookNotificationSender.SendAsync`（素の HTTP。手順 3） | 全通知（銘柄名・LLM の散文・確定者名） | **直す**（`allowed_mentions: {parse: []}`） |
| 2 | `DiscordNetBotGateway` の `RespondAsync`（本文つき・11 箇所） | 定数文言 ＋ ハンドラの整形済み `Message`（段階ゲートの警告等） | **直す**（ラッパー経由） |
| 3 | 同 `FollowupAsync`（7 箇所） | ハンドラの整形済み `Message`（`ReportResponseTextOf` 等） | **直す**（ラッパー経由） |
| 4 | 同 `ModifyOriginalResponseAsync`（1 箇所。`MessageProperties` の担い手） | 同上（編集でも本文は解釈し直される） | **直す**（ラッパー経由） |
| 5 | 同 `SocketAutocompleteInteraction.RespondAsync(IEnumerable<AutocompleteResult>)`（1 箇所） | 入力補完の候補。**`AllowedMentions` を取らないオーバーロード**であり、メッセージでもない | 対象外 |
| 6 | `OrderExecutionService` の `ModifyAsync`（本番 2・テスト 4） | 注文訂正。**Discord ではない** | 対象外 |
| 7 | `CostControlService` / `OrderExecutionService` のテストの `Respond`（16 箇所） | テストスタブのプロパティ名。**Discord ではない** | 対象外 |
| 8 | `NullDiscordBotGateway` / `LoggingNotificationSender` | 送信しない（安全既定・ログのみ） | 対象外 |
| 9 | `UpdateAsync` / `CreatePostAsync` / `CreatePostWithFile(s)Async` / `SendMessageAsync` / `ReplyAsync` / `SendFile(s)Async` / `RespondWithFile(s)Async` / `FollowupWithFile(s)Async` | **一致 0 件**＝DM・スレッド・フォーラム投稿・ボタン応答の `UpdateAsync`・embed・ファイル添付の経路は**存在しない** | 対象外 |
| 10 | `deploy/` `infra/` `scripts/` `.github/`（手順 3 の 2 本目・12 ファイル） | helm の値・環境変数名・Secret 名・その描画検査。**送信コードではない** | 対象外 |

**意図してメンションを必要としている経路は 1 つも無い**（本文はすべて本人向け ephemeral 応答と運用通知であり、
`@` を付ける文言はコード上に存在しない）。よって**全経路で「何もメンションしない」を既定**にできる。

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

実測（`Discord.Rest.EntityExtensions.ToModel` を反射で呼び、**Discord.Net 自身の `DiscordContractResolver` で
JSON まで写した**。Discord.Net 3.20.1・**実送信なし**）:

```
AllowedMentions.None                           -> {"parse":null,"roles":[],"users":[]}
new AllowedMentions(AllowedMentionTypes.None)  -> {"parse":[],"roles":[],"users":[]}
SuppressAll（出荷形）                          -> {"parse":[],"roles":[],"users":[],"replied_user":false}
AllowedMentions.All（対照）                    -> {"parse":["everyone","roles","users"],"roles":[],"users":[]}
```

この差は**電線に載る形のまま**テストで固定した（`方針は直列化すると_parse_が空配列になる` /
`ライブラリの_AllowedMentions_None_は直列化すると_parse_が_null_になる` /
`対照_AllowedMentions_All_は_everyone_を通す`）。**メモリ上の `AllowedMentions` だけを見る表明では、
Discord.Net が `AllowedMentionTypes.None` の写し方を変えたときに誰も気付けない**（#867 の監査指摘 N4）。
将来ライブラリ側が `AllowedMentions.None` を直しても、本リポジトリは明示形を使い続ける。

**ミューテーション確認（実走）**: `SuppressAll` を `AllowedMentions.None` へ差し戻すと
`DiscordMentionPolicyTests` の 10 本中 **7 本が落ちる**（直列化テストを含む）。表明は空振りしていない。

## 受け入れ基準 → テストの写像

| 受け入れ基準（#867） | テスト |
| --- | --- |
| Webhook の送信本文に `allowed_mentions` が付き `parse` が空 | `DiscordWebhookNotificationSenderTests.Webhook_は_allowed_mentions_の_parse_を空で送る`（電線の文字列 `"allowed_mentions":{"parse":[]}` も固定） |
| `@everyone` を含む値が本文に載っても解釈されない形で送られる | 同 `メンション文字列を含む本文でも_本文は無加工で_parse_は空のまま送られる`（`@everyone` / `@here` / `<@&ロールID>` / `<@ユーザーID>` の 4 形） |
| Bot（Gateway）の応答にも同じ穴が無い | `DiscordMentionPolicyTests` の 4 本（初回応答・ボタンつき・追送・差し替え）。`IDiscordInteraction` のフェイクが引数を捕捉する（**実 Discord へは送らない**） |
| Bot 経路も**電線に載る形**で `parse: []` になる | 同 `方針は直列化すると_parse_が空配列になる` ほか 2 本（`ToModel` ＋ `DiscordContractResolver` で JSON まで写す。#867 監査 N4） |
| 既存の見た目・内容は変わらない | 本文・`ephemeral`・ボタンの引数はラッパーがそのまま渡す（`NotificationService.Tests` 495 件が緑） |

## 検証（実走。`origin/develop` = `a1421b33` へ rebase 後の値）

🔴 **実 Discord へは一切送信していない**（fake `HttpMessageHandler` と fake `IDiscordInteraction`）。

```
$ dotnet build backend/backend.slnx
ビルドに成功しました。 / 0 個の警告 / 0 エラー

$ dotnet test backend/backend.slnx
成功!  合格:  171 - AiStockTrading.Architecture.Tests        成功!  合格:  145 - CostControlService.Tests
成功!  合格:   78 - AiStockTrading.Bff.Endpoints.Tests       成功!  合格:  479 - InformationCollectionService.Tests
成功!  合格:  437 - AiStockTrading.Shared.Contracts.Tests     成功!  合格:  135 - MarketMonitorService.Tests
成功!  合格:  315 - AiStockTrading.Shared.Infrastructure.Tests 成功! 合格:  495 - NotificationService.Tests
成功!  合格:   27 - AiStockTrading.Shared.Kernel.Tests        成功!  合格:  136（skip 4）- OpendAuthGateway.Tests
成功!  合格:   42 - AiStockTrading.Shared.KnowledgeBase.Tests  成功! 合格:  539 - OrderExecutionService.Tests
成功!  合格:   14 - AiStockTrading.TestSupport.Messaging.Tests 成功! 合格: 1014 - ReportService.Tests
成功!  合格:  144 - AiStockTrading.TestSupport.PlatformShim.Tests 成功! 合格: 1764 - RiskManagementService.Tests
成功!  合格:  133 - AuditService.Tests                        成功!  合格:  698 - TradeDecisionService.Tests
成功!  合格:  340 - BacktestService.Tests                     失敗!  失敗: 8、合格: 10 - AiStockTrading.IntegrationTests
成功!  合格:   36 - ConfigurationService.Tests                  ← Docker 不在の既知の 8 件

$ dotnet format backend/backend.slnx --verify-no-changes   → 出力なし・exit 0
$ node scripts/check-*.js                                   → 全件 OK
   （check-backlog-audit-output.js / check-planning-adr-range.js は引数必須のため単体実行では usage。CI が渡す）
$ node scripts/check-adr-index-sync.js --range=origin/develop...HEAD   → OK
$ node scripts/check-commit-messages.js --range=origin/develop...HEAD  → ✓ すべてのコミットが規約に適合
$ node scripts/gen-knowledge-graph.js --check               → OK
$ node --test scripts/scripts.repo.test.js                  → pass 1 / fail 0
```

## 影響範囲

- `backend/Services/NotificationService/Infrastructure/ExternalServices/DiscordMentionPolicy.cs`（新規）
- 同 `DiscordInteractionResponses.cs`（新規）
- 同 `DiscordWebhookNotificationSender.cs`
- 同 `DiscordNetBotGateway.cs`（19 箇所をラッパーへ寄せる）
- `backend/Services/NotificationService/Tests/Infrastructure/ExternalServices/DiscordMentionPolicyTests.cs`（新規）
- 同 `DiscordWebhookNotificationSenderTests.cs`

**`NotificationService/Features/Notifications/NotificationFormatter.cs` は触らない**
（[#866](https://github.com/endazon/ai-stock-trading/issues/866) が並行して編集中。本件は送信側で完結する）。

## 監査（#867）の非ブロッキング指摘への対応

- **N1**（母集合の引き方が記憶依存だった）→ §母集合を**ライブラリから反射で導出する形へ書き直した**。
  引き直しても本番コードの漏れは 0 件で結論は変わらないが、**引き方が誤っていた**ことを経緯として残す。
- **N2**（検証出力が rebase 前の古い値だった）→ §検証を**再実走して貼り直した**
  （`ReportService.Tests` 930 → 1014、`NotificationService.Tests` 479 → 495）。
- **N4**（Bot 経路の「電線に載る形」が未固定だった）→ 直列化テスト 3 本を追加した（§落とし穴）。
- **N3**（19 箇所の置換に直接のテスト網が無い）は**対応しない**。証跡は diff そのものであり、監査が
  全行を目視して忠実な置換であることを確認済み。ラッパーの表明（10 本）が方針の載り方を固定している。

## 残余リスク

- **実 Discord への投稿では検証していない**（#867 の「`allowed_mentions` 未指定の Webhook が `@everyone` を
  実際に通知するかは未検証」は解消していない）。本 PR が固定したのは**電線に載る形**である。
  Discord の API 仕様上 `parse: []` は「一切解釈しない」であり、未指定より狭い。
- **ラッパーは迂回できる**（IADR-0359 決定 4）。新しい送信経路が素の `RespondAsync` を呼べば穴が開く。
