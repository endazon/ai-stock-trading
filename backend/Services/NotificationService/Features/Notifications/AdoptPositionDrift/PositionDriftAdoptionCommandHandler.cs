using AiStockTrading.Shared.Contracts.Logging;
using Microsoft.Extensions.Logging;
using NotificationService.Domain;

namespace NotificationService.Features.Notifications.AdoptPositionDrift;

// FR-10, FR-11, FR-14, UC-06, ADR-0003, ADR-0041 決定 4, #871, IADR-0350, IADR-0423:
// 台帳とブローカーの乖離の取り込み（`/drift adopt <symbol> <market>`）のコマンド処理。
//
// ADR-0041 決定 4 は取り込みの窓口を「owner の REST API と Discord Bot の両方」に置き、**記録の内容は同じ**とした。
// 台帳を書き換える操作であるため、**kill switch・GFV 解除と同じ閂の並び**（多層認証 → コマンド解析 →
// 確認フレーズ → 理由必須 → リスク管理の呼び出し）に揃える。確認ボタン → 理由＋確認フレーズのモーダルは
// Gateway アダプタが担い、本ハンドラはモーダルの入力を受け取って検証する（GoodFaithViolationCommandHandler と同型）。
//
// 🔴 **いずれかの閂で止まればリスク管理を呼ばない**——閂が「呼んだ後で無視する」形なら、台帳が実際に書き換わってしまう。
public sealed class PositionDriftAdoptionCommandHandler(
    IPositionDriftAdoptionController controller,
    DiscordBotOptions options,
    ILogger<PositionDriftAdoptionCommandHandler> logger)
{
    /// <param name="adoptionReason">
    /// 利用者が入力した**なぜ台帳を合わせるのか**（例: 証券会社のアプリで全株を売却した）。API の <c>reason</c> と同じ欄であり、
    /// 🔴 <b>加工せずにそのまま送る</b>——GFV 解除のように操作者を併記すると、同じ取り込みでも窓口によって理由文が変わり、
    /// ADR-0041 決定 4 の「記録の内容は同じ」が崩れる。操作者は構造化した欄（onBehalfOf）で運ぶ。
    /// </param>
    public async Task<PositionDriftAdoptionCommandResult> HandleAsync(
        DiscordCommandContext context,
        string? confirmationPhrase,
        string? adoptionReason,
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
                LogSanitizer.Sanitize(context.UserId), LogSanitizer.Sanitize(context.ChannelId), auth.Reason);
            return PositionDriftAdoptionCommandResult.Denied(auth.Reason);
        }

        // 閂2: コマンド解析。取り込み以外（kill switch・GFV・段階・未知・書式外の銘柄や市場）は本ハンドラでは実行しない。
        // 種別を明示的に絞ることで、別種のコマンドが台帳の書き換えへ落ちることを構造的に防ぐ。
        var command = BotCommandParser.Parse(context.RawCommand);
        if (command is not { Kind: BotCommandKind.PositionDriftAdopt, Symbol: { } symbol, Market: { } market })
        {
            logger.LogWarning(
                "乖離の取り込み以外のコマンドを拒否しました（Actor={Actor}・Kind={Kind}）。",
                LogSanitizer.Sanitize(auth.Actor), command.Kind);
            return PositionDriftAdoptionCommandResult.Denied("乖離の取り込みのコマンドではない");
        }

        // 閂3: 確認フレーズ必須（**未設定なら拒否＝安全既定**。kill switch・GFV 解除と同一の Verify を通す）。
        // 台帳を書き換える操作であり、kill switch と同じ水準を要する（#871 の要求）。
        var confirmation = KillSwitchConfirmation.Verify(confirmationPhrase, options);
        if (!confirmation.IsConfirmed)
        {
            logger.LogWarning(
                "乖離の取り込みを確認ステップで拒否しました（Actor={Actor}・理由={Reason}）。",
                LogSanitizer.Sanitize(auth.Actor), confirmation.Reason);
            return PositionDriftAdoptionCommandResult.Denied(confirmation.Reason);
        }

        // 閂4: 理由必須（API と同じ。定型文で埋めない——「なぜ台帳を合わせたか」が監査から復元できなくなる）。
        if (string.IsNullOrWhiteSpace(adoptionReason))
        {
            logger.LogWarning("乖離の取り込みを理由欠如で拒否しました（Actor={Actor}）。", LogSanitizer.Sanitize(auth.Actor));
            return PositionDriftAdoptionCommandResult.Denied("取り込みの理由が入力されていない");
        }

        // FR-11, #871, IADR-0240 決定11, IADR-0383: **多層認証が解決した操作者を onBehalfOf で運ぶ**
        // （`auth.Actor`＝Keycloak 利用者名。コマンド文字列からは採らない）。理由文は前後の空白だけを落として送る。
        var result = await controller
            .AdoptAsync(symbol, market, adoptionReason.Trim(), auth.Actor!, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "乖離の取り込みを要求しました（Actor={Actor}・Symbol={Symbol}・Market={Market}・Succeeded={Succeeded}・Adopted={Adopted}）。",
            LogSanitizer.Sanitize(auth.Actor), symbol, market, result.Succeeded, result.Adopted);

        return PositionDriftAdoptionCommandResult.Executed(result);
    }
}

// FR-10, #871: 取り込みコマンドの処理結果。**Denied はリスク管理を呼んでいない（＝台帳は変わっていない）ことを意味する。**
// WasExecuted=true はリスク管理を呼んだことを意味し、受理されたか（Adopted）・拒否の理由（Message）はその応答に従う。
public sealed record PositionDriftAdoptionCommandResult(bool WasExecuted, string Message, bool? Adopted = null)
{
    public static PositionDriftAdoptionCommandResult Denied(string reason) => new(false, reason);

    public static PositionDriftAdoptionCommandResult Executed(PositionDriftAdoptionResult result) =>
        new(true, result.Message, result.Adopted);
}
