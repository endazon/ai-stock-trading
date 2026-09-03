using Microsoft.Extensions.Logging;
using NotificationService.Domain;

namespace NotificationService.Features.Notifications.ReviewReport;

// FR-14, FR-07, UC-03〜05, ADR-0003, IADR-0240: 報告書レビューコマンドの処理。
// 多層認証 → コマンド解析 → 版番号ガード → 報告書サービス呼び出しの順に閂を掛ける。
// kill switch / pause / 段階ゲートと同型で、いずれかで不成立なら報告書サービスを呼ばない。
//
// **二重適用は 2 層で防ぐ**（IADR-0240 決定2）。
//   層1（本ハンドラ）: 同一 `periodKey ＋ 版番号` の 2 回目は **確定 API を呼ばずに**「確定済み」を返す。
//                      窓口での多重押下（ボタンの連打・チャットUI との同時操作）をここで吸収する。
//   層2（報告書サービス）: 確定 API が版番号付き冪等であり、**二重適用の権威はこちらが持つ**。
//                      Bot はステートレスであるべき（詳細設計07）ため、層1 だけには依存しない。
//
// 本ハンドラは Discord.Net に依存しない（Gateway アダプタが DiscordCommandContext に変換して渡す）。
public sealed class ReportCommandHandler(
    IReportReviewController controller,
    VersionedConfirmationGuard guard,
    DiscordBotOptions options,
    ILogger<ReportCommandHandler> logger)
{
    public async Task<ReportCommandResult> HandleAsync(
        DiscordCommandContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 閂1: 多層認証（DM・サーバー・チャンネル・許可リスト・Keycloak マッピング）。kill switch と同水準。
        // ボタン押下時にも本ハンドラを通るため、押下者のすり替えはここで弾かれる（IADR-0240 決定7）。
        var auth = DiscordCommandAuthorizer.Authorize(context, options);
        if (!auth.IsAllowed)
        {
            // 詳細設計07: 許可外の着信は無視しログのみ残す。利用者へ理由は返さない。
            logger.LogWarning(
                "Discord コマンドを拒否しました（User={UserId}・Channel={ChannelId}・理由={Reason}）。",
                context.UserId, context.ChannelId, auth.Reason);
            return ReportCommandResult.Denied(auth.Reason);
        }

        // 閂2: コマンド解析。報告書レビュー系以外（kill switch・pause・段階・GFV・未知）は実行しない。
        // 書式外の periodKey・不正な版番号もパーサが Unknown に丸めるため、ここで止まる。
        var command = BotCommandParser.Parse(context.RawCommand);
        switch (command)
        {
            case { Kind: BotCommandKind.ReportShow, PeriodKey: { } showKey }:
                return await ShowAsync(showKey, auth.Actor!, cancellationToken).ConfigureAwait(false);

            // 版番号なしの approve は**確認ボタンを出す前段**。ここでは確定せず、現在の版番号だけを返す。
            case { Kind: BotCommandKind.ReportApprove, PeriodKey: { } preKey, Version: null }:
                return await ShowAsync(preKey, auth.Actor!, cancellationToken).ConfigureAwait(false);

            case { Kind: BotCommandKind.ReportApprove, PeriodKey: { } key, Version: { } version }:
                return await ApproveAsync(key, version, auth.Actor!, cancellationToken).ConfigureAwait(false);

            case { Kind: BotCommandKind.ReportRequestChanges, PeriodKey: { } rcKey }:
                return await RequestChangesAsync(rcKey, command.Version, auth.Actor!, cancellationToken)
                    .ConfigureAwait(false);

            default:
                // 他系（kill switch/pause/段階/GFV）のほか、書式外の periodKey・不正な版番号で
                // パーサが Unknown に丸めた場合もここを通る。
                logger.LogWarning(
                    "報告書レビュー系として解釈できないコマンドを拒否しました（Actor={Actor}・Kind={Kind}）。",
                    auth.Actor, command.Kind);
                return ReportCommandResult.Denied("報告書レビュー系コマンドではない");
        }
    }

    // FR-07, UC-03〜05: レビュー局面（版番号）の照会。表示専用・副作用なし。
    // **報告書の本文・要約は取りに行かない**（IADR-0240 決定4。要約は ReportDraftPresented 通知が
    // サニタイズ済みで届けており、Bot が生本文を取るとそのサニタイズを迂回する）。
    private async Task<ReportCommandResult> ShowAsync(
        string periodKey, string actor, CancellationToken cancellationToken)
    {
        var review = await controller.GetReviewAsync(periodKey, cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "報告書レビューを照会しました（Actor={Actor}・PeriodKey={PeriodKey}・Succeeded={Succeeded}）。",
            actor, periodKey, review.Succeeded);

        return review.Succeeded
            ? ReportCommandResult.Review(review)
            : ReportCommandResult.Failed(review.Message);
    }

    // FR-07, FR-14, ADR-0003, 詳細設計07 §二重実行防止: 版番号付きの確定。
    private async Task<ReportCommandResult> ApproveAsync(
        string periodKey, int version, string actor, CancellationToken cancellationToken)
    {
        // 閂3: 版番号ガード（層1）。同一 対象ID＋版番号 の 2 回目は確定 API を呼ばない。
        var outcome = guard.TryConfirm(periodKey, version);
        switch (outcome)
        {
            case ConfirmationOutcome.AlreadyConfirmed:
                logger.LogInformation(
                    "報告書の二重確定を窓口で吸収しました（Actor={Actor}・PeriodKey={PeriodKey}・版={Version}）。",
                    actor, periodKey, version);
                return ReportCommandResult.AlreadyConfirmed(
                    $"報告書 {periodKey}（版 {version}）は確定済みです。");

            case ConfirmationOutcome.Stale:
                logger.LogWarning(
                    "古い版の確定要求を拒否しました（Actor={Actor}・PeriodKey={PeriodKey}・版={Version}）。",
                    actor, periodKey, version);
                return ReportCommandResult.Stale(
                    $"版 {version} は最新ではありません。最新ドラフトを確認してください。");
        }

        var result = await controller.ConfirmAsync(periodKey, version, cancellationToken).ConfigureAwait(false);

        // 🔴 IADR-0240 決定3: 呼び出しが失敗したら予約を解放する。解放しないと同じ版を二度と確定できない
        // （Discord は確定の唯一の窓口であり、詰みは Bot の再起動でしか解けない）。
        if (!result.Succeeded)
        {
            guard.Release(periodKey, version);
            logger.LogWarning(
                "報告書の確定に失敗したため版番号の予約を解放しました（Actor={Actor}・PeriodKey={PeriodKey}・版={Version}）。",
                actor, periodKey, version);
            return ReportCommandResult.Failed(result.Message);
        }

        // 2xx だが受理されなかった（版不一致の 409 等）場合も予約を解放する。**その版では確定できていない**
        // ため、予約を残すと最新版での確定まで窓口が塞がる。
        if (!result.Confirmed)
        {
            guard.Release(periodKey, version);
            logger.LogWarning(
                "報告書の確定が受理されませんでした（Actor={Actor}・PeriodKey={PeriodKey}・版={Version}）。",
                actor, periodKey, version);
            return ReportCommandResult.Failed(result.Message);
        }

        logger.LogInformation(
            "報告書を確定しました（Actor={Actor}・PeriodKey={PeriodKey}・版={Version}）。", actor, periodKey, version);
        return ReportCommandResult.Confirmed(result.Message);
    }

    // FR-14, UC-03〜05: 差し戻し（修正指示）。安全方向・可逆のため版番号ガードは掛けない
    // （報告書サービス側の版番号付き楽観排他が二重適用を防ぐ。差し戻しの二重適用は同サービスで冪等）。
    //
    // 版番号が与えられていなければ照会して補う（スラッシュコマンドは版番号を受け取らない）。
    // **照会に失敗したら差し戻しを行わない**——版番号を推測して楽観排他を素通しさせない。
    private async Task<ReportCommandResult> RequestChangesAsync(
        string periodKey, int? requestedVersion, string actor, CancellationToken cancellationToken)
    {
        var version = requestedVersion;
        if (version is null)
        {
            var review = await controller.GetReviewAsync(periodKey, cancellationToken).ConfigureAwait(false);
            if (!review.Succeeded)
                return ReportCommandResult.Failed(review.Message);

            version = review.Version;
        }

        var result = await controller
            .RequestChangesAsync(periodKey, version.Value, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "報告書を差し戻しました（Actor={Actor}・PeriodKey={PeriodKey}・版={Version}・Succeeded={Succeeded}）。",
            actor, periodKey, version, result.Succeeded);

        return result.Succeeded
            ? ReportCommandResult.Review(result)
            : ReportCommandResult.Failed(result.Message);
    }
}

// FR-14, FR-07, IADR-0240: 報告書レビューコマンドの処理結果。
// WasExecuted=false は報告書サービスを呼んでいない（または呼んで失敗した）ことを意味する。
//
// Version は確認ボタンへ載せる版番号（照会に成功したときのみ非 null）。
// ConfirmedNow は**この要求で実際に確定が起きた**か——二重送信の 2 回目（AlreadyConfirmed）では false になる。
//
// **IsDenied は「多層認証・解析で弾いた」ことを表し、Failed（呼び出しの失敗）と区別する。**
// 拒否理由（内部の層名）は利用者へ返さないが、失敗の理由は返す（利用者が対処できる情報である）。
public sealed record ReportCommandResult(
    bool WasExecuted,
    string Message,
    int? Version = null,
    bool ConfirmedNow = false,
    bool IsDenied = false)
{
    public static ReportCommandResult Denied(string reason) => new(false, reason, IsDenied: true);

    // 呼び出しは行ったが失敗した（HTTP エラー・タイムアウト・受理されず）。失敗を成功に見せない。
    public static ReportCommandResult Failed(string message) => new(false, message);

    public static ReportCommandResult Review(ReportReviewResult review) =>
        new(true, review.Message, review.Version);

    public static ReportCommandResult Confirmed(string message) => new(true, message, ConfirmedNow: true);

    // 二重送信の 2 回目。**副作用なし**（確定 API を呼んでいない）。
    public static ReportCommandResult AlreadyConfirmed(string message) => new(true, message);

    // 確定済みより古い版。「最新ドラフトを確認してください」と応答する。
    public static ReportCommandResult Stale(string message) => new(false, message);
}
