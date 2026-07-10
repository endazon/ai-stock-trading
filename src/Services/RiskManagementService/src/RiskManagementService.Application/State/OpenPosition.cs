using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.State;

// FR-03, FR-10, IADR-0030: #63 台帳の射影が返す銘柄別ネット建玉（数量>0）。Side は符号付き在庫の向き
// （+ ロング=Buy / − ショート=Sell）、AverageEntryPrice は平均取得単価。損切り価格は含まない（導出は OpenPositionsService）。
public sealed record OpenPosition(
    string Symbol,
    Market Market,
    TradeSide Side,
    int Quantity,
    decimal AverageEntryPrice);
