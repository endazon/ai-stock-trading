using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Report.Domain;

// FR-16: 損益集計の入力となる 1 約定。取引台帳（#63）の約定に対応する（実データ源の連携は #22 後続）。
// Quantity は約定数量（>0）、Price は約定単価（円換算参照価格）。
public sealed record PeriodTradeFill(
    string Symbol,
    Market Market,
    TradeSide Side,
    PositionEffect PositionEffect,
    int Quantity,
    decimal Price,
    DateTimeOffset ExecutedAt);
