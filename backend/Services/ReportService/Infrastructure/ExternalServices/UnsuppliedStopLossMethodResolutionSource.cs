using ReportService.Domain;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-10, #1002, IADR-0429 決定4: 監査台帳へ結線されていない構成の安全既定。**常に null（未供給）**。
//
// 🔴 空の記録を返さない。承認がある日に「解決結果の記録が見つからない」と書かれ、結線を忘れたことが
// 発注執行の未処理と同じに見えてしまう。
public sealed class UnsuppliedStopLossMethodResolutionSource : IStopLossMethodResolutionSource
{
    public Task<StopLossMethodResolutionFeed?> GetResolutionsAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
        Task.FromResult<StopLossMethodResolutionFeed?>(null);
}
