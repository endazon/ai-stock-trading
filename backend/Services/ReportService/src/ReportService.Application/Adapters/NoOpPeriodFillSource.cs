using ReportService.Application.Ports;
using ReportService.Domain;

namespace ReportService.Application.Adapters;

// FR-06, FR-16, IADR-0115 決定5, #280: 約定供給の安全既定（no-op）。権威源（リスク管理の取引台帳）への
// s2s 照会が構成されていないときはこれが選ばれ、数値 0 の報告書が生成される（生成そのものは止めない）。
public sealed class NoOpPeriodFillSource : IPeriodFillSource
{
    public Task<IReadOnlyList<PeriodTradeFill>> GetFillsAsync(
        DateOnly fromInclusive,
        DateOnly toInclusive,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PeriodTradeFill>>([]);
}
