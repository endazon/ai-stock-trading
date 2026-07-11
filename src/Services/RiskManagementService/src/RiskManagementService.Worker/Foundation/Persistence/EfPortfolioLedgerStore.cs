using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.RiskManagement.Worker.Foundation.Persistence;

// FR-10, FR-05, IADR-0018: 取引台帳の EF 実装（追記専用・専有 DB）。承認は DecisionId、約定は OrderId で冪等。
internal sealed class EfPortfolioLedgerStore(RiskManagementDbContext db) : IPortfolioLedgerStore
{
    public void AppendApproval(Guid decisionId, OrderIntent intent, DateTimeOffset approvedAt)
    {
        ArgumentNullException.ThrowIfNull(intent);

        // 冪等: 既に承認済みの DecisionId は無視する（MassTransit 再送）。
        if (db.ApprovedOrders.Find(decisionId) is not null)
            return;

        db.ApprovedOrders.Add(new ApprovedOrderRow
        {
            DecisionId = decisionId,
            Symbol = intent.Symbol,
            Market = intent.Market,
            Side = intent.Side,
            ProductType = intent.ProductType,
            PositionEffect = intent.PositionEffect,
            Mode = intent.Mode,
            Quantity = intent.Quantity,
            Price = intent.Price,
            StopLossPrice = intent.StopLossPrice,
            ApprovedAt = approvedAt,
        });
        db.SaveChanges();
    }

    public bool AppendFill(Guid decisionId, string orderId, int filledQuantity, decimal averagePrice, DateTimeOffset executedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderId);

        // 相関する承認 Intent が無ければ記録しない（銘柄・方向を補完できないため）。
        if (db.ApprovedOrders.Find(decisionId) is null)
            return false;

        // 冪等: 同一 OrderId の再送は無視する。
        if (db.TradeFills.Find(orderId) is not null)
            return true;

        db.TradeFills.Add(new TradeFillRow
        {
            OrderId = orderId,
            DecisionId = decisionId,
            FilledQuantity = filledQuantity,
            AveragePrice = averagePrice,
            ExecutedAt = executedAt,
        });
        db.SaveChanges();
        return true;
    }

    public IReadOnlyList<LedgerFill> GetFills()
    {
        // 約定 × 承認 Intent を DecisionId で結合し、銘柄・方向・建玉効果を補完して射影入力を返す。
        var query =
            from f in db.TradeFills
            join a in db.ApprovedOrders on f.DecisionId equals a.DecisionId
            select new LedgerFill(
                a.Symbol, a.Market, a.Side, a.PositionEffect,
                f.FilledQuantity, f.AveragePrice, f.ExecutedAt, a.StopLossPrice);

        return query.ToList();
    }
}
