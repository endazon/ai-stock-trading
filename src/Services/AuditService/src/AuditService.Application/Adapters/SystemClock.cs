using AiStockTrading.Audit.Application.Ports;

namespace AiStockTrading.Audit.Application.Adapters;

// FR-11: システム時刻に基づく IClock。監査記録の RecordedAt に用いる。
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
