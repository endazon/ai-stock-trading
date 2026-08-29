using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Domain;

// FR-05, FR-16: 実効スリッページの算出（アーキ概要「執行方針: 実効スリッページの取引毎記録」）。
// 計画価格（OrderIntent.Price）と平均約定価格の差を、常に「不利＝正」の符号で表す:
//   買い（Buy）: 高く約定 = 不利 → (avgFill − planned) / planned
//   売り（Sell）: 安く約定 = 不利 → (planned − avgFill) / planned
// 未約定（planned ≤ 0 / avgFill ≤ 0）は 0 とする。
public static class SlippageCalculator
{
    public static decimal Compute(decimal plannedPrice, decimal averageFillPrice, TradeSide side)
    {
        if (plannedPrice <= 0m || averageFillPrice <= 0m)
        {
            return 0m;
        }

        var raw = side == TradeSide.Buy
            ? averageFillPrice - plannedPrice
            : plannedPrice - averageFillPrice;

        return raw / plannedPrice;
    }
}
