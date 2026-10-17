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
        Classify(store, row) == RevisionDraftState.Stale;

    /// <summary>
    /// FR-07, FR-08, ADR-0068 決定1・6, #1373, IADR-0549 決定3・5: 行が版 n+1 のドラフトか、そうならまだ効いているか。
    /// 🔴 `/policy`・作り直し・確定時の写しの失効・KB の入れ直しが<b>同じ判定</b>を使う（効いていれば承認待ちの写しを残し、
    /// 効いていなければ失効の印を付ける）。
    /// </summary>
    public static RevisionDraftState Classify(IReportStore store, VersionedReport row)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(row);

        if (ConfirmedBeforeRevision(store, row) is null)
            return RevisionDraftState.NotRevision;
        return IsEffective(store, row.Report.PeriodKey) ? RevisionDraftState.Pending : RevisionDraftState.Stale;
    }
}

/// <summary>FR-07, ADR-0068 決定1, #1373, IADR-0549: 行と版 n+1 のドラフトの関係。</summary>
public enum RevisionDraftState
{
    /// <summary>版 n+1 のドラフトではない（確定済みの行・確定版の無いドラフト）。</summary>
    NotRevision,

    /// <summary>版 n+1 のドラフトで、その日報はまだ効いている（承認待ち。確定できる）。</summary>
    Pending,

    /// <summary>版 n+1 のドラフトで、効いている日報が移った（確定できない。記録として残す）。</summary>
    Stale,
}
