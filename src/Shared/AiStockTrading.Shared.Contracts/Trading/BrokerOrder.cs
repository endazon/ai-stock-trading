namespace AiStockTrading.Shared.Contracts.Trading;

// FR-05: 証券会社アダプタが返す注文の実体
public record BrokerOrder(
    string OrderId,
    OrderIntent Intent,
    OrderStatus Status,
    int FilledQuantity,
    decimal AveragePrice,
    DateTimeOffset PlacedAt,
    DateTimeOffset? CompletedAt);
