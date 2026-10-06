using AiStockTrading.Shared.Contracts.Trading;
using ReportService.Domain;
using ReportService.Features.Reports;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-16, #1181, IADR-0493 決定 4: リスク管理サービスの所在が構成されていないときの既定。**常に null（未供給）**。
// 🔴 **空列へ倒さない**（空列は「期間開始時点で建玉なし」という主張になる。UnsuppliedPeriodDriftAdoptionSource と同じ向き）。
public sealed class UnsuppliedOpeningInventorySource : IOpeningInventorySource
{
    public Task<IReadOnlyList<OpeningLot>?> GetOpeningInventoryAsync(
        Market market, DateOnly beforeTradingDay, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OpeningLot>?>(null);
}
