using AiStockTrading.RiskManagement.Application.Adapters;
using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Worker.Foundation.Persistence;

// FR-19, #154, IADR-0067: 注文アクティビティ射影ストアの EF 実装（Risk 専有 DB）。承認で行を作り、
// 約定・訂正・取消で DecisionId 相関の既存行を更新する。相関する承認が無いイベントは無視する（射影の一貫性）。
internal sealed class EfOrderActivityStore(RiskManagementDbContext db) : IOrderActivityStore
{
    public void RecordPlacement(
        Guid decisionId, string symbol, Market market, TradeSide side, int quantity, DateTimeOffset placedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);

        // 冪等: 既に承認済みの DecisionId は無視する（MassTransit 再送）。
        if (db.OrderActivities.Find(decisionId) is not null)
            return;

        db.OrderActivities.Add(new OrderActivityRow
        {
            DecisionId = decisionId,
            Symbol = symbol,
            Market = market,
            Side = side,
            PlacedAt = placedAt,
            Quantity = quantity,
            FilledQuantity = 0,
            Status = OrderStatus.Accepted,
            AmendmentCount = 0,
            TerminalAt = null,
        });
        db.SaveChanges();
    }

    public void RecordExecution(Guid decisionId, OrderStatus status, int filledQuantity, DateTimeOffset executedAt)
    {
        if (db.OrderActivities.Find(decisionId) is not { } row)
            return;

        row.Status = status;
        row.FilledQuantity = filledQuantity;
        if (OrderActivityProjection.IsTerminal(status))
            row.TerminalAt = executedAt;
        db.SaveChanges();
    }

    public void RecordModification(Guid decisionId, int quantity, DateTimeOffset modifiedAt)
    {
        if (db.OrderActivities.Find(decisionId) is not { } row)
            return;

        row.AmendmentCount++;
        row.Quantity = quantity;
        db.SaveChanges();
    }

    public void RecordCancellation(Guid decisionId, DateTimeOffset cancelledAt)
    {
        if (db.OrderActivities.Find(decisionId) is not { } row)
            return;

        row.Status = OrderStatus.Cancelled;
        row.TerminalAt = cancelledAt;
        db.SaveChanges();
    }
}
