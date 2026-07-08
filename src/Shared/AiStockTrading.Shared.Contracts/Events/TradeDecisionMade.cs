using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Shared.Contracts.Events;

// FR-04, UC-01, UC-02: 取引判断サービスが売買判断を確定した（判断根拠つき）
public record TradeDecisionMade(
    Guid DecisionId,
    OrderIntent Intent,
    string Rationale,
    DateTimeOffset DecidedAt);
