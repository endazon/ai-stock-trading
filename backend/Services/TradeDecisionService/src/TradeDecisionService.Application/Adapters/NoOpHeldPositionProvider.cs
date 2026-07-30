using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.TradeDecision.Application.Adapters;

// FR-04, #292, IADR-0119: 建玉照会の安全既定。常に null（不明）を返す。
// 不明のもとでは売り判断が見送りへ倒れる（＝裸の新規売りを出さない）。実照会は Worker が
// RiskManagement:BaseUrl 設定時に HttpHeldPositionProvider を配線したときのみ有効。
public sealed class NoOpHeldPositionProvider : IHeldPositionProvider
{
    public Task<int?> GetSignedQuantityAsync(
        string symbol, Market market, CancellationToken cancellationToken = default) =>
        Task.FromResult<int?>(null);
}
