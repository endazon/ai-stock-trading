using AiStockTrading.OrderExecution.Application.Ports;
using AiStockTrading.OrderExecution.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.OrderExecution.Worker.Foundation.Persistence;

// FR-05, FR-16: 発注結果ストアの EF 実装（追記中心）。DecisionId は相関キー、OrderId は主キー。
internal sealed class EfExecutedOrderStore(OrderExecutionDbContext db) : IExecutedOrderStore
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
            .Select(r => new ExecutionRecord(
                r.DecisionId, r.OrderId, r.Symbol, r.Market, r.Side, r.ProductType, r.PositionEffect,
                r.Quantity, r.PlannedPrice, r.FilledQuantity, r.AveragePrice, r.Status, r.SlippageRatio, r.ExecutedAt))];
    }
}
