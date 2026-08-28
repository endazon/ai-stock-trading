using OrderExecutionService.Application.Ports;
using OrderExecutionService.Domain;

namespace OrderExecutionService.Infrastructure.Persistence;

// FR-10, #331, IADR-0210 決定6: 保護逆指値レグ記録ストアの EF 実装（発注執行の専有 DB）。
// Save は EntryDecisionId で upsert（再発注＝試行の置き換え）。
internal sealed class EfProtectiveStopOrderStore(OrderExecutionDbContext db) : IProtectiveStopOrderStore
{
    public void Save(ProtectiveStopOrder stop)
    {
        ArgumentNullException.ThrowIfNull(stop);

        var row = db.ProtectiveStopOrders.Find(stop.EntryDecisionId);
        if (row is null)
        {
            row = new ProtectiveStopOrderRow { EntryDecisionId = stop.EntryDecisionId };
            db.ProtectiveStopOrders.Add(row);
        }

        row.StopDecisionId = stop.StopDecisionId;
        row.StopOrderId = stop.StopOrderId;
        row.Symbol = stop.Symbol;
        row.Market = stop.Market;
        row.EntrySide = stop.EntrySide;
        row.ProductType = stop.ProductType;
        row.Mode = stop.Mode;
        row.Quantity = stop.Quantity;
        row.TriggerPrice = stop.TriggerPrice;
        row.FxRateToBase = stop.FxRateToBase;
        row.Attempt = stop.Attempt;
        row.State = stop.State;
        row.CreatedAt = stop.CreatedAt;
        row.UpdatedAt = stop.UpdatedAt;
        db.SaveChanges();
    }

    public ProtectiveStopOrder? Find(Guid entryDecisionId)
    {
        var row = db.ProtectiveStopOrders.Find(entryDecisionId);
        return row is null ? null : ToDomain(row);
    }

    public IReadOnlyList<ProtectiveStopOrder> FindActive(int batchSize) =>
        db.ProtectiveStopOrders
            .Where(r => r.State == ProtectiveStopState.Active)
            .OrderBy(r => r.CreatedAt)
            .Take(batchSize)
            .ToList()
            .Select(ToDomain)
            .ToList();

    private static ProtectiveStopOrder ToDomain(ProtectiveStopOrderRow r) =>
        new(r.EntryDecisionId, r.StopDecisionId, r.StopOrderId, r.Symbol, r.Market, r.EntrySide,
            r.ProductType, r.Mode, r.Quantity, r.TriggerPrice, r.FxRateToBase, r.Attempt, r.State,
            r.CreatedAt, r.UpdatedAt);
}
