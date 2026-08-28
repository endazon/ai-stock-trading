using OrderExecutionService.Application.Ports;

namespace OrderExecutionService.Application.Adapters;

// FR-05: システム時刻に基づく IClock。
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
