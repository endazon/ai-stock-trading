using ReportService.Features.Reports;
using ReportService.Domain;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-10, #823, IADR-0422 決定3: 監査台帳へ結線されていない構成の安全既定。**常に null（未供給）**。
//
// 🔴 空の集計（＝新規建ての承認なし）を返さない。承認は本番で実際に起きるため、結線を忘れた日が
// 「承認なし」で通ってしまう。
public sealed class UnsuppliedStopLossMethodUsageSource : IStopLossMethodUsageSource
{
    public Task<StopLossMethodUsage?> GetUsageAsync(
        DateOnly fromInclusive, DateOnly toInclusive, CancellationToken cancellationToken = default) =>
        Task.FromResult<StopLossMethodUsage?>(null);
}
