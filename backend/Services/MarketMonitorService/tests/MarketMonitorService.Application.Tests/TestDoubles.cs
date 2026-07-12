using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.MarketMonitor.Application.Tests;

// テスト用の固定クロック。
internal sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}

// テスト用の価格ソース。銘柄→価格の辞書で応答し、未登録は null（取得失敗）を返す。
internal sealed class FakeMarketDataSource : IMarketDataSource
{
    private readonly Dictionary<(string, Market), decimal> _prices = [];

    public FakeMarketDataSource Set(string symbol, Market market, decimal price)
    {
        _prices[(symbol, market)] = price;
        return this;
    }

    public Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_prices.TryGetValue((symbol, market), out var price)
            ? new Quote(symbol, market, price, DateTimeOffset.UtcNow)
            : null);
    }
}
