using System.Text;
using Microsoft.Extensions.Logging;
using NotificationService.Domain;
using NotificationService.Features.Notifications.ReviewReport;

namespace NotificationService.Features.Notifications.RevisePolicy;

// FR-13, FR-14, FR-07, ADR-0042 決定 1・2, ADR-0003, #1025, IADR-0433: `/policy` の確認ボタンの押下。
// ①報告書の版を確定し（既存の ReportCommandHandler＝多層認証・版番号ガード・OnBehalfOf）、②**この要求で確定できたときだけ**、
// その版を作った `/policy` の試行に記録された入れ替え案を、③市場監視サービスで適用し、④内訳を Discord と台帳へ出す。
//
// 🔴 **案に載った銘柄だけを適用する**（ADR-0042 決定 1）。銘柄はボタンからも利用者の入力からも取らず、報告書サービスの台帳から取る。
// 🔴 **案を作った時点の監視銘柄が分からなければ適用しない**（楽観排他の基準が無い）。変わっていれば市場監視が 1 件も適用しない。
// 🔴 **確定できなかったとき（版落ち・二重押下・失敗）は適用しない**——確定されていない方針に付いた入れ替えを先に効かせない。
// 🔴 原則 A: 適用の結果が分からない（タイムアウト等）ときは「適用しなかった」と言わず、設定画面での確認を促す。
public sealed class PolicyApprovalCommandHandler(
    ReportCommandHandler reportHandler,
    IPolicyRevisionController policies,
    IMarketMonitorWatchlistController watchlist,
    DiscordBotOptions options,
    ILogger<PolicyApprovalCommandHandler> logger)
{
    /// <summary>応答文の上限（Discord の 2000 文字に余白を残す）。</summary>
    public const int MaxLength = 1900;

    public async Task<PolicyApprovalResult> HandleAsync(DiscordCommandContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // 閂1: 多層認証（ボタンの押下者のすり替えもここで弾く）。
        var auth = DiscordCommandAuthorizer.Authorize(context, options);
        if (!auth.IsAllowed)
        {
            logger.LogWarning("Discord コマンドを拒否しました（User={UserId}・理由={Reason}）。", context.UserId, auth.Reason);
            return PolicyApprovalResult.Denied();
        }

        // 閂2: 解析。`/policy approve <periodKey> <version>` だけ（銘柄は取らない）。
        var command = BotCommandParser.Parse(context.RawCommand);
        if (command is not { Kind: BotCommandKind.PolicyApprove, PeriodKey: { } key, Version: { } version })
        {
            logger.LogWarning("方針の改訂の確定として解釈できないコマンドを拒否しました（Actor={Actor}）。", auth.Actor);
            return PolicyApprovalResult.Denied();
        }

        // ① 確定（既存の経路。版番号ガード・OnBehalfOf・二重押下の吸収は ReportCommandHandler が持つ）。
        var confirm = await reportHandler
            .HandleAsync(context with { RawCommand = $"/report approve {key} {version}" }, cancellationToken)
            .ConfigureAwait(false);
        if (!confirm.ConfirmedNow)
        {
            return new PolicyApprovalResult(
                Fit(confirm.Message + "\n監視銘柄の入れ替えは適用していません（この操作では確定していないため）。"),
                ConfirmedNow: false, WatchlistApplyStatus: null);
        }

        // ② 確定した版の入れ替え案（報告書サービスの台帳）。
        var lookup = await policies.GetWatchlistProposalAsync(key, version, cancellationToken).ConfigureAwait(false);
        if (!lookup.Succeeded)
        {
            logger.LogWarning("確定した版の入れ替え案を照会できませんでした（PeriodKey={PeriodKey}・版={Version}）。", key, version);
            return Done(confirm.Message, $"監視銘柄の入れ替え案を照会できなかったため、適用していません（{lookup.Message}）。"
                + "適用する場合は設定画面から変更してください。", null);
        }

        // 案が無い（/policy の案でない）・この版で確定されていない（PR #1027 の監査 H1。報告書サービスが 409）・入れ替え無し。
        if (lookup.Proposal is not { } proposal)
            return Done(confirm.Message, $"監視銘柄の入れ替え案はありません（{lookup.Message}）。", null);
        if (proposal.Changes.Count == 0)
            return Done(confirm.Message, "監視銘柄の入れ替え案はありません。", null);

        if (proposal.ApplyRecorded)
            return Done(confirm.Message, "この案の入れ替えは既に適用の記録があります（二重に適用しません）。", null);

        var actor = auth.Actor!;
        if (proposal.Snapshot is not { } snapshot)
        {
            const string unknown = "案を作った時点の監視銘柄が分からないため、入れ替えを適用していません。適用する場合は設定画面から変更してください。";
            await RecordAsync(proposal, "snapshot-unknown", [], unknown, actor, cancellationToken).ConfigureAwait(false);
            return Done(confirm.Message, unknown, null);
        }

        // ③ 適用（案の銘柄だけ・案を作った時点の監視銘柄を期待値として）。
        var apply = await watchlist
            .ApplyProposalAsync(snapshot, proposal.Changes, $"{key}-v{version}", actor, cancellationToken)
            .ConfigureAwait(false);

        var (outcome, text) = apply.Status switch
        {
            WatchlistApplyStatus.Applied => ("applied", Breakdown(apply)),
            WatchlistApplyStatus.Stale => ("stale", apply.Message),
            WatchlistApplyStatus.Rejected => ("rejected", "入れ替えを適用できませんでした: " + apply.Message),
            _ => ("indeterminate", apply.Message),
        };

        logger.LogInformation(
            "入れ替え案の適用（Actor={Actor}・PeriodKey={PeriodKey}・版={Version}・結果={Outcome}・適用={Applied}・適用せず={Skipped}）。",
            actor, key, version, outcome, apply.Items.Count(i => i.Applied), apply.Items.Count(i => !i.Applied));

        // ④ 内訳を台帳へ（監査）。記録に失敗しても適用は巻き戻さない（利用者には見せる）。
        var recorded = await RecordAsync(proposal, outcome, apply.Items, text, actor, cancellationToken).ConfigureAwait(false);
        return Done(confirm.Message, recorded ? text : text + "\n（内訳の記録に失敗しました。監視銘柄の変更履歴は設定画面で確認できます）", apply.Status);
    }

    private async Task<bool> RecordAsync(
        WatchlistProposalDetail proposal, string outcome, IReadOnlyList<WatchlistApplyItemView> items, string message, string actor,
        CancellationToken cancellationToken) =>
        await policies.RecordWatchlistApplyAsync(proposal.AttemptId, outcome, items, message, actor, cancellationToken)
            .ConfigureAwait(false);

    // 適用の内訳（適用した銘柄・適用しなかった銘柄と理由・ADR-0031 の推定〔警告のみ〕）。
    internal static string Breakdown(WatchlistApplyOutcome apply)
    {
        var sb = new StringBuilder("【監視銘柄の入れ替え】");
        var applied = apply.Items.Count(i => i.Applied);
        sb.Append($"適用 {applied} 件・適用せず {apply.Items.Count - applied} 件");
        foreach (var item in apply.Items)
        {
            sb.Append($"\n- {PolicyRevisionMessage.ActionLabel(item.Action)} {item.Symbol}: ");
            sb.Append(item.Applied ? "適用しました" : $"適用しませんでした（{item.SkipReason ?? "理由不明"}）");
        }

        if (apply.Estimate is { } e)
        {
            sb.Append($"\nFinnhub の推定 {e.EstimatedDailyRequests:N0} 回/日");
            sb.Append(e.Exceeds
                ? $"（暫定上限 {e.ProvisionalDailyLimit:N0} 回/日を超過・警告のみ）"
                : $"（暫定上限 {e.ProvisionalDailyLimit:N0} 回/日以内）");
        }

        return sb.ToString();
    }

    private static PolicyApprovalResult Done(string confirmMessage, string watchlistText, WatchlistApplyStatus? status) =>
        new(Fit(confirmMessage + "\n" + watchlistText), ConfirmedNow: true, status);

    private static string Fit(string text) => text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
}

// `/policy` の確認ボタンの結果。ConfirmedNow はこの要求で確定したか。WatchlistApplyStatus は適用を試みたときだけ非 null。
public sealed record PolicyApprovalResult(string Message, bool ConfirmedNow, WatchlistApplyStatus? WatchlistApplyStatus, bool IsDenied = false)
{
    public static PolicyApprovalResult Denied() =>
        new("この操作は実行されませんでした（許可されていません）。", false, null, IsDenied: true);
}
