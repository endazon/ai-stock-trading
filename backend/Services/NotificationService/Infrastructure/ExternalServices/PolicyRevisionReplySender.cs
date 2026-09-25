using Discord;
using Microsoft.Extensions.Logging;
using NotificationService.Features.Notifications.RevisePolicy;

namespace NotificationService.Infrastructure.ExternalServices;

// FR-07, FR-14, UC-03〜05, ADR-0003, #1016, IADR-0431 決定 5: `/policy` の応答の送り順と確認ボタンの位置。
// DiscordNetBotGateway から切り出した（Discord.Net の相互作用を持たない＝送信の関数を差し替えて試験できる）。
//
// 🔴 **確認ボタンは最後の通にだけ付け、それより前の通（方針の全文）をすべて送り終えてからしか送らない。**
// 途中の通が送れなければボタンは送らず、「版 N は承認待ちのまま保存されている（まだ確定していない）」ことと、
// 確定・やり直しの方法を知らせる（案は報告書サービスに保存済みであり、「何も起きていない」と伝えない＝原則 A）。
public static class PolicyRevisionReplySender
{
    public const string ApprovePrompt =
        "\n\n確定すると、この版の方針が取引に適用されます。やめる場合は押さずに置くか、/policy で指示し直してください。";

    public const string ApproveButtonPrefix = DiscordNetBotGateway.ReportApproveButtonPrefix;

    /// <summary>
    /// 応答を順に送る。<paramref name="followup"/> は 1 通を送る関数（本文・ボタン）。
    /// </summary>
    public static async Task SendAsync(
        PolicyRevisionCommandResult result,
        Func<string, MessageComponent?, Task> followup,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(followup);

        if (!result.WasExecuted)
        {
            await followup(
                result.IsDenied ? "この操作は実行されませんでした（許可されていません）。" : result.Message, null).ConfigureAwait(false);
            return;
        }

        var approvable = result.PeriodKey is not null && result.Version is not null;
        var sent = 0;
        try
        {
            for (; sent < result.Messages.Count - 1; sent++)
                await followup(result.Messages[sent], null).ConfigureAwait(false);

            var last = result.Messages[^1];
            if (!approvable)
            {
                await followup(last, null).ConfigureAwait(false);
                return;
            }

            var button = new ComponentBuilder().WithButton(
                $"版 {result.Version} を確定する",
                ApproveButtonPrefix + $"{result.PeriodKey}-{result.Version}",
                // 確定は取引方針を有効化する破壊的操作（ADR-0003）のため危険色。
                ButtonStyle.Danger).Build();
            await followup(last + ApprovePrompt, button).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex,
                "方針の改訂案の応答の送信が途中で失敗しました（送れた通={Sent}/{Total}）。確認ボタンは出していません。",
                sent, result.Messages.Count);

            var notice = approvable
                ? $"方針案の表示が途中で失敗したため、確認ボタンを出していません。版 {result.Version}（{result.PeriodKey}）は"
                  + "承認待ちのまま保存されています（まだ確定していません・取引には適用されていません）。"
                  + $"全文を見てから確定するには /policy で指示し直してください。内容を把握済みなら /report approve period:{result.PeriodKey} でも確定できます。"
                : "方針案の表示が途中で失敗しました。案は保存されていますが承認待ちではありません。/report show で状態を確認してください。";
            try
            {
                await followup(notice, null).ConfigureAwait(false);
            }
            catch (Exception inner) when (inner is not OperationCanceledException)
            {
                logger.LogWarning(inner, "送信失敗の知らせも送れませんでした。");
            }
        }
    }
}
