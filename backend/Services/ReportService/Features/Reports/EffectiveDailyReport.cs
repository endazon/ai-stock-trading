using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-07, FR-14, ADR-0068 決定1, #1373, IADR-0549 決定1・3: 「効いている日報」（最新の確定済み日報＝二段で選んだ期間キー）と、
// 確定済みの日報に版を足したドラフト（版 n+1）の見分け方。`/policy`・`/report regenerate`・レビューの表示が同じ規則で読む（3 箇所に書かない）。
// 🔴 ここでの判定は案内と早めの拒否のためのもの。**確定の可否は保存の中で判定し直す**（IReportStore.Confirm・StartRevisionDraft）。
public static class EffectiveDailyReport
{
    /// <summary>いま効いている日報の期間キー（確定済み日報が無ければ null）。</summary>
    public static string? EffectivePeriodKey(IReportStore store) =>
        store.GetLatestConfirmed(ReportKind.Daily)?.Report.PeriodKey;

    /// <summary>この期間キーの日報が効いている日報か。</summary>
    public static bool IsEffective(IReportStore store, string periodKey) =>
        string.Equals(EffectivePeriodKey(store), periodKey, StringComparison.Ordinal);

    /// <summary>
    /// 未確定の行が、確定済みの日報に版を足したドラフト（版 n+1）なら、それまでの確定版を返す。そうでなければ null。
    /// （同じ期間キーに確定版の写しがあり、行が Draft であること。）
    /// </summary>
    public static VersionedReport? ConfirmedBeforeRevision(IReportStore store, VersionedReport row) =>
        row.Report.State == ReportState.Draft ? store.GetLatestConfirmedVersion(row.Report.PeriodKey) : null;

    /// <summary>版 n+1 のドラフトで、効いている日報が移った後のもの（確定・改訂・作り直しをすべて断る）。</summary>
    public static bool IsStaleRevisionDraft(IReportStore store, VersionedReport row) =>
        ConfirmedBeforeRevision(store, row) is not null && !IsEffective(store, row.Report.PeriodKey);
}
