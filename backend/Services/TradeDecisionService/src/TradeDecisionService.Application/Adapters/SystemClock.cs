using AiStockTrading.TradeDecision.Application.Ports;

namespace AiStockTrading.TradeDecision.Application.Adapters;

// FR-04: システム時刻に基づく IClock。
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
