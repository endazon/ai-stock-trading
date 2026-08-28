namespace MarketMonitorService.Common.Abstractions;

// FR-03: システム時刻に基づく IClock。
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
