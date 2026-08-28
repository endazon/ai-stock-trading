using CostControlService.Application.Ports;

namespace CostControlService.Application.Adapters;

// NFR（費用）: システム時刻に基づく IClock。
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
