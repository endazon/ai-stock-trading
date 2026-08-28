using ConfigurationService.Application.Ports;

namespace ConfigurationService.Application.Adapters;

// FR-17: システム時刻に基づく IClock。変更履歴の日時に用いる。
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
