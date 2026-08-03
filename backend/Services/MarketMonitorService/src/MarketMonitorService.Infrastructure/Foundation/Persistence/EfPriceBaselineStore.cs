using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.MarketMonitor.Infrastructure.Foundation.Persistence;

// FR-03: 基準値（前回判断時点価格）ストアの EF 実装（(Symbol, Market) キー）。
internal sealed class EfPriceBaselineStore(MarketMonitorDbContext db) : IPriceBaselineStore
{
    public decimal? GetBaseline(string symbol, Market market)
    {
        var row = db.PriceBaselines.Find(symbol, market);
        return row?.Price;
    }

    public void SetBaseline(string symbol, Market market, decimal price)
    {
        var row = db.PriceBaselines.Find(symbol, market);
        if (row is null)
        {
            db.PriceBaselines.Add(new PriceBaselineRow
            {
                Symbol = symbol,
                Market = market,
                Price = price,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        else
        {
            row.Price = price;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }

        db.SaveChanges();
    }
}
