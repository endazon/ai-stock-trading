using ReportService.Features.Reports;
using ReportService.Domain;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-16, #611, IADR-0286 決定2: 期末レート供給の安全既定（常に null＝未供給）。
// Fx:Provider が未設定・no-op の構成で選ばれる。**空・0 円へ倒さない**——為替差損益 0 円は「為替では損得が無かった」という
// 別の主張であり、供給が無いこととは違う。
public sealed class UnsuppliedPeriodEndFxRateSource : IPeriodEndFxRateSource
{
    public Task<PeriodEndFxRate?> GetRateAsync(DateOnly periodEnd, CancellationToken cancellationToken = default) =>
        Task.FromResult<PeriodEndFxRate?>(null);
}
