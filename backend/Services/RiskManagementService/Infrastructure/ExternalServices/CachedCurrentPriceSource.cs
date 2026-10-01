using RiskManagementService.Features.RiskManagement;
using RiskManagementService.Common.Abstractions;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;
using Microsoft.Extensions.Options;

namespace RiskManagementService.Infrastructure.ExternalServices;

// FR-10, #81, IADR-0066: 手元に補充済みの現在値（QuoteCache）を同期に読み出す ICurrentPriceSource 実装。
// 発注判断の同期経路から呼ばれるため、ここではネットワーク I/O を行わない（補充は QuoteRefreshService が非同期に行う）。
// 保持期限を超えた前回値は取得不可として扱い、キーを落とす＝当該建玉の含みは 0（安全側・IADR-0066）。
// FR-10, #1131, IADR-0473: 保持期限は QuoteSessionFreshness の起点から数える —— 閉場中に引いた値は次の開場から、
// 場中に引いた値は取得時刻から（従来どおり）。補充が閉場中は止まるため、取得時刻から数えると夜通し価格を失う。
public sealed class CachedCurrentPriceSource(
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
            if (cache.GetEntry(position.Symbol, position.Market) is { } entry
                && QuoteSessionFreshness.IsFresh(position.Market, entry.FetchedAt, now, maxStaleness))
                prices[(position.Symbol, position.Market)] = entry.Quote.Price;
        }

        return prices;
    }
}
