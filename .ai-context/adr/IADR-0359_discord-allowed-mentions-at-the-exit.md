---
title: IADR-0359 Discord への送信は出口 1 箇所でメンションを一切解釈させない（本文はサニタイズしない・Bot の本文つき応答はラッパーへ寄せる）
type: impl-adr
status: Accepted
related_ids: [FR-09, FR-14, UC-06, ADR-0009, ADR-0028, IADR-0020, IADR-0062, IADR-0097, IADR-0240]
author: endazon (with Claude Code)
created: 2026-09-19
updated: 2026-09-19
plan_refs:
  - planning:projects/ai-stock-trading/02_requirements/01_requirements.md
related_specs:
  - ../specs/20260919_867_discord-allowed-mentions.md
---

# IADR-0359: Discord 送信の `allowed_mentions` を出口 1 箇所で締める

> 実装リポジトリ内の意思決定記録（Implementation ADR）。1 ファイル = 1 意思決定。
> 計画リポジトリの ADR（`ADR-XXXX`）とは別系統（`IADR-XXXX`）とし、実装に閉じた決定を記録する。
> 計画に影響する決定は planning へ issue で環流する（`feedback.yml` テンプレート）。

- 状態: Accepted
- 日付: 2026-09-19
- 決定者: endazon（[#867](https://github.com/endazon/ai-stock-trading/issues/867)）/ Claude Code（起案）

## 起点・関連

- [#867](https://github.com/endazon/ai-stock-trading/issues/867)（[#861](https://github.com/endazon/ai-stock-trading/issues/861) の監査が実測）。
  Webhook 送信は `new { content }` だけを送り `allowed_mentions` を指定していなかった。
- 通知本文に無加工で載る外部由来の値: 銘柄名・LLM の散文・ニュースの見出し・GFV 解除の是正理由
  （利用者が自由記述する。計画 ADR-0028 決定 2（[#464](https://github.com/endazon/ai-stock-trading/issues/464)））・
  確定者名 `onBehalfOf`（値域がメール形式のため `@` を許す。[IADR-0240](IADR-0240_discord-report-review-window-and-idempotent-confirm.md)）。
- 作業仕様書: `.ai-context/specs/20260919_867_discord-allowed-mentions.md`（母集合・実測値）。

## コンテキスト

Discord は既定で**本文を解釈して**メンションを発火させる。`allowed_mentions` を省いた POST に
`@everyone` が含まれていれば、サーバー全員へ通知が飛び得る。送信経路は 2 系統ある:

| 系統 | 実装 | 既定 |
| --- | --- | --- |
| Webhook（縮退用・[IADR-0020](IADR-0020_notification-safe-outbound.md)） | `DiscordWebhookNotificationSender`（素の HTTP） | `allowed_mentions` なし＝**全解釈** |
| Bot（Gateway・[IADR-0062](IADR-0062_discord-bot-gateway-and-authorization.md)） | `DiscordNetBotGateway`（Discord.Net） | `allowedMentions` 引数 null＝**全解釈** |

**権限昇格ではない**（#867 の評価どおり。値を送れるのは owner の secret を持つ者に限られる）。
多層防御としての是正である。

## 決定

### 決定 1: 方針は 1 つの型（`DiscordMentionPolicy`）が持ち、2 系統へ同じ方針を配る

`Infrastructure/ExternalServices/DiscordMentionPolicy.cs` が、Bot 用（`AllowedMentions` オブジェクト）と
Webhook 用（素の JSON フィールド `{"parse": []}`）の**両方の形**を公開する。方針を変えるときに直す場所を 1 つにする。

**明示の許可リスト（`users` / `roles`）は持たせない。** 持たせると個別 ID のメンションが通る。
**意図してメンションを必要としている経路は現時点で 1 つも無い**ため、全経路で「何もメンションしない」を
既定にできる。将来そういう経路ができたら、**その経路だけ**が方針を上書きする。

🔴 **この「1 つも無い」の根拠は、API 名を記憶で列挙した走査ではない。** 起案時の初版は記憶で 16 種を
並べ、`CreatePostAsync` / `CreatePostWithFile(s)Async`（フォーラム投稿）・`UpdateAsync`
（`SocketMessageComponent` のボタン応答で本文を差し替える定番 API）・`ModifyAsync` / `Respond` を
**落としていた**（#867 の監査が検出）。母集合は
**`Discord.Net.Core` / `Rest` / `WebSocket` の公開型から「`AllowedMentions` を引数に取る public メソッド」
＝ 14 種と「`AllowedMentions` を持つ public プロパティ」＝ `MessageProperties.AllowedMentions` を
反射で導出して**引き直した（`traceability.repo.md` 規則 9）。**引き直しても本番コードの漏れは 0 件で
結論は変わらなかった**が、**引き方が記憶依存だった**ことを是正として記録する。手順と出力は仕様書 §母集合。

この導出は `DeferAsync` / `RespondWithModalAsync` / `RespondWithPremiumRequiredAsync` を**自動的に外す**
——`AllowedMentions` を取らない＝メンションを載せる場所が無いからである（初版は同じ結論を人の判断で
書いていた。導出に任せるほうが強い）。

### 決定 2: 🔴 Discord.Net の `AllowedMentions.None` は使わない

**名前に反して `AllowedTypes` が null** であり、REST モデルへ写すと `parse: null` になる。
`parse` が**空配列**になるのは `new AllowedMentions(AllowedMentionTypes.None)` のほうである。
実測（`Discord.Rest.EntityExtensions.ToModel` を反射で呼び、**Discord.Net 自身の `DiscordContractResolver`
で JSON まで写した**。Discord.Net 3.20.1・**実送信なし**）:

```
AllowedMentions.None                           -> {"parse":null,"roles":[],"users":[]}   ← 空配列ではない
new AllowedMentions(AllowedMentionTypes.None)  -> {"parse":[],"roles":[],"users":[]}     ← 正規形
SuppressAll（出荷形）                          -> {"parse":[],"roles":[],"users":[],"replied_user":false}
AllowedMentions.All（対照）                    -> {"parse":["everyone","roles","users"],"roles":[],"users":[]}
```

方針オブジェクトは**呼ぶたびに新しい実体を返す**（`AllowedMentions` は可変であり、共有した 1 個を
呼び出し側が書き換えると全経路の方針が静かに崩れる）。

**固定は「電線に載る形」で行う**（#867 の監査指摘 N4）。メモリ上の `AllowedMentions`
（`AllowedTypes == None`）だけを見る表明では、**Discord.Net が `AllowedMentionTypes.None` の写し方を
変えたときに誰も気付けない**。テストは上と同じ経路（`ToModel` ＋ `DiscordContractResolver`）で
JSON まで写して文字列で固定する。ライブラリ側の内部構造が変わればそこで落ちる——それが狙いである。
Webhook 側は素の JSON を組んでいるため、送信要求の本文を直接固定している
（`"allowed_mentions":{"parse":[]}`）。

### 決定 3: Bot の**本文つき**応答はラッパー（`DiscordInteractionResponses`）経由に寄せる

`RespondTextAsync` / `FollowupTextAsync` / `ReplaceOriginalResponseAsync` の 3 つだけを出口にし、
方針を**必ず**載せる。19 箇所（Respond 11・Followup 7・Modify 1）を寄せた。
19 箇所へ `allowedMentions:` を一つずつ書き足す案は採らない——**次の 1 箇所で忘れる形**である。

本文を持たない応答は対象外とする（メンションを載せる場所が無い）:
`DeferAsync`（8 箇所・応答の予約）／`RespondWithModalAsync`（3 箇所・ラベルは本コードの定数）／
`SocketAutocompleteInteraction.RespondAsync(IEnumerable<AutocompleteResult>)`（1 箇所・候補であってメッセージではない）。

### 決定 4: 機械検査は置かない。ただし**ラッパーは迂回できる**ことを残余リスクとして記録する

運用標準は「**同型の事故が 2 回起きたら検査器**」であり、**本件は 1 回目**である。設計（既定の安全化）で閉じ、
記録に留める。ただし C# の拡張メソッドは強制ではなく、**新しい経路が `SocketInteraction` の素の
`RespondAsync` を呼べば穴が開く**。テストのフェイク `IDiscordInteraction` は**ラッパーが通す 3 つ以外の
送信 API を `NotSupportedException` で落とす**が、これは**フェイクを使うテストの中でしか効かない**
——本番コードの迂回は止められない。2 回目が起きたら `scripts/check-*.js` に走査器を足す
（`RespondAsync|FollowupAsync|ModifyOriginalResponseAsync` の素の呼び出しを `DiscordInteractionResponses.cs`
以外で禁止する形）。

### 決定 5: 文字列のサニタイズ（`@` の置換・ゼロ幅空白の挿入）はしない

表示を壊す（利用者名 `a@b.example` が読めなくなる）うえ、経路が増えるたびに取りこぼす。
`<@&ロールID>` のような構文形は `@` の置換では落とせない。**本文は無加工のまま送り、発火だけを止める層
（`allowed_mentions`）が正しい。** 値域（`onBehalfOf` 等）は従来どおり各所の目的に沿って決め、
本件のために狭めない。

## 影響

- 既存の通知・応答の**見た目と内容は変わらない**（本文・`ephemeral`・ボタンはそのまま渡す）。
  変わるのは「Discord がメンションとして発火させるか」だけである。
- `NotificationService` の 495 件のテストが緑（`backend.slnx` 全体でも Docker 依存の
  `AiStockTrading.IntegrationTests` 8 件を除き緑）。
- **ミューテーション確認**: `SuppressAll` を `AllowedMentions.None` へ差し戻すと
  `DiscordMentionPolicyTests` 10 本中 7 本が落ちる（直列化テストを含む）。表明は空振りしていない。

## 残余リスク

- **実 Discord への投稿では検証していない。** 固定したのは電線に載る形（`"allowed_mentions":{"parse":[]}`）である。
  #867 が「未検証」と記した「未指定の Webhook が実際に `@everyone` を通知するか」は**依然として未検証**だが、
  `parse: []` は未指定より狭く、狭める方向にしか効かない。
- **ラッパーの迂回**（決定 4）。
- 決定 1 の「意図したメンションが必要な経路は無い」は**現時点の走査結果**である。運用上「重大事象は
  `@here` で叩き起こす」という要求が出たら、その経路だけが方針を上書きする新しい決定が要る。
