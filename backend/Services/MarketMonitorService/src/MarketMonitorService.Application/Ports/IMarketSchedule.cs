namespace AiStockTrading.MarketMonitor.Application.Ports;

// FR-03: 市場の開場判定。閉場中は監視ポーリングを停止する（04_workflows/02: 市場閉場中は監視停止）。
// 本 PR は平日判定の最小実装。時間帯・祝日を含む正確な市場カレンダーは #21 で差し替える（ポートで吸収）。
public interface IMarketSchedule
{
    bool IsOpen(DateTimeOffset instant);
}
