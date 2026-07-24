using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.State;

namespace AiStockTrading.TradeDecision.Application.Adapters;

// FR-02, FR-04, IADR-0099 決定1: 現在値供給の安全既定。実ソースを呼ばず常に null（＝現在値なし＝現行動作）を返す。
// IsEnabled=false のため判断側の fail-safe ゲート（有効化時のみ発注抑止）は作動せず、既定挙動をバイト等価に保つ。
// 実供給（MarketDataCurrentPriceProvider）は Worker が MarketData:Provider 設定時に明示配線したときのみ有効。
public sealed class NoOpCurrentPriceProvider : ICurrentPriceProvider
{
    public bool IsEnabled => false;

    public Task<decimal?> GetCurrentPriceAsync(
        DecisionTrigger trigger, CancellationToken cancellationToken = default) =>
        Task.FromResult<decimal?>(null);
}
