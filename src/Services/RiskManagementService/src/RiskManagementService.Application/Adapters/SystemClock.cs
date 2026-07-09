using AiStockTrading.RiskManagement.Application.Ports;

namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-10: システム時刻に基づく IClock。基準タイムゾーンは全体前提条件に合わせ Asia/Tokyo（JST）とする
// （§5 の日次基準・営業日は日本時間で運用する想定）。Slice B で構成から差し替え可能にする。
public sealed class SystemClock : IClock
{
    private static readonly TimeZoneInfo BaseZone =
        TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "Tokyo Standard Time" : "Asia/Tokyo");

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, BaseZone).DateTime);
}
