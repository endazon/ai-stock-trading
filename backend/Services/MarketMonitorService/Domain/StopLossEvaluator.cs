using AiStockTrading.Shared.Contracts.Trading;

namespace MarketMonitorService.Domain;

// FR-03, FR-10, ADR-0003: 保有ポジションが損切りラインに到達したかを判定する。建玉方向で対称に扱う。
public static class StopLossEvaluator
{
    // ロング（Buy 建て）: 現在値が損切り価格以下に下落したら到達。
    // ショート（Sell 建て・信用有効時）: 現在値が損切り価格以上に上昇したら到達。
    public static bool IsTriggered(HeldPosition position, decimal currentPrice)
    {
        ArgumentNullException.ThrowIfNull(position);

        return IsTriggered(position.Side, position.StopLossPrice, currentPrice);
    }

    // #909, IADR-0380 決定3: 建玉を持たない場（閉場時の「最終観測値が既にラインを越えていたか」の判定）からも
    // 同じ比較を引けるようにする。**比較の定義を 2 か所に置かない**（向きの取り違えは無保護へ倒れる）。
    public static bool IsTriggered(TradeSide side, decimal stopLossPrice, decimal currentPrice) => side switch
    {
        TradeSide.Buy => currentPrice <= stopLossPrice,
        TradeSide.Sell => currentPrice >= stopLossPrice,
        _ => false,
    };
}
