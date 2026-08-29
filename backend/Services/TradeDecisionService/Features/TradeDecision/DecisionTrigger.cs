using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace TradeDecisionService.Features.TradeDecision;

// FR-02, FR-03, IADR-0023: 判断の起点を一般化したトリガー。定時（Scheduled）と価格変動（PriceMovement）の 2 系統を
// 同一の DecideAsync へ合流させる。価格文脈（Price/BaselinePrice/ChangeRatio）は PriceMovement 起点でのみ与えられる。
public enum DecisionTriggerKind
{
    Scheduled,
    PriceMovement,
}

public sealed record DecisionTrigger(
    string Symbol,
    Market Market,
    DecisionTriggerKind Kind,
    decimal? Price = null,
    decimal? BaselinePrice = null,
    decimal? ChangeRatio = null)
{
    // 価格変動イベント（イベント駆動系統）から生成する。
    public static DecisionTrigger FromPriceMovement(PriceMovementDetected e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new DecisionTrigger(e.Symbol, e.Market, DecisionTriggerKind.PriceMovement, e.Price, e.BaselinePrice, e.ChangeRatio);
    }

    // 定時サイクル（価格変動トリガーなし）から生成する。銘柄・市場は監視銘柄（watchlist）由来。
    public static DecisionTrigger Scheduled(string symbol, Market market) =>
        new(symbol, market, DecisionTriggerKind.Scheduled);
}
