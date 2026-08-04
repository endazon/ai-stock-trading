using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.State;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Application.Services;

// FR-14, UC-06, UC-07, ADR-0009, IADR-0075: 一時停止/再開・状態照会コマンドの処理。多層認証 → コマンド解析 →
// Risk 呼び出しの順に閂を掛ける。kill switch（KillSwitchCommandHandler）と同型だが、**確認フレーズは要求しない**
// （pause は `/resume` で戻せる可逆操作のため。確認ボタンによる2段階は Gateway アダプタが担う）。
//
// 本ハンドラは Discord.Net に依存しない（Gateway アダプタが DiscordCommandContext に変換して渡す）。
// pause 系以外（kill switch・未知）は本ハンドラでは実行しない（KillSwitchCommandHandler が kill switch を扱う）。
public sealed class PauseCommandHandler(
    IPauseController controller,
    DiscordBotOptions options,
    ILogger<PauseCommandHandler> logger)
{
    public async Task<PauseCommandResult> HandleAsync(
        DiscordCommandContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 閂1: 多層認証（DM・サーバー・チャンネル・許可リスト・Keycloak マッピング）。kill switch と同水準。
        var auth = DiscordCommandAuthorizer.Authorize(context, options);
        if (!auth.IsAllowed)
        {
            // 詳細設計07: 許可外の着信は無視しログのみ残す。利用者へ理由は返さない。
            logger.LogWarning(
                "Discord コマンドを拒否しました（User={UserId}・Channel={ChannelId}・理由={Reason}）。",
                context.UserId, context.ChannelId, auth.Reason);
            return PauseCommandResult.Denied(auth.Reason);
        }

        // 閂2: コマンド解析。pause/resume/status 以外は本ハンドラでは実行しない。
        var command = BotCommandParser.Parse(context.RawCommand);
        if (command.Kind is not (BotCommandKind.Pause or BotCommandKind.Resume or BotCommandKind.Status))
        {
            logger.LogWarning("一時停止系以外のコマンドを拒否しました（Actor={Actor}・Kind={Kind}）。", auth.Actor, command.Kind);
            return PauseCommandResult.Denied("一時停止系コマンドではない");
        }

        // 状態照会（/status）は参照のみ（副作用なし）。認証を通過していれば現在状態を返す。
        if (command.Kind == BotCommandKind.Status)
        {
            var status = await controller.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("状態照会を実行しました（Actor={Actor}・Succeeded={Succeeded}）。", auth.Actor, status.Succeeded);
            return PauseCommandResult.StatusView(status);
        }

        // 理由には操作者（Keycloak 利用者名）と経路を残す。Risk 側は理由必須（FR-11・ADR-0009）。
        var reason = $"Discord Bot 経由の操作（actor={auth.Actor}）";

        // pause/resume は冪等（Risk が現状態を返す）。確認フレーズは要求しない（確認ボタンのみ）。
        var result = command.Kind == BotCommandKind.Pause
            ? await controller.PauseAsync(reason, cancellationToken).ConfigureAwait(false)
            : await controller.ResumeAsync(reason, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "一時停止操作を実行しました（Actor={Actor}・Kind={Kind}・Succeeded={Succeeded}・Paused={Paused}）。",
            auth.Actor, command.Kind, result.Succeeded, result.Paused);

        return PauseCommandResult.Executed(result);
    }
}

// FR-14: 一時停止系コマンドの処理結果。Denied は Risk を呼んでいないことを意味する。
// StatusMessage は照会結果の表示テキスト（Pause/Resume では Result.Message）。
public sealed record PauseCommandResult(bool WasExecuted, PauseResult? Result, string Message)
{
    public static PauseCommandResult Denied(string reason) => new(false, null, reason);

    public static PauseCommandResult Executed(PauseResult result) => new(true, result, result.Message);

    // 状態照会は「実行された（＝認証通過・Risk 照会済み）」が pause 操作結果は持たない。
    public static PauseCommandResult StatusView(RiskStatusResult status) => new(true, null, status.Message);
}
