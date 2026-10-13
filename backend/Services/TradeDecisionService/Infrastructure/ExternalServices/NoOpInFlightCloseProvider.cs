using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, #1338, IADR-0535 決定2・3: 処理中の決済の照会の安全既定（未結線）。常に null（不明）＝判断は LLM を呼ぶ（従来どおり）。
public sealed class NoOpInFlightCloseProvider : IInFlightCloseProvider
{
    public Task<InFlightCloseCoverage?> GetAsync(
        string symbol, Market market, TradeSide closeSide, CancellationToken cancellationToken = default) =>
        Task.FromResult<InFlightCloseCoverage?>(null);
}
