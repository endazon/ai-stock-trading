using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.MarketMonitor.Domain;

// FR-03, FR-10, ADR-0003: 保有ポジションが損切りラインに到達したかを判定する。建玉方向で対称に扱う。
public static class StopLossEvaluator
{
    // ロング（Buy 建て）: 現在値が損切り価格以下に下落したら到達。
    // ショート（Sell 建て・信用有効時）: 現在値が損切り価格以上に上昇したら到達。
    public static bool IsTriggered(HeldPosition position, decimal currentPrice)
    {
        ArgumentNullException.ThrowIfNull(position);

        return position.Side switch
        {
            TradeSide.Buy => currentPrice <= position.StopLossPrice,
            TradeSide.Sell => currentPrice >= position.StopLossPrice,
            _ => false,
        };
    }
}
