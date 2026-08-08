using AiStockTrading.RiskManagement.Application.Ports;

namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-10: システム時刻に基づく IClock。基準タイムゾーンは全体前提条件に合わせ Asia/Tokyo（JST）とする
// （§5 の日次基準・営業日は日本時間で運用する想定）。Slice B で構成から差し替え可能にする。
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    // #463, IADR-0181: 取引日の導出は TradingDay に一本化した（観測の到達の記録が同じ基準を要するため）。
    // **供給元が 2 つになれば必ず食い違う**——ここで別に変換すると「観測が届いた日」と「当日」がずれる。
    public DateOnly Today => TradingDay.Of(DateTimeOffset.UtcNow);
}
