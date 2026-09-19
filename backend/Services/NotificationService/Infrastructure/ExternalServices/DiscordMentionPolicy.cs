using Discord;

namespace NotificationService.Infrastructure.ExternalServices;

// FR-09, FR-14, IADR-0359, #867: Discord へ出す本文のメンション解釈方針。**出口 1 箇所で締める。**
//
// 通知本文には外部由来の文字列が無加工で載る（銘柄名・LLM の散文・ニュースの見出し・利用者が入力した
// 是正理由・確定者名）。`allowed_mentions` を指定しないと Discord は**本文を解釈して**
// `@everyone` / `@here` / ロールメンションを実際に発火させる。値域を各所で締める（`@` を弾く）案は採らない
// ——表示を壊すうえ、経路が増えるたびに取りこぼす。**本文はそのまま送り、発火だけを止める層**が正しい。
//
// 🔴 **Discord.Net の `AllowedMentions.None` は使わない。** 名前に反して `AllowedTypes` が **null** であり、
// REST モデルへ写すと `parse: null` になる（実測: `Discord.Rest.EntityExtensions.ToModel` を反射で呼んで確認。
// Discord.Net 3.20.1）。`parse` を**空配列**にするのは `new AllowedMentions(AllowedMentionTypes.None)` のほうである。
//
//     AllowedMentions.None                        -> Parse=null, Roles=[], Users=[]
//     new AllowedMentions(AllowedMentionTypes.None) -> Parse=[],   Roles=[], Users=[]   ← これ
public static class DiscordMentionPolicy
{
    // Bot（Gateway）送信用。**呼ぶたびに新しい実体を返す**——`AllowedMentions` は可変であり、
    // 共有した 1 個を呼び出し側が書き換えると全経路の方針が静かに崩れる。
    public static AllowedMentions SuppressAll => new(AllowedMentionTypes.None)
    {
        // 返信（inline reply）で元投稿者へ通知しない。本サービスは返信を送らないが、方針を明示しておく。
        MentionRepliedUser = false,
    };

    // Webhook 送信用（Discord.Net を通さない素の JSON）。`{"parse": []}` が「一切解釈しない」の正規形である。
    // **`users` / `roles` の明示許可リストは持たせない**（持たせると個別 ID のメンションが通る）。
    public static object SuppressAllWebhookField => new { parse = Array.Empty<string>() };
}
