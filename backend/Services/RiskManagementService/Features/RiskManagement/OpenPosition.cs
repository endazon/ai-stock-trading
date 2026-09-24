using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-03, FR-10, IADR-0030/0035: #63 台帳の射影が返す銘柄別ネット建玉（数量>0）。Side は符号付き在庫の向き
// （+ ロング=Buy / − ショート=Sell）、AverageEntryPrice は平均取得単価。
// StopLossPrice は**保有中のエントリー（ロット）のうち最も保護的な損切り価格**（ロング: 最も高い／ショート: 最も低い。
// #936, IADR-0393 が IADR-0035 の「最新の同方向エントリー」を改めた）。nullable＝どのロットも損切り価格を持たない
// （欠損時は OpenPositionsService が近似導出）。
// FR-10, #257, IADR-0107 決定1/4: AverageEntryPrice・StopLossPrice は**ローカル通貨**（現在値と同一通貨で比較するため）。
// FxRateToBase は建玉の加重平均約定時レートで、含み損益を基準通貨（USD）へ換算するために持つ（既定 1＝米国株）。
public sealed record OpenPosition(
    string Symbol,
    Market Market,
    TradeSide Side,
    int Quantity,
    decimal AverageEntryPrice,
    decimal? StopLossPrice = null,
    decimal FxRateToBase = 1m)
{
    /// <summary>
    /// #936, IADR-0393: 損切り価格の記録を持たないロット（レガシーの約定等）が保有に含まれるか。
    /// 🔴 **「そのロットにはラインが無い」ではなく「ラインが分からない」**である。<see cref="StopLossPrice"/> が
    /// 他のロットの値を持っていても、公開する側（OpenPositionsService）はこのロットを近似で見積もって候補に入れる。
    /// 既定 false。<c>ProjectOpenPositions</c> だけが設定する（もう 1 つの組み手 <c>Project</c> は統制の判定入力であり、
    /// 損切り価格そのものを持たせない＝<see cref="StopLossPrice"/> も null）。
    /// </summary>
    public bool StopLossUnknown { get; init; }
}
