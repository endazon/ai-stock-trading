namespace NotificationService.Features.Notifications;

// FR-07, FR-14, UC-03〜05, ADR-0003, #1016, IADR-0431: 報告書サービスの方針の改訂（`POST /reports/policy-revisions`）の抽象。
// 通知サービスは報告書の状態を持たない（権威は報告書サービス側）。報告書レビュー（IReportReviewController）と同型。
//
// 🔴 **監視銘柄を変える口は持たない。** 改訂案に含まれる監視銘柄の入れ替え案は表示するだけであり、適用は設定画面
// （SC-02）で行う（FR-14「設定値の変更は Discord からは参照のみ」）。`DiscordSettingsAreReadOnlyTests` が固定する。
public interface IPolicyRevisionController
{
    // 改訂案を作らせる（報告書サービスが新しい版として保存・提示する。確定はしない）。
    // periodKey は null で当日（JST）の日報。onBehalfOf は多層認証が解決した操作者（改訂者として本文に残る）。
    Task<PolicyRevisionCommandOutcome> ReviseAsync(
        string? periodKey, string instruction, string onBehalfOf, CancellationToken cancellationToken = default);
}

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

// 監視銘柄の入れ替え案の 1 件（表示のみ）。Action は "add" / "remove"。
public sealed record WatchlistChangeSuggestionView(string Action, string Symbol, string Reason);
