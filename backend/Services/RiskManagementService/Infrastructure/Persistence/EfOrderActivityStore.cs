using RiskManagementService.Features.RiskManagement;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Infrastructure.Persistence;

// FR-19, #154, IADR-0067: 注文アクティビティ射影ストアの EF 実装（Risk 専有 DB）。承認で行を作り、
// 約定・訂正・取消で DecisionId 相関の既存行を更新する。相関する承認が無いイベントは無視する（射影の一貫性）。
public sealed class EfOrderActivityStore(RiskManagementDbContext db) : IOrderActivityStore
{
    public void RecordPlacement(
        Guid decisionId, string symbol, Market market, TradeSide side, int quantity, DateTimeOffset placedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);

        // 冪等: 既に承認済みの DecisionId は無視する（ブローカ再送・IADR-0129 決定 10 の根拠のひとつ）。
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

    // FR-10, #829, IADR-0346 決定5: 見送りは Rejected の終端（行が無ければ作る・既に終端なら変えない）。
    public void RecordForgone(
        Guid decisionId, string symbol, Market market, TradeSide side, int quantity, DateTimeOffset forgoneAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(symbol);

        if (db.OrderActivities.Find(decisionId) is { } row)
        {
            if (row.TerminalAt is not null)
                return;

            row.Status = OrderStatus.Rejected;
            row.TerminalAt = forgoneAt;
            db.SaveChanges();
            return;
        }

        db.OrderActivities.Add(new OrderActivityRow
        {
            DecisionId = decisionId,
            Symbol = symbol,
            Market = market,
            Side = side,
            PlacedAt = forgoneAt,
            Quantity = quantity,
            FilledQuantity = 0,
            Status = OrderStatus.Rejected,
            AmendmentCount = 0,
            TerminalAt = forgoneAt,
        });
        db.SaveChanges();
    }
}
