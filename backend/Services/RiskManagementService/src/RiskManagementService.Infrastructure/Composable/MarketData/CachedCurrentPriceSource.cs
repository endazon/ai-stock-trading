using RiskManagementService.Application.Ports;
using RiskManagementService.Application.State;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using Microsoft.Extensions.Options;

namespace RiskManagementService.Infrastructure.MarketData;

// FR-10, #81, IADR-0066: 手元に補充済みの現在値（QuoteCache）を同期に読み出す ICurrentPriceSource 実装。
// 発注判断の同期経路から呼ばれるため、ここではネットワーク I/O を行わない（補充は QuoteRefreshService が非同期に行う）。
// 保持期限を超えた前回値は取得不可として扱い、キーを落とす＝当該建玉の含みは 0（安全側・IADR-0066）。
internal sealed class CachedCurrentPriceSource(
    QuoteCache cache,
    IClock clock,
    IOptions<MarketDataOptions> options) : ICurrentPriceSource
{
    public IReadOnlyDictionary<(string Symbol, Market Market), decimal> GetCurrentPrices(IReadOnlyList<OpenPosition> positions)
    {
        ArgumentNullException.ThrowIfNull(positions);

        var maxStaleness = TimeSpan.FromSeconds(Math.Max(1, options.Value.MaxQuoteStalenessSeconds));
        var now = clock.UtcNow;
        var prices = new Dictionary<(string Symbol, Market Market), decimal>();

        foreach (var position in positions)
        {
            var quote = cache.GetFresh(position.Symbol, position.Market, now, maxStaleness);
            if (quote is not null)
                prices[(position.Symbol, position.Market)] = quote.Price;
        }

        return prices;
    }
}
