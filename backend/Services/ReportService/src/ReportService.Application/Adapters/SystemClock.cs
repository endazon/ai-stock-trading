using AiStockTrading.Report.Application.Ports;

namespace AiStockTrading.Report.Application.Adapters;

// FR-07: システム時刻に基づく IClock。確定日時に用いる。
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
