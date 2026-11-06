using Discord;
using Microsoft.Extensions.Logging;

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

    // NFR-06, #1552: ボタンから開いた確認の画面の送信の後、元のメッセージを結果で差し替える（ボタンを取り除く）。
    // 🔴 結果だけは必ず届ける: 差し替えが Discord に拒まれた（HttpException）・時間切れ（TimeoutException）・
    // 要求していないキャンセル（TaskCanceledException。Discord.Net の内部の時間切れ）なら、結果を追送する。実 Discord での確認は未了（#570）。
    // 要求されたキャンセル・その他の例外はそのまま上げる（握り潰さない）。
    internal static async Task ReplaceOriginalOrFollowupAsync(Func<Task> replace, Func<Task> followup, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(replace);
        ArgumentNullException.ThrowIfNull(followup);
        ArgumentNullException.ThrowIfNull(logger);
        try
        {
            await replace().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsDeliveryFailure(ex))
        {
            logger.LogWarning("確認の画面の送信後に元のメッセージを差し替えられませんでした（{Error}）。結果は追送します。", ex.GetType().Name);
            await followup().ConfigureAwait(false);
        }
    }

    private static bool IsDeliveryFailure(Exception ex) => ex switch
    {
        Discord.Net.HttpException or TimeoutException => true,
        TaskCanceledException canceled => !canceled.CancellationToken.IsCancellationRequested,
        _ => false,
    };
}
