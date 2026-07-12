using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.MarketMonitor.Worker.Tests;

internal sealed class FakeClock(DateTimeOffset utcNow) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;
}

internal sealed class FakeSchedule(bool open) : IMarketSchedule
{
    public bool Open { get; set; } = open;

    public bool IsOpen(DateTimeOffset instant) => Open;
}

internal sealed class FakeMarketDataSource : IMarketDataSource
{
    private readonly Dictionary<(string, Market), decimal> _prices = [];

    public FakeMarketDataSource Set(string symbol, Market market, decimal price)
    {
        _prices[(symbol, market)] = price;
        return this;
    }

    public Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken cancellationToken = default)
        => Task.FromResult(_prices.TryGetValue((symbol, market), out var p)
            ? new Quote(symbol, market, p, DateTimeOffset.UtcNow)
            : null);
}
