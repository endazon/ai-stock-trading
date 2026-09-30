using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-10, #1113, IADR-0463 決定 4: 新規建ての可否の照会の安全既定（未結線）。常に null（不明）＝判断は LLM を呼ぶ（従来どおり）。
public sealed class NoOpEntryBlockersProvider : IEntryBlockersProvider
{
    public Task<EntryBlockers?> GetAsync(string symbol, Market market, CancellationToken cancellationToken = default) =>
        Task.FromResult<EntryBlockers?>(null);
}
