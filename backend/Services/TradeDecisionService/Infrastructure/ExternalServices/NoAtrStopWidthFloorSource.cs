using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-10, ADR-0049 決定2・決定5, #1120, IADR-0465 決定1: ATR(14) を供給しない既定の実装（配備までの暫定手段）。
// 常に null を返し、取引判断は参照価格（アンカー後）の 2% を下限とする。日足が判断へ通ってから ATR の実装へ差し替える。
public sealed class NoAtrStopWidthFloorSource : IStopWidthFloorSource
{
    public ValueTask<StopWidthFloor?> GetFloorAsync(
        string symbol, Market market, decimal anchoredPrice, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<StopWidthFloor?>(null);
}
