
namespace InformationCollectionService.Common.Abstractions;

// FR-01: システム時刻に基づく IClock。レート制限（IADR-0064）の補充判定に用いる。
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
