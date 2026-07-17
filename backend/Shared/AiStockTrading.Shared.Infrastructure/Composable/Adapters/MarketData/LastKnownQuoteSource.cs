using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Infrastructure.Composable.Adapters.MarketData;

// FR-10, IADR-0066: 現在値が取得できないときのフォールバック（#81 受け入れ条件「0 もしくは前回値」）を与える
// デコレータ。取得できた値はそのまま返して QuoteCache へ保持し、取得不可（null）のときだけ保持期限以内の前回値を
// 返す。前回値が無い／期限超過なら null＝当該建玉の含みは 0 になる（期限の根拠は QuoteCache を参照）。
//
// 保持先の QuoteCache を注入で共有することで、非同期に補充して同期に読み出す経路（リスク管理の
// QuoteRefreshService → CachedCurrentPriceSource）と、都度取得する経路（報告書）が同じ前回値を使う。
public sealed class LastKnownQuoteSource(
    IMarketDataSource inner,
    QuoteCache cache,
    TimeProvider timeProvider,
    TimeSpan maxStaleness) : IMarketDataSource
{
    public async Task<Quote?> GetLatestQuoteAsync(string symbol, Market market, CancellationToken cancellationToken = default)
    {
        var quote = await inner.GetLatestQuoteAsync(symbol, market, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();

        if (quote is not null)
        {
            cache.Set(quote, now);
            return quote;
        }

        return cache.GetFresh(symbol, market, now, maxStaleness);
    }
}
