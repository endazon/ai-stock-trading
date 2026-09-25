using OrderExecutionService.Features.OrderExecution;
using OrderExecutionService.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.EntityFrameworkCore;

namespace OrderExecutionService.Infrastructure.Persistence;

// FR-05, FR-16: 発注結果ストアの EF 実装（追記中心）。DecisionId は相関キー、OrderId は主キー。
public sealed class EfExecutedOrderStore(OrderExecutionDbContext db) : IExecutedOrderStore
{
    public void Save(ExecutionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        db.ExecutedOrders.Add(new ExecutedOrderRow
        {
            OrderId = record.OrderId,
            DecisionId = record.DecisionId,
            Symbol = record.Symbol,
            Market = record.Market,
            Side = record.Side,
            ProductType = record.ProductType,
            PositionEffect = record.PositionEffect,
            Quantity = record.Quantity,
            PlannedPrice = record.PlannedPrice,
            FilledQuantity = record.FilledQuantity,
            AveragePrice = record.AveragePrice,
            Status = record.Status,
            SlippageRatio = record.SlippageRatio,
            ExecutedAt = record.ExecutedAt,
        });
        db.SaveChanges();
    }

    public IReadOnlyList<ExecutionRecord> GetAll()
    {
        return [.. db.ExecutedOrders
            .OrderByDescending(r => r.ExecutedAt)
            .Select(r => ToRecord(r))];
    }

    public ExecutionRecord? FindByDecisionId(Guid decisionId)
    {
        var row = db.ExecutedOrders.FirstOrDefault(r => r.DecisionId == decisionId);
        return row is null ? null : ToRecord(row);
    }

    // #270, IADR-0113: 約定追跡の対象＝非終端かつ追跡上限内の記録を古い順に返す。
    // 終端判定は OrderStatusLifecycle と同一の集合を列挙する（EF が SQL へ翻訳できる形で書く）。
    public IReadOnlyList<ExecutionRecord> FindPendingSince(DateTimeOffset since, int batchSize)
    {
        return [.. db.ExecutedOrders
            .Where(r => (r.Status == OrderStatus.Accepted || r.Status == OrderStatus.PartiallyFilled)
                && r.ExecutedAt >= since)
            .OrderBy(r => r.ExecutedAt)
            .Take(batchSize)
            .Select(r => ToRecord(r))];
    }

    // FR-10, #958, IADR-0406 決定1: 指定した注文 ID の非終端の記録を古い順に返す（追跡上限は見ない）。
    public IReadOnlyList<ExecutionRecord> FindPendingByOrderIds(IReadOnlyCollection<string> orderIds)
    {
        ArgumentNullException.ThrowIfNull(orderIds);
        if (orderIds.Count == 0)
            return [];

        var ids = orderIds.ToList();
        return [.. db.ExecutedOrders
            .Where(r => (r.Status == OrderStatus.Accepted || r.Status == OrderStatus.PartiallyFilled)
                && ids.Contains(r.OrderId))
            .OrderBy(r => r.ExecutedAt)
            .Select(r => ToRecord(r))];
    }

    // FR-10, #958, IADR-0406 決定3: 非終端の行の追跡の起点を進める。変更追跡により UPDATE は executed_at の 1 列だけになり、
    // 並行に約定追跡が書いた状態・数量を古い値で上書きしない。
    public bool RenewTracking(string orderId, DateTimeOffset trackedFrom)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        var row = db.ExecutedOrders.Find(orderId);
        if (row is null
            || !(row.Status == OrderStatus.Accepted || row.Status == OrderStatus.PartiallyFilled)
            || row.ExecutedAt >= trackedFrom)
        {
            return false;
        }

        row.ExecutedAt = trackedFrom;
        db.SaveChanges();
        return true;
    }

    // #270, IADR-0113: 観測した最新のブローカ状態を既存行へ反映する。行が無ければ何もしない
    // （新規に作らない＝DecisionId 1:1 の不変を壊さない）。
    public bool UpdateOutcome(
        string orderId,
        OrderStatus status,
        int filledQuantity,
        decimal averagePrice,
        decimal slippageRatio,
        DateTimeOffset executedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        var row = db.ExecutedOrders.Find(orderId);
        if (row is null)
            return false;

        row.Status = status;
        row.FilledQuantity = filledQuantity;
        row.AveragePrice = averagePrice;
        row.SlippageRatio = slippageRatio;
        row.ExecutedAt = executedAt;
        db.SaveChanges();
        return true;
    }

    private static ExecutionRecord ToRecord(ExecutedOrderRow r) => new(
        r.DecisionId, r.OrderId, r.Symbol, r.Market, r.Side, r.ProductType, r.PositionEffect,
        r.Quantity, r.PlannedPrice, r.FilledQuantity, r.AveragePrice, r.Status, r.SlippageRatio, r.ExecutedAt);
}
