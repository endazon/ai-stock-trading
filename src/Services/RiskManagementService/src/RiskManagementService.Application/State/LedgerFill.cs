using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.State;

// FR-10, FR-05, IADR-0018: 取引台帳の 1 約定（承認 Intent と OrderExecuted を DecisionId で相関して補完済み）。
// 射影（PortfolioProjection）の入力。銘柄・市場・約定方向・建玉効果・約定数量・約定単価・約定時刻を持つ。
// Quantity は約定数量（>0）、Price は約定単価（円換算の参照価格）。
public sealed record LedgerFill(
    string Symbol,
    Market Market,
    TradeSide Side,
    PositionEffect PositionEffect,
    int Quantity,
    decimal Price,
    DateTimeOffset ExecutedAt);
