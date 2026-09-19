using Discord;

namespace NotificationService.Infrastructure.ExternalServices;

// FR-09, FR-14, IADR-0359, #867: Bot（Gateway）が**本文を伴って**応答する唯一の出口。
//
// Discord.Net の `RespondAsync` / `FollowupAsync` / `ModifyOriginalResponseAsync` を直接呼ぶと
// `allowedMentions` の既定は **null＝本文を解釈して発火させる**。既定を安全側へ倒すため、本文を出す応答は
// すべて本クラス経由にし、`DiscordMentionPolicy.SuppressAll` を**必ず**載せる。
//
// 🔴 **迂回できる**（IADR-0359 決定 4）。`SocketInteraction` の素の `RespondAsync` は依然として呼べるため、
// 本ラッパーは「規律 + 既定の安全化」であって強制ではない。機械検査は今回置かない（同型の事故が 2 回
// 起きたら検査器、という運用標準に従う。1 回目は本 IADR に記録する）。
//
// 本文を持たない応答は対象外である（メンションを載せる場所が無い）:
//   - `DeferAsync` …… 応答の予約のみ
//   - `RespondWithModalAsync` …… モーダルのラベルは本コードの定数のみ
//   - `SocketAutocompleteInteraction.RespondAsync(IEnumerable<AutocompleteResult>)` …… 入力補完の候補
//     （メッセージではない。候補の文字列は Discord のメンション解釈を経ない）
public static class DiscordInteractionResponses
{
    // 初回応答（本文つき）。ephemeral は既定 true（本サービスの統制操作はすべて本人にだけ見せる）。
    public static Task RespondTextAsync(
        this IDiscordInteraction interaction,
        string text,
        MessageComponent? components = null,
        bool ephemeral = true)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        return interaction.RespondAsync(
            text,
            ephemeral: ephemeral,
            allowedMentions: DiscordMentionPolicy.SuppressAll,
            components: components);
    }

    // `DeferAsync` 後の追送（本文つき）。
    public static Task FollowupTextAsync(
        this IDiscordInteraction interaction,
        string text,
        MessageComponent? components = null,
        bool ephemeral = true)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        return interaction.FollowupAsync(
            text,
            ephemeral: ephemeral,
            allowedMentions: DiscordMentionPolicy.SuppressAll,
            components: components);
    }

    // 送信済み応答の差し替え（本文つき）。編集でも Discord は本文を解釈し直すため、同じ方針を載せる。
    public static Task ReplaceOriginalResponseAsync(
        this IDiscordInteraction interaction,
        string text,
        MessageComponent components)
    {
        ArgumentNullException.ThrowIfNull(interaction);
        return interaction.ModifyOriginalResponseAsync(m =>
        {
            m.Content = text;
            m.Components = components;
            m.AllowedMentions = DiscordMentionPolicy.SuppressAll;
        });
    }
}
