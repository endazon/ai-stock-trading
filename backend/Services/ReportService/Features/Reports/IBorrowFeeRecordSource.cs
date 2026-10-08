using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, #338, ADR-0016 決定15, ADR-0027 決定1・決定4, 04_report-templates 月報 §6.1 / 日報 §4, IADR-0254:
// 当期間の借株料の記録の供給（計上できた日と、料率が取れず未計上だった日）。
//
// 🔴 **供給不達は `null`（未供給）へ倒す。** 「借株コスト 0 USD」と書けば費用が無かったと読める。
//
// FR-06, #1224, IADR-0516 決定 3: [from, to] は**照会の範囲**（報告書のセッションの窓を覆う JST の暦日の外包）であり、報告書に載る範囲ではない。
// 報告書サービスが受け取った後に窓で絞る（ReportLedgerWindowing）。
public interface IBorrowFeeRecordSource
{
    /// <summary>JST 取引日 [from, to] の借株料の記録。照会できなければ null（未供給）。</summary>
    Task<BorrowFeeRecord?> GetBorrowFeesAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default);
}
