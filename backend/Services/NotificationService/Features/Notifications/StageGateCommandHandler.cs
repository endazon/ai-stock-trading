using Microsoft.Extensions.Logging;
using NotificationService.Domain;

namespace NotificationService.Features.Notifications;

// FR-20, FR-14, UC-06, ADR-0008, IADR-0070/0081: 段階ゲートのコマンド処理。多層認証 → コマンド解析 →
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
    /// <summary>
    /// FR-20, FR-11, SC-02, #466, §4.1 追補3（質問票 第15回 Q13-a）, IADR-0180 決定5:
    /// **昇格の確認プロンプトへ添える引き下げ警告**（出ていなければ <c>null</c>）。
    /// <para>
    /// 裁定は「`/stage status` だけでは足りない ——『承認前に status を読む』は<b>人の運用に依存する前提</b>で
    /// あり、読まなければ警告が届かない」と述べている。**確認ボタンを押した後にだけ警告が出る形は、
    /// この批判がそのまま当てはまる**（押してからでは遷移は既に受理・記録されている）。
    /// よって<b>確認を出す前</b>にも同じ警告を届ける。
    /// </para>
    /// <para>
    /// 多層認証は本メソッドでも掛ける（許可外の着信へ現況を漏らさない）。**照会に失敗したら
    /// <c>null</c>＝警告なし**——確認そのものを止めない（警告は昇格を妨げないという裁定に従う）。
    /// </para>
    /// </summary>
    public async Task<string?> GetPromotionWarningAsync(
        DiscordCommandContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!DiscordCommandAuthorizer.Authorize(context, options).IsAllowed)
        {
            return null;
        }

        var status = await controller.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return status.Succeeded ? status.Stage1Warning : null;
    }

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

                    // FR-20, FR-11, SC-02, #466, §4.1 追補3（質問票 第15回 Q13-a）, IADR-0180:
                    // **昇格承認（`/stage promote`）にだけ**最小取引件数の引き下げ警告を足す。
                    //
                    // 裁定は「昇格承認」を名指ししている。差し戻し（`/stage demote`）は安全側の操作であり、
                    // そこへ同じ警告を出すと「読まれない警告」化を招く——裁定が「`/stage status` だけでは
                    // 足りない」とした理由（人の運用に依存する前提を置かない）の裏返しである。
                    var isPromotion = command.Kind == BotCommandKind.StagePromote;
                    var warned = isPromotion && result.Stage1Warning is not null;

                    logger.LogInformation(
                        "段階遷移を要求しました（Actor={Actor}・Kind={Kind}・Target={Target}・Succeeded={Succeeded}・Accepted={Accepted}・Warned={Warned}）。",
                        auth.Actor, command.Kind, target, result.Succeeded, result.Accepted, warned);

                    return warned
                        ? StageGateCommandResult.Transition(result, result.Stage1Warning)
                        : StageGateCommandResult.Transition(result);
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

    // #466: 警告なし（既定値のまま・差し戻し・旧版 Risk）の遷移応答。
    public static StageGateCommandResult Transition(StageTransitionCommandResult result) =>
        new(true, result.Message, result.Accepted);

    // #466, IADR-0180: 昇格承認に警告を添えた応答。**警告は遷移の可否を変えない**——
    // `Accepted` は Risk の判定をそのまま運ぶ（警告を理由に昇格を拒否しない。裁定が明示）。
    public static StageGateCommandResult Transition(StageTransitionCommandResult result, string? warning) =>
        new(true, warning is null ? result.Message : $"{result.Message}\n{warning}", result.Accepted);
}
