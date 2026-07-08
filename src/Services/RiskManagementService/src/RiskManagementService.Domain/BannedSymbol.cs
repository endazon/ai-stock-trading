using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Domain;

// FR-19: 取引禁止銘柄（利用者が登録。理由と登録日を記録する）
public record BannedSymbol(
    string Symbol,
    Market Market,
    string Reason,
    DateOnly RegisteredOn);
