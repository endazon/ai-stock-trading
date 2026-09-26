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

    // FR-07, FR-14, UC-03〜05, #834: `/report` の period（会話キー）の入力補完の候補。
    //
    // 閂1（多層認証）は**ここでも掛ける**。補完は「どの会話キーが存在するか」を漏らす経路であり、
    // 許可外の利用者へは**候補を返さない**（kill switch / GFV と同水準＝窓口の認可水準を下げない）。
    //
    // 🔴 **fail-safe**: 一覧の取得に失敗したら候補なしで素通しする（コントローラが空を返す契約）。
    // 補完は入力の補助であって統制ではない——補完が引けないことを理由に `/report` 自体を壊さない。
    //
    // FR-14, #843 項目2: 一覧照会には**補完専用の時間予算**（SuggestionBudget）を掛ける。Discord の autocomplete は
    // 3 秒で応答期限が切れるが、名前付き HttpClient `report-review` の Timeout は 5 秒（照会・確定・差し戻しと共有の
    // ため変えない）。予算が無いと、一覧が 3〜5 秒かかる帯では毎打鍵で応答期限切れ（Gateway の catch）へ落ちる。
    public Task<IReadOnlyList<string>> SuggestPeriodsAsync(
        DiscordCommandContext context,
        string? input,
        CancellationToken cancellationToken = default) =>
        SuggestPeriodsAsync(context, input, SuggestionBudget, cancellationToken);

    // FR-14, #843 項目2: 補完の一覧照会に掛ける予算。Discord の応答期限 3 秒から応答送信の余白 0.5 秒を引いた値。
    // 数え方は「一覧照会の開始から」である（Discord の着信時刻から数えると Pod との時計ずれが予算に入る。
    // 形の比較は作業仕様書 20260925_843 の規則 11 の表）。
    public static readonly TimeSpan SuggestionBudget = TimeSpan.FromMilliseconds(2500);

    // テストが短い予算を与えるための入口（DI の形＝コンストラクタは変えない）。
    internal async Task<IReadOnlyList<string>> SuggestPeriodsAsync(
        DiscordCommandContext context,
        string? input,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var auth = DiscordCommandAuthorizer.Authorize(context, options);
        if (!auth.IsAllowed)
        {
            // 詳細設計07: 許可外の着信は無視しログのみ残す。候補も返さない（存在の探索を助けない）。
            logger.LogWarning(
                "報告書の入力補完を拒否しました（User={UserId}・Channel={ChannelId}・理由={Reason}）。",
                context.UserId, context.ChannelId, auth.Reason);
            return [];
        }

        // #843 項目2: 予算切れはアダプタから見ると「呼び出し側の取り消し」であり、アダプタは空へ丸めず伝播させる
        // （`Handled` の既存の契約）。**空へ丸めるのはここ**である。呼び出し側自身の取り消しは従来どおり伝播させる。
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(budget);

        IReadOnlyList<string> periodKeys;
        try
        {
            periodKeys = await controller.ListPeriodKeysAsync(budgetCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "報告書一覧の照会が補完の予算（{BudgetMs} ms）内に返りませんでした。候補なしで応答します。",
                (int)budget.TotalMilliseconds);
            return [];
        }

        return ReportPeriodSuggestions.Filter(periodKeys, input);
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

        // FR-09, UC-03, IADR-0240 決定11, #774: 多層認証で解決した操作者を確定要求へ添える。Bot のトークンは
        // owner マップ機密クライアントのもので人を表さない——添えないと確定者が通知・監査台帳で unknown になる。
        var result = await controller
            .ConfirmAsync(periodKey, version, actor, cancellationToken)
            .ConfigureAwait(false);

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
// ConfirmedNow は**この要求で報告書サービスに確定を問い合わせ、この版で確定されていると確かめた**か（今回の遷移、または
// 同じ版の冪等な再確定〔再起動の後の押し直し＝IADR-0433 決定 7〕）。窓口の二重送信の 2 回目（AlreadyConfirmed・API を呼ばない）
// と、別の版で確定済み（報告書サービスが version で示す）では false になる。
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
