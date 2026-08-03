using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TradeDecision.Application.Ports;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Infrastructure.Composable.Adapters;

// FR-10, FR-17, #257, IADR-0107: 判断側ポート IFxRateProvider を、共有ポート IFxRateSource（Fx:Provider で選択）へ
// 写像するアダプタ。市場 → 取引通貨の導出（MarketCurrency）だけを担い、取得・キャッシュ・鮮度判定はレート源側が持つ。
// fail-safe: レート源が解決できない（未結線・取得失敗・鮮度切れ）ときは null を返し、判断側が新規建てを見送る。
internal sealed class MarketFxRateProvider(IFxRateSource source, ILogger<MarketFxRateProvider> logger)
    : IFxRateProvider
{
    public async Task<decimal?> GetRateToBaseAsync(Market market, CancellationToken cancellationToken = default)
    {
        var currency = MarketCurrency.Of(market);
        var rate = await source.GetRateToBaseAsync(currency, cancellationToken).ConfigureAwait(false);
        if (rate is null || rate.Rate <= 0m)
        {
            logger.LogInformation(
                "基準通貨への換算レートが未解決です（market={Market} currency={Currency}）。当該銘柄の新規建ては見送られます。",
                market, currency);
            return null;
        }

        return rate.Rate;
    }
}
