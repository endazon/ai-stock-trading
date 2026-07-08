using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-05, UC-01, UC-02: 発注執行サービスが注文結果（約定・失注・取消）を確定した
public record OrderExecuted(
    Guid DecisionId,
    string OrderId,
    OrderStatus Status,
    int FilledQuantity,
    decimal AveragePrice,
    DateTimeOffset ExecutedAt);
