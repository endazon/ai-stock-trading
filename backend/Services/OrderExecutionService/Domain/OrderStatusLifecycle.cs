using AiStockTrading.Shared.Contracts.Trading;

namespace OrderExecutionService.Domain;

// #270, FR-05, IADR-0113: 注文状態の終端判定。約定追跡（ポーラー）と発注結果ストアが同じ定義を用いるための
// 単一情報源。終端＝これ以上ブローカ側で状態が変わらない状態であり、追跡の打ち切り条件になる。
public static class OrderStatusLifecycle
{
    public static bool IsTerminal(OrderStatus status) =>
        status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Rejected or OrderStatus.Expired;

    /// <summary>非終端（<see cref="OrderStatus.Accepted"/> / <see cref="OrderStatus.PartiallyFilled"/>）＝追跡対象。</summary>
    public static bool IsPending(OrderStatus status) => !IsTerminal(status);
}
