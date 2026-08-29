using AiStockTrading.Shared.Contracts.Ports;
using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-10, FR-17, #257, #364, IADR-0107/0152: 判断側ポート IFxRateProvider を、共有ポート IFxRateSource（Fx:Provider で選択）へ
// 写像するアダプタ。市場 → 取引通貨の導出（MarketCurrency）だけを担い、取得・キャッシュ・鮮度判定はレート源側が持つ。
// fail-safe: レート源が解決できない（未結線・取得失敗・鮮度切れ）ときは null を返し、判断側が新規建てを見送る。
public sealed class MarketFxRateProvider(IFxRateSource source, ILogger<MarketFxRateProvider> logger)
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

    /// <summary>
    /// レートと鮮度の判定結果を写す（#506・IADR-0197）。
    /// <b>鮮度切れでも値は返す</b>——出口（手仕舞い）には古いレートでも実在する値が要る。
    /// </summary>
    public async Task<FxRateReading?> GetReadingAsync(Market market, CancellationToken cancellationToken = default)
    {
        var currency = MarketCurrency.Of(market);
        var reading = await source.GetReadingAsync(currency, cancellationToken).ConfigureAwait(false);
        if (reading is null || reading.Rate.Rate <= 0m)
        {
            logger.LogInformation(
                "基準通貨への換算レートが未解決です（market={Market} currency={Currency}）。" +
                "当該銘柄は新規建て・手仕舞いともに見送られます（値が無いため決済へ載せる換算率が無い）。",
                market, currency);
            return null;
        }

        return reading;
    }
}
