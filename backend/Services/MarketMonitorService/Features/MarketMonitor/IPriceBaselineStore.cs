using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Features.MarketMonitor;

// FR-03: 変動判定の基準値（前回 AI 判断時点の価格）を銘柄別に保持する。
// 基準値の更新契機は Slice B で TradeDecisionMade 購読により実装する。未設定銘柄は null を返す。
public interface IPriceBaselineStore
{
    decimal? GetBaseline(string symbol, Market market);

    void SetBaseline(string symbol, Market market, decimal price);
}
