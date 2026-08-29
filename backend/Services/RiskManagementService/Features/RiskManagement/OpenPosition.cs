using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-03, FR-10, IADR-0030/0035: #63 台帳の射影が返す銘柄別ネット建玉（数量>0）。Side は符号付き在庫の向き
// （+ ロング=Buy / − ショート=Sell）、AverageEntryPrice は平均取得単価。
// StopLossPrice は最新の同方向エントリー由来の損切り価格（IADR-0035・nullable＝欠損時は OpenPositionsService が近似導出）。
// FR-10, #257, IADR-0107 決定1/4: AverageEntryPrice・StopLossPrice は**ローカル通貨**（現在値と同一通貨で比較するため）。
// FxRateToBase は建玉の加重平均約定時レートで、含み損益を基準通貨（USD）へ換算するために持つ（既定 1＝米国株）。
public sealed record OpenPosition(
    string Symbol,
    Market Market,
    TradeSide Side,
    int Quantity,
    decimal AverageEntryPrice,
    decimal? StopLossPrice = null,
    decimal FxRateToBase = 1m);
