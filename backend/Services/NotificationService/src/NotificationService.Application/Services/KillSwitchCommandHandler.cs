using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.State;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Application.Services;

// FR-14, UC-06, IADR-0063: kill switch コマンドの処理。多層認証 → コマンド解析 → 確認ステップ → Risk 呼び出し
// の順に閂を掛ける。いずれかで不成立なら Risk を呼ばない（誤爆防止）。
//
// 本ハンドラは Discord.Net に依存しない（Gateway アダプタが DiscordCommandContext に変換して渡す）。
// 拒否理由は詳細設計07 に従いログに残す。利用者への応答文言は呼び出し側（Gateway アダプタ）が組む。
public sealed class KillSwitchCommandHandler(
    IKillSwitchController controller,
    DiscordBotOptions options,
    ILogger<KillSwitchCommandHandler> logger)
{
    // confirmationPhrase は起動時のみ要求される（解除は確認ボタンのみ＝詳細設計07「解除のみ確認ステップを追加」）。
    public async Task<KillSwitchCommandResult> HandleAsync(
        DiscordCommandContext context,
        string? confirmationPhrase,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 閂1: 多層認証（DM・サーバー・チャンネル・許可リスト・Keycloak マッピング）。
        var auth = DiscordCommandAuthorizer.Authorize(context, options);
        if (!auth.IsAllowed)
        {
            // 詳細設計07: 許可外の着信は無視しログのみ残す。利用者へ理由は返さない。
            logger.LogWarning(
                "Discord コマンドを拒否しました（User={UserId}・Channel={ChannelId}・理由={Reason}）。",
                context.UserId, context.ChannelId, auth.Reason);
            return KillSwitchCommandResult.Denied(auth.Reason);
        }

        // 閂2: コマンド解析。未知は実行しない。
        var command = BotCommandParser.Parse(context.RawCommand);
        if (command.Kind == BotCommandKind.Unknown)
        {
            logger.LogWarning("未知のコマンドを拒否しました（Actor={Actor}）。", auth.Actor);
            return KillSwitchCommandResult.Denied("未知のコマンド");
        }

        // 閂3: 起動は確認フレーズ必須（未設定なら拒否）。解除には要求しない。
        if (command.Kind == BotCommandKind.KillSwitchEngage)
        {
            var confirmation = KillSwitchConfirmation.Verify(confirmationPhrase, options);
            if (!confirmation.IsConfirmed)
            {
                logger.LogWarning(
                    "kill switch 起動を確認ステップで拒否しました（Actor={Actor}・理由={Reason}）。",
                    auth.Actor, confirmation.Reason);
                return KillSwitchCommandResult.Denied(confirmation.Reason);
            }
        }

        // 理由には操作者（Keycloak 利用者名）と経路を残す。Risk 側は理由必須（ADR-0007）。
        var reason = $"Discord Bot 経由の操作（actor={auth.Actor}）";

        // 起動は冪等（起動済みなら Risk が現状態を返すのみ）。
        var result = command.Kind == BotCommandKind.KillSwitchEngage
            ? await controller.EngageAsync(reason, cancellationToken).ConfigureAwait(false)
            : await controller.DisengageAsync(reason, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "kill switch 操作を実行しました（Actor={Actor}・Kind={Kind}・Succeeded={Succeeded}・Engaged={Engaged}）。",
            auth.Actor, command.Kind, result.Succeeded, result.Engaged);

        return KillSwitchCommandResult.Executed(result);
    }
}

// FR-14: kill switch コマンドの処理結果。Denied は Risk を呼んでいないことを意味する。
public sealed record KillSwitchCommandResult(bool WasExecuted, KillSwitchResult? Result, string Reason)
{
    public static KillSwitchCommandResult Denied(string reason) => new(false, null, reason);

    public static KillSwitchCommandResult Executed(KillSwitchResult result) => new(true, result, result.Message);
}
