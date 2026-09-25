using ReportService.Domain;

namespace ReportService.Features.Reports;

// FR-06, FR-10, ADR-0040 決定1, #1002, IADR-0429 決定4: 日報の「実際に適用された手法（発注執行の解決結果）」と
// 月報 §6 の日数ベースの内訳の供給（監査台帳の StopLossMethodResolved）。
//
// 🔴 **供給不達は `null`（未供給）へ倒す。** 空の記録へ倒すと、承認はあるのに「解決結果の記録が見つからない」と
// 書かれ、照会できなかったことと区別できない。
public interface IStopLossMethodResolutionSource
{
    /// <summary>
    /// JST 取引日 [from, to] の承認に属し得る解決結果（照会の窓は前後 1 日を含む。照合は呼び出し側が DecisionId で行う）。
    /// 照会できなければ null（未供給）。
    /// </summary>
    Task<StopLossMethodResolutionFeed?> GetResolutionsAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default);
}
