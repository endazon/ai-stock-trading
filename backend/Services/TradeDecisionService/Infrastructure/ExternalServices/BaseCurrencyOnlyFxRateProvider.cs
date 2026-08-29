using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-10, #257, #364, IADR-0107 決定3 / IADR-0152 決定1: FX レート供給の安全既定。基準通貨の市場（米国株）は
// レート 1 で従来どおり判断でき、非基準通貨（日本株）は解決不能（null）＝新規建て見送りに倒れる。
// 実供給（FxRateSourceFactory 経由の Worker アダプタ）は Fx:Provider 設定時に明示配線したときのみ有効になる。
public sealed class BaseCurrencyOnlyFxRateProvider : IFxRateProvider
{
    public Task<decimal?> GetRateToBaseAsync(Market market, CancellationToken cancellationToken = default) =>
        Task.FromResult<decimal?>(MarketCurrency.IsBaseCurrency(market) ? 1m : null);
}
