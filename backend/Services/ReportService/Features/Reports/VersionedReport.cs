using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-07: 報告書と楽観排他用のバージョン。
public sealed record VersionedReport(TradingReport Report, int Version)
{
    /// <summary>
    /// FR-07, FR-13, #1025, IADR-0433 決定 7（PR #1027 の監査 H1）: この報告書が**下書きの版 <paramref name="draftVersion"/> で**確定されたか。
    /// 確定は版を 1 進める（Draft v → Confirmed v+1）ため、確定済みかつ現在の版が <c>draftVersion + 1</c> のときだけ真。
    /// 🔴 同じ会話キーの別の版（古い確認ボタン）で確定されたものを「この版で確定された」と読まない。
    /// </summary>
    public bool IsConfirmedAtDraftVersion(int draftVersion) =>
        Report.State == ReportState.Confirmed && Version == draftVersion + 1;
}
