using System.Collections.Concurrent;
using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;

// FR-10, #257, #364, IADR-0107 決定5: 「そのレートを取りに行くか」「そのレートを使ってよいか」を決める装飾。
//   - TTL: 取得済みレートの再利用時間。日次系列（DEXJPUS）を判断サイクルごとに叩かない。
//   - 鮮度上限: 観測日が古すぎるレートは採らない（null＝レート無し＝非基準通貨の新規建て見送り）。
// 失敗（null）・鮮度切れはキャッシュしない。一時障害を TTL のあいだ引きずると、回復後も見送りが続くため。
internal sealed class CachingFxRateSource(
    IFxRateSource inner,
    TimeSpan ttl,
    TimeSpan maxRateAge,
    TimeProvider timeProvider,
    ILogger<CachingFxRateSource> logger)
    : IFxRateSource
{
    private readonly ConcurrentDictionary<Currency, (FxRate Rate, DateTimeOffset FetchedAt)> _cache = new();

    public async Task<FxRate?> GetRateToBaseAsync(Currency quote, CancellationToken cancellationToken = default)
    {
        // ポート契約: 基準通貨は外部へ問い合わせず必ずレート 1（観測ではないため鮮度判定もしない）。
        if (quote == MarketCurrency.Base)
            return FxRate.Identity(quote);

        var now = timeProvider.GetUtcNow();
        if (_cache.TryGetValue(quote, out var cached) && now - cached.FetchedAt < ttl)
            return cached.Rate;

        var rate = await inner.GetRateToBaseAsync(quote, cancellationToken).ConfigureAwait(false);
        if (rate is null)
            return null;

        if (now - rate.AsOf > maxRateAge)
        {
            logger.LogWarning(
                "為替レートの観測が古いため採用しません（{Quote}: 観測日 {AsOf} / 上限 {MaxAgeDays} 日）。" +
                "当該通貨建て銘柄の新規建ては見送られます（IADR-0107）。",
                quote, rate.AsOf, maxRateAge.TotalDays);
            return null;
        }

        _cache[quote] = (rate, now);
        return rate;
    }
}
