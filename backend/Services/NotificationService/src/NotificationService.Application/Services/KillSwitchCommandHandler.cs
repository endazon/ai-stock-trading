using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.State;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Application.Services;

// FR-14, UC-06, IADR-0062: kill switch コマンドの処理。多層認証 → コマンド解析 → 確認ステップ → Risk 呼び出し
// の順に閂を掛ける。いずれかで不成立なら Risk を呼ばない（誤爆防止）。
//
// 本ハンドラは Discord.Net に依存しない（Gateway アダプタが DiscordCommandContext に変換して渡す）。
// 拒否理由は詳細設計07 に従いログに残す。利用者への応答文言は呼び出し側（Gateway アダプタ）が組む。
public sealed class KillSwitchCommandHandler(
    IKillSwitchController controller,
    DiscordBotOptions options,
    ILogger<KillSwitchCommandHandler> logger)
{
    // FR-14, UC-06, ADR-0009, IADR-0097: confirmationPhrase は起動・解除の双方で要求する（planning#35 裁定）。
    // 起動・解除いずれも「確認ボタン＋確認フレーズ」を要し、フレーズ不一致・未入力・未設定は Risk を呼ばず拒否する。
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

        // 閂2: コマンド解析。kill switch 以外（未知・pause/resume/status）は本ハンドラでは実行しない。
        // pause 系は PauseCommandHandler が扱う。ここで種別を明示的に絞ることで、非 kill switch 種別が
        // 末尾の「起動でなければ解除」分岐へ落ちて誤って解除されることを構造的に防ぐ。
        var command = BotCommandParser.Parse(context.RawCommand);
        if (command.Kind is not (BotCommandKind.KillSwitchEngage or BotCommandKind.KillSwitchDisengage))
        {
            logger.LogWarning("kill switch 以外のコマンドを拒否しました（Actor={Actor}・Kind={Kind}）。", auth.Actor, command.Kind);
            return KillSwitchCommandResult.Denied("kill switch コマンドではない");
        }

        // 閂3: 起動・解除ともに確認フレーズ必須（未設定なら双方拒否＝安全既定・IADR-0097）。
        // 解除も実弾方向へ戻す高リスク操作のため、起動と同一の Verify を通す。フレーズ不一致・未入力・未設定は
        // Risk を呼ばず拒否する。拒否理由（内部の層名）は操作種別を添えてログに残す（監査は既存経路＝LogWarning）。
        var confirmation = KillSwitchConfirmation.Verify(confirmationPhrase, options);
        if (!confirmation.IsConfirmed)
        {
            var operation = command.Kind == BotCommandKind.KillSwitchEngage ? "起動" : "解除";
            logger.LogWarning(
                "kill switch {Operation}を確認ステップで拒否しました（Actor={Actor}・理由={Reason}）。",
                operation, auth.Actor, confirmation.Reason);
            return KillSwitchCommandResult.Denied(confirmation.Reason);
        }

        // 理由には操作者（Keycloak 利用者名）と経路を残す。Risk 側は理由必須（ADR-0007）。
        var reason = $"Discord Bot 経由の操作（actor={auth.Actor}）";

        // 起動・解除いずれも冪等（既に当該状態なら Risk が現状態を返すのみ）。
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
