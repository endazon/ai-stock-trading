using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.OrderExecution.Domain;

// FR-05, FR-16: 発注結果の記録（注文実体＋スリッページ）。月報レビュー（FR-16）・ポジション/損益射影のデータ源。
// DecisionId で取引判断/損切りと相関する。SlippageRatio は「不利＝正」（SlippageCalculator）。
public record ExecutionRecord(
    Guid DecisionId,
    string OrderId,
    string Symbol,
    Market Market,
    TradeSide Side,
    ProductType ProductType,
    PositionEffect PositionEffect,
    int Quantity,
    decimal PlannedPrice,
    int FilledQuantity,
    decimal AveragePrice,
    OrderStatus Status,
    decimal SlippageRatio,
    DateTimeOffset ExecutedAt);
