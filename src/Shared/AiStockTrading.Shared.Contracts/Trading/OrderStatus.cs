namespace AiStockTrading.Shared.Contracts.Trading;

// FR-05: 注文状態（受付・約定・失注・取消）の追跡
public enum OrderStatus
{
    Accepted,
    PartiallyFilled,
    Filled,
    Expired,
    Cancelled,
}
