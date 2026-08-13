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
    /// <param name="clearanceReason">
    /// 利用者が入力した**何を是正したか**。ADR-0028 §理由「解除の記録が監査ログに残ることで、
    /// <b>後から「なぜ解除したか」を追える</b>」の実体である。
    /// <b>決定3 により Discord が唯一の窓口であるため、ここが空なら「なぜ」は監査から復元できない。</b>
    /// </param>
    public async Task<GoodFaithViolationCommandResult> HandleAsync(
        DiscordCommandContext context,
        string? confirmationPhrase,
        string? clearanceReason = null,
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

        // 閂4: 理由必須。ADR-0028 決定2 は「**原因の是正が済んでいることの確認を伴う**」解除を求める。
        //
        // **定型文で埋めない。** 決定3 により Discord が唯一の窓口であるため、定型文にすると
        // **全解除の理由が同一文字列になり「なぜ解除したか」が監査から復元できない**
        // （残るのは「解除者が確認済みと自称した」ことだけ）。Risk 側の理由必須チェックも
        // 常に非空の定型文では発火せず、実質的に無効化される。
        if (string.IsNullOrWhiteSpace(clearanceReason))
        {
            logger.LogWarning("GFV 解除を理由欠如で拒否しました（Actor={Actor}）。", auth.Actor);
            return GoodFaithViolationCommandResult.Denied("解除の理由が入力されていない");
        }

        // 操作者と経路は理由へ添える（監査の「誰が」を理由文からも辿れるようにする）。
        var reason = $"{clearanceReason.Trim()}（Discord Bot 経由・actor={auth.Actor}）";

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
