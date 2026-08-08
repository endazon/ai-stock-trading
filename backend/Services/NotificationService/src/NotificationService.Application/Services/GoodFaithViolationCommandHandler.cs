using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.State;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Application.Services;

// FR-19, FR-10, FR-11, UC-06, #464, ADR-0028 決定2/決定3, IADR-0182:
// GFV 違反による停止の解除コマンドの処理。**kill switch と同じ閂の並び**（多層認証 → コマンド解析 →
// 確認フレーズ → Risk 呼び出し）に揃える。
//
// ADR-0028 §結果 が「**解除操作そのものが新たな攻撃面・誤操作面になる**」と明記しており、
// 「既存の破壊的統制操作（`/kill-switch` 等）と同じ確認水準・同じ多層認証に揃える」ことが issue の要求である。
//
// 確認ボタン → 確認フレーズのモーダルは Gateway アダプタが担い、本ハンドラはフレーズを受け取って検証する
// （KillSwitchCommandHandler と同型）。
public sealed class GoodFaithViolationCommandHandler(
    IGoodFaithViolationController controller,
    DiscordBotOptions options,
    ILogger<GoodFaithViolationCommandHandler> logger)
{
    public async Task<GoodFaithViolationCommandResult> HandleAsync(
        DiscordCommandContext context,
        string? confirmationPhrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 閂1: 多層認証（DM・サーバー・チャンネル・許可リスト・Keycloak マッピング）。空設定は全拒否。
        var auth = DiscordCommandAuthorizer.Authorize(context, options);
        if (!auth.IsAllowed)
        {
            // 詳細設計07: 許可外の着信は無視しログのみ残す。利用者へ理由は返さない。
            logger.LogWarning(
                "Discord コマンドを拒否しました（User={UserId}・Channel={ChannelId}・理由={Reason}）。",
                context.UserId, context.ChannelId, auth.Reason);
            return GoodFaithViolationCommandResult.Denied(auth.Reason);
        }

        // 閂2: コマンド解析。GFV 解除以外（kill switch・pause・段階・未知）は本ハンドラでは実行しない。
        // 種別を明示的に絞ることで、別種のコマンドが解除経路へ落ちることを構造的に防ぐ。
        var command = BotCommandParser.Parse(context.RawCommand);
        if (command.Kind is not BotCommandKind.GoodFaithViolationClear)
        {
            logger.LogWarning(
                "GFV 解除以外のコマンドを拒否しました（Actor={Actor}・Kind={Kind}）。", auth.Actor, command.Kind);
            return GoodFaithViolationCommandResult.Denied("GFV 解除コマンドではない");
        }

        // 閂3: 確認フレーズ必須（**未設定なら拒否＝安全既定**。kill switch と同一の Verify を通す）。
        // 統制を解く方向の操作であり、kill switch の解除と同じ水準を要する。
        // フレーズ不一致・未入力・未設定は Risk を呼ばず拒否する。
        var confirmation = KillSwitchConfirmation.Verify(confirmationPhrase, options);
        if (!confirmation.IsConfirmed)
        {
            logger.LogWarning(
                "GFV 解除を確認ステップで拒否しました（Actor={Actor}・理由={Reason}）。", auth.Actor, confirmation.Reason);
            return GoodFaithViolationCommandResult.Denied(confirmation.Reason);
        }

        // ADR-0028 決定2: 解除は「**原因の是正が済んでいることの確認を伴う**」ものであり、
        // 理由が監査ログへ残る。操作者と経路を理由に残す（Risk 側は理由必須）。
        var reason = $"Discord Bot 経由の GFV 解除（actor={auth.Actor}・原因の是正を確認済みとして解除）";

        var result = await controller.ClearAsync(reason, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "GFV 解除を実行しました（Actor={Actor}・Succeeded={Succeeded}・Cleared={Cleared}）。",
            auth.Actor, result.Succeeded, result.Cleared);

        return GoodFaithViolationCommandResult.Executed(result);
    }
}

// FR-19, #464: GFV 解除コマンドの処理結果。Denied は Risk を呼んでいないことを意味する。
public sealed record GoodFaithViolationCommandResult(bool WasExecuted, string Message, bool? Cleared = null)
{
    public static GoodFaithViolationCommandResult Denied(string reason) => new(false, reason);

    public static GoodFaithViolationCommandResult Executed(GoodFaithViolationClearResult result) =>
        new(true, result.Message, result.Cleared);
}
