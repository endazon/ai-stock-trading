using RiskManagementService.Common.Abstractions;

namespace RiskManagementService.Features.RiskManagement.GetWorkingEntryOrders;

// FR-04, FR-10, ADR-0003, #934, IADR-0390 決定1: 当日の未約定の新規建て注文を、統制（IADR-0346）と**同じ入力・同じ純関数**
// から導く。注文源（IWorkingEntryOrderSource）と台帳の約定（残数量の差し引き）を読み、PortfolioProjection.ProjectWorkingEntries
// に委ねる。走査の下限は LedgerPortfolioStateProvider と同じ 2 日（当日の判定は純関数が市場の現地取引日で行う）。
public sealed class WorkingEntryOrdersService(
    IPortfolioLedgerStore ledger,
    IWorkingEntryOrderSource workingEntryOrders,
    IClock clock)
{
    private static readonly TimeSpan WorkingEntryLookback = TimeSpan.FromDays(2);

    public IReadOnlyList<WorkingEntryOrderView> Build()
    {
        var now = clock.UtcNow;
        var working = workingEntryOrders.GetWorkingEntryOrders(now - WorkingEntryLookback);

        return [.. PortfolioProjection.ProjectWorkingEntries(ledger.GetFills(), now, working)
            .Select(w => new WorkingEntryOrderView(
                w.Order.DecisionId,
                w.Order.Symbol,
                w.Order.Market,
                w.Order.Side,
                w.Remaining,
                w.Order.Price,
                w.Order.ApprovedAt))];
    }
}
