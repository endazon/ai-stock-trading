using ReportService.Common.Abstractions;
using ReportService.Domain;

namespace ReportService.Features.Reports;

// 🔴 FR-07, UC-03, #1423 M-6, ADR-0075 決定1〜4, IADR-0583: 日報の無応答の判定（方針の照会と通知の常駐が同じ答えを見る）。
//
// - 承認待ち（IADR-0583 決定2）: 最新の確定済み日報より**開始日が新しい**未確定の日報。確定済みの日報の再確定（`/policy` の改訂の下書き。
//   開始日は効いている日報と同じ）は含めない。確定済みの日報が 1 件も無いときは、未確定の日報があればそれを承認待ちとする。
// - 期限（決定1）: その日報の生成境界より後の最初の米国市場の開場（ReportNoResponsePolicy.DeadlineOf）。
// - 結果は ReportNoResponsePolicy.Decide に従う（期限内＝応答待ち・期限超過＝設定どおり。既定は継続）。
public sealed class DailyReportNoResponseEvaluator(IReportStore store, IClock clock, ReportNoResponseSettings settings)
{
    /// <summary>
    /// 設定が「停止」か。継続（既定）なら停止の印は立ち得ないので、方針の照会はこれを見て評価（2 回の照会）を省く
    /// （PR #1462 の AI レビュー。通知の常駐は継続でも評価する）。
    /// </summary>
    public bool HaltConfigured => settings.Behavior == NoResponseBehavior.Halt;

    public DailyReportNoResponseStatus Evaluate()
    {
        var pending = store.GetLatestUnconfirmed(ReportKind.Daily);
        if (pending is not null
            && store.GetLatestConfirmed(ReportKind.Daily) is { } confirmed
            && pending.PeriodStart <= confirmed.Report.PeriodStart)
        {
            pending = null; // 効いている日報（以前）の再確定の下書きは「無応答」ではない。
        }

        if (pending is null || ReportNoResponsePolicy.DeadlineOf(pending.PeriodStart, settings.Schedule) is not { } deadline)
            return new DailyReportNoResponseStatus(NoResponseOutcome.ContinueLastConfirmed, null, null, Overdue: false, settings.Behavior);

        var outcome = ReportNoResponsePolicy.Decide(clock.UtcNow, deadline, hasPendingReview: true, settings.Behavior);
        return new DailyReportNoResponseStatus(
            outcome, pending, deadline, Overdue: outcome != NoResponseOutcome.AwaitingApproval, settings.Behavior);
    }
}

/// <summary>FR-07, #1423 M-6, IADR-0583: 無応答時の設定（`Reports:NoResponseBehavior`）と、期限を数える日報の生成境界。</summary>
public sealed record ReportNoResponseSettings(NoResponseBehavior Behavior, ReportScheduleOptions Schedule);

/// <summary>
/// FR-07, #1423 M-6, IADR-0583: 無応答の判定結果。<see cref="Overdue"/> は承認待ちの日報が期限を過ぎたこと（継続・停止を問わない。通知の条件）。
/// <see cref="Halted"/> は「停止」が効いていること（取引判断が全体を見送る条件）。
/// </summary>
public sealed record DailyReportNoResponseStatus(
    NoResponseOutcome Outcome, ReportPeriodKeyItem? Pending, DateTimeOffset? Deadline, bool Overdue, NoResponseBehavior Behavior)
{
    public bool Halted => Outcome == NoResponseOutcome.Halt;
}
