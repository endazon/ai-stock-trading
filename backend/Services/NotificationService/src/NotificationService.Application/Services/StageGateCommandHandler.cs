using AiStockTrading.Notification.Application.Ports;
using AiStockTrading.Notification.Application.State;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.Notification.Application.Services;

// FR-20, FR-14, UC-06, ADR-0007/0008, IADR-0070/0081: 段階ゲートのコマンド処理。多層認証 → コマンド解析 →
// Risk 呼び出しの順に閂を掛ける。kill switch / pause（KillSwitchCommandHandler / PauseCommandHandler）と同型。
//
// 確認（promote/demote の確認ボタン）は Gateway アダプタが担い、本ハンドラは確認済み前提で呼ばれる
// （pause と同型）。段階遷移の二重適用は Risk 側の連番検証（現段階指定＝422）で構造的に防がれるため、本ハンドラは
// 版番号ガードを持たない（IADR-0081 決定2）。
//
// 本ハンドラは Discord.Net に依存しない（Gateway アダプタが DiscordCommandContext に変換して渡す）。
// 段階ゲート系以外（kill switch・pause・未知）は本ハンドラでは実行しない。
public sealed class StageGateCommandHandler(
    IStageGateController controller,
    DiscordBotOptions options,
    ILogger<StageGateCommandHandler> logger)
{
    public async Task<StageGateCommandResult> HandleAsync(
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
            return StageGateCommandResult.Denied(auth.Reason);
        }

        // 閂2: コマンド解析。段階ゲート系以外は本ハンドラでは実行しない。
        var command = BotCommandParser.Parse(context.RawCommand);
        switch (command.Kind)
        {
            case BotCommandKind.StageStatus:
                {
                    var status = await controller.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("段階ゲート照会を実行しました（Actor={Actor}・Succeeded={Succeeded}）。", auth.Actor, status.Succeeded);
                    return StageGateCommandResult.StatusView(status);
                }

            case BotCommandKind.StageWithdrawal:
                {
                    var withdrawal = await controller.EvaluateWithdrawalAsync(cancellationToken).ConfigureAwait(false);
                    logger.LogInformation("撤退評価を実行しました（Actor={Actor}・Succeeded={Succeeded}）。", auth.Actor, withdrawal.Succeeded);
                    return StageGateCommandResult.StatusView(withdrawal);
                }

            case BotCommandKind.StagePromote or BotCommandKind.StageDemote when command.TargetStage is { } target:
                {
                    // 承認者は Risk 側が認証済みトークン（owner マップ）から取る（要求本文は targetStage のみ）。
                    var result = await controller.RequestTransitionAsync(target, cancellationToken).ConfigureAwait(false);
                    logger.LogInformation(
                        "段階遷移を要求しました（Actor={Actor}・Kind={Kind}・Target={Target}・Succeeded={Succeeded}・Accepted={Accepted}）。",
                        auth.Actor, command.Kind, target, result.Succeeded, result.Accepted);
                    return StageGateCommandResult.Transition(result);
                }

            default:
                // 他系（kill switch/pause 等）のほか、範囲外の遷移先・typo でパーサが Unknown に丸めた場合もここを通る。
                logger.LogWarning(
                    "段階ゲート系として解釈できないコマンドを拒否しました（Actor={Actor}・Kind={Kind}・未知/範囲外/他系）。",
                    auth.Actor, command.Kind);
                return StageGateCommandResult.Denied("段階ゲート系コマンドではない");
        }
    }
}

// FR-20: 段階ゲートコマンドの処理結果。Denied は Risk を呼んでいないことを意味する。
// Accepted は遷移要求のみ意味を持つ（照会・撤退評価では null）。
public sealed record StageGateCommandResult(bool WasExecuted, string Message, bool? Accepted = null)
{
    public static StageGateCommandResult Denied(string reason) => new(false, reason);

    // 照会・撤退評価は「実行された（＝認証通過・Risk 照会済み）」が遷移結果は持たない。
    public static StageGateCommandResult StatusView(StageGateStatusResult status) => new(true, status.Message);

    public static StageGateCommandResult Transition(StageTransitionCommandResult result) =>
        new(true, result.Message, result.Accepted);
}
