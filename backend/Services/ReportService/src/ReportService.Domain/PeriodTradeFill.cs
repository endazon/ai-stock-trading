using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.Report.Domain;

// FR-16: 損益集計の入力となる 1 約定。取引台帳（#63）の約定に対応する（実データ源の連携は #22 後続）。
// Quantity は約定数量（>0）、Price は約定単価（基準通貨〔USD〕へ換算済みの参照価格）。
// PositionEffect は集計ロジックでは未使用（在庫は Side の符号で増減判定する）が、台帳の LedgerFill との整合・監査・
// 将来の両建て（別ロット）会計のため保持する（IADR-0018/0025 と同方針）。
public sealed record PeriodTradeFill(
    string Symbol,
    Market Market,
    TradeSide Side,
    PositionEffect PositionEffect,
    int Quantity,
    decimal Price,
    DateTimeOffset ExecutedAt);
