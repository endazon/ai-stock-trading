namespace NotificationService.Features.Notifications;

// FR-07, FR-14, UC-03〜05, ADR-0003, #1016, IADR-0431: 報告書サービスの方針の改訂（`POST /reports/policy-revisions`）の抽象。
// 通知サービスは報告書の状態を持たない（権威は報告書サービス側）。報告書レビュー（IReportReviewController）と同型。
//
// 🔴 **監視銘柄を変える口は持たない。** 入れ替え案の適用は市場監視サービスの口（`IMarketMonitorWatchlistController`）が行い、
// ここは案の照会と適用の内訳の記録だけを持つ（ADR-0042 決定 1。`DiscordSettingsAreReadOnlyTests` が固定する）。
public interface IPolicyRevisionController
{
    // 改訂案を作らせる（報告書サービスが新しい版として保存・提示する。確定はしない）。
    // periodKey は null で当日（JST）の日報。onBehalfOf は多層認証が解決した操作者（改訂者として本文に残る）。
    // FR-13, ADR-0042 決定 1, #1025: currentWatchlist は照会した現在の監視銘柄（案の土台・適用の楽観排他の基準）。null＝照会できなかった。
    Task<PolicyRevisionCommandOutcome> ReviseAsync(
        string? periodKey,
        string instruction,
        string onBehalfOf,
        IReadOnlyList<WatchlistSnapshotItemView>? currentWatchlist,
        CancellationToken cancellationToken = default);

    // FR-13, ADR-0042 決定 1, #1025: 確定した版の入れ替え案（報告書サービスの台帳）。その版が /policy の案でなければ Found=false。
    Task<WatchlistProposalLookup> GetWatchlistProposalAsync(
        string periodKey, int version, CancellationToken cancellationToken = default);

    // FR-13, ADR-0042 決定 1, #1025: 適用の内訳を記録する（監査。1 回だけ）。記録できなければ false。
    Task<bool> RecordWatchlistApplyAsync(
        Guid attemptId,
        string outcome,
        IReadOnlyList<WatchlistApplyItemView> items,
        string message,
        string onBehalfOf,
        CancellationToken cancellationToken = default);
}

// 案の照会の結果。Succeeded=false は照会の失敗（案の有無が分からない）。Found=false はその版が /policy の案ではない。
public sealed record WatchlistProposalLookup(bool Succeeded, bool Found, WatchlistProposalDetail? Proposal, string Message);

// 確定した版の案。Snapshot が null なら案を作った時点の監視銘柄が分からない（適用しない）。
public sealed record WatchlistProposalDetail(
    Guid AttemptId,
    string PeriodKey,
    int ReportVersion,
    IReadOnlyList<WatchlistChangeSuggestionView> Changes,
    IReadOnlyList<WatchlistSnapshotItemView>? Snapshot,
    bool ApplyRecorded);

// FR-07, #1016: 改訂の結果。
// Succeeded=false は**案が保存されていない**か、**保存されたか分からない**（Indeterminate=true。タイムアウト等）。
// 失敗を成功に見せない・不明を失敗に見せない（原則 A）。Message は利用者へそのまま見せる文。
public sealed record PolicyRevisionCommandOutcome(
    bool Succeeded,
    bool Indeterminate,
    string Message,
    PolicyRevisionProposalView? Proposal = null);

// 報告書サービスが保存・提示した案の射影（文字列は発行側で無害化済み・IADR-0116 決定3 と同じ位置）。
public sealed record PolicyRevisionProposalView(
    string PeriodKey,
    int Version,
    bool Created,
    bool Presented,
    string Message,
    string PolicySummary,
    IReadOnlyList<WatchlistChangeSuggestionView> WatchlistChanges,
    string? Rationale);

// 監視銘柄の入れ替え案の 1 件（`/policy` の確認ボタンで確定したときだけ適用される）。Action は "add" / "remove"。
public sealed record WatchlistChangeSuggestionView(string Action, string Symbol, string Reason);
