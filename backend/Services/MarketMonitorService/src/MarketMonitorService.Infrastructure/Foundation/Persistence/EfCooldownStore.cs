using MarketMonitorService.Application.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Infrastructure.Persistence;

// FR-03: クールダウン（銘柄別最終トリガー時刻）ストアの EF 実装（(Symbol, Market) キー）。
internal sealed class EfCooldownStore(MarketMonitorDbContext db) : ICooldownStore
{
    public DateTimeOffset? GetLastTriggered(string symbol, Market market)
    {
        var row = db.Cooldowns.Find(symbol, market);
        return row?.LastTriggeredAt;
    }

    public void SetLastTriggered(string symbol, Market market, DateTimeOffset at)
    {
        var row = db.Cooldowns.Find(symbol, market);
        if (row is null)
        {
            db.Cooldowns.Add(new CooldownRow
            {
                Symbol = symbol,
                Market = market,
                LastTriggeredAt = at,
            });
        }
        else
        {
            row.LastTriggeredAt = at;
        }

        db.SaveChanges();
    }
}
