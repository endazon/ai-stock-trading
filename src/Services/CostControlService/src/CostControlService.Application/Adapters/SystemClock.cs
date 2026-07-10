using AiStockTrading.CostControl.Application.Ports;

namespace AiStockTrading.CostControl.Application.Adapters;

// NFR（費用）: システム時刻に基づく IClock。
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
