using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TradeDecision.Application.Ports;

namespace AiStockTrading.TradeDecision.Application.Adapters;

// FR-10, #257, IADR-0106 決定3: FX レート供給の安全既定。基準通貨の市場（日本株）はレート 1 で従来どおり判断でき、
// 非基準通貨（米国株）は解決不能（null）＝新規建て見送りに倒れる。
// 実供給（FxRateSourceFactory 経由の Worker アダプタ）は Fx:Provider 設定時に明示配線したときのみ有効になる。
public sealed class BaseCurrencyOnlyFxRateProvider : IFxRateProvider
{
    public Task<decimal?> GetRateToBaseAsync(Market market, CancellationToken cancellationToken = default) =>
        Task.FromResult<decimal?>(MarketCurrency.IsBaseCurrency(market) ? 1m : null);
}
