using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain.Manipulation;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.EntityFrameworkCore;

namespace AiStockTrading.RiskManagement.Worker.Foundation.Persistence;

// FR-19, #154, IADR-0067: 相場操縦検知の入力窓を注文アクティビティ射影（order_activity）から供給する EF 実装。
// IOrderActivitySource は同期契約（RiskEvaluator が同期純関数）のため同期で読む。行が無ければ空窓を返し、
// 最小標本ガードにより無嫌疑になる（fail-safe・IADR-0040/0067）。
internal sealed class EfOrderActivitySource(RiskManagementDbContext db) : IOrderActivitySource
{
    public OrderActivityWindow GetRecentActivity(
        string symbol, Market market, DateTimeOffset asOf, TimeSpan lookback)
    {
        var from = asOf - lookback;

        var records = db.OrderActivities
            .Where(r => r.Symbol == symbol && r.Market == market && r.PlacedAt >= from && r.PlacedAt <= asOf)
            .AsEnumerable()
            .Select(r => new OrderActivityRecord
            {
                PlacedAt = r.PlacedAt,
                Side = r.Side,
                Quantity = r.Quantity,
                FilledQuantity = r.FilledQuantity,
                Status = r.Status,
                AmendmentCount = r.AmendmentCount,
                TerminalAt = r.TerminalAt,
            })
            .ToList();

        return records.Count == 0
            ? OrderActivityWindow.Empty(symbol, market, asOf)
            : new OrderActivityWindow { Symbol = symbol, Market = market, AsOf = asOf, Records = records };
    }
}
