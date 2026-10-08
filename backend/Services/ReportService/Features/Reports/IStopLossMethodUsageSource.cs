using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, FR-10, ADR-0040 決定1, #823, IADR-0422 決定3: 日報 §4「損切りの実行機構（当日）」の供給
// （当期間の新規建ての承認を、承認時点の手法ごとに数えたもの）。
//
// 🔴 **供給不達は `null`（未供給）へ倒す。** 空の集計（承認 0 件）へ倒すと「当日は新規建ての承認が無かった」と読める。
//
// FR-06, #1224, IADR-0516 決定 3: [from, to] は**照会の範囲**（報告書のセッションの窓を覆う JST の暦日の外包）であり、報告書に載る範囲ではない。
// 報告書サービスが受け取った後に窓で絞る（ReportLedgerWindowing）。
public interface IStopLossMethodUsageSource
{
    /// <summary>JST 取引日 [from, to] の新規建ての承認の手法別件数。照会できなければ null（未供給）。</summary>
    Task<StopLossMethodUsage?> GetUsageAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default);
}
