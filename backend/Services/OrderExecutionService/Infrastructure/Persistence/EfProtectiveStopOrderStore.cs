using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Infrastructure.Persistence;

// FR-10, #331, IADR-0210 決定6: 保護逆指値レグ記録ストアの EF 実装（発注執行の専有 DB）。
// Save は EntryDecisionId で upsert（再発注＝試行の置き換え）。
public sealed class EfProtectiveStopOrderStore(OrderExecutionDbContext db) : IProtectiveStopOrderStore
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
        row.Mechanism = stop.Mechanism;
        row.TriggeredAt = stop.TriggeredAt;
        row.TriggeredPrice = stop.TriggeredPrice;
        row.RemainingProtected = stop.RemainingProtected;
        row.StalledNotifiedAt = stop.StalledNotifiedAt;
        row.PendingExternalReduction = stop.PendingExternalReduction;
        row.ExternalReductionObservations = stop.ExternalReductionObservations;
        row.ExternalReductionAbsences = stop.ExternalReductionAbsences;
        row.ProtectionSuspendedSince = stop.ProtectionSuspendedSince;
        row.ProtectionSuspendedNotifiedAt = stop.ProtectionSuspendedNotifiedAt;
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

    // #820, IADR-0344 決定1・決定4: 損切りライン到達の突き合わせ対象（Active な S1 の同一銘柄・同一方向）。
    public IReadOnlyList<ProtectiveStopOrder> FindActiveSoftwareStops(string symbol, Market market, TradeSide entrySide) =>
        db.ProtectiveStopOrders
            .Where(r => r.State == ProtectiveStopState.Active
                && r.Mechanism == StopLossExecutionMethod.SoftwareStop
                && r.Symbol == symbol
                && r.Market == market
                && r.EntrySide == entrySide)
            .OrderBy(r => r.CreatedAt)
            .ToList()
            .Select(ToDomain)
            .ToList();

    // #820 の 6 巡目監査・7 巡目監査, IADR-0344 追記(6)・追記(7): 観測を数え続けてよいかの門が読む
    // 「完了済みの S1」（更新が新しい順・上限つき）。
    public IReadOnlyList<ProtectiveStopOrder> FindCompletedSoftwareStops(
        string symbol, Market market, TradeSide entrySide, int limit) =>
        db.ProtectiveStopOrders
            .Where(r => r.State == ProtectiveStopState.Completed
                && r.Mechanism == StopLossExecutionMethod.SoftwareStop
                && r.Symbol == symbol
                && r.Market == market
                && r.EntrySide == entrySide)
            .OrderByDescending(r => r.UpdatedAt)
            .Take(limit)
            .ToList()
            .Select(ToDomain)
            .ToList();

    private static ProtectiveStopOrder ToDomain(ProtectiveStopOrderRow r) =>
        new(r.EntryDecisionId, r.StopDecisionId, r.StopOrderId, r.Symbol, r.Market, r.EntrySide,
            r.ProductType, r.Mode, r.Quantity, r.TriggerPrice, r.FxRateToBase, r.Attempt, r.State,
            r.CreatedAt, r.UpdatedAt, r.Mechanism, r.TriggeredAt, r.TriggeredPrice,
            r.RemainingProtected, r.StalledNotifiedAt,
            r.PendingExternalReduction, r.ExternalReductionObservations,
            r.ExternalReductionAbsences, r.ProtectionSuspendedSince, r.ProtectionSuspendedNotifiedAt);
}
