using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Features.RiskManagement;

// FR-10, #829, IADR-0346: 承認済みで生きている（終端でない）新規建て注文 1 件。射影（PortfolioProjection）の入力。
// Quantity は**承認数量**（約定分を引く前）であり、残数量は Project が同じ DecisionId の約定累計を引いて出す
// （約定と同じ入力から引くことで二重計上しない）。Price はローカル通貨の承認価格、FxRateToBase は承認時レート
// （約定時レートの近似・IADR-0107。既定 1＝基準通貨市場）。
public sealed record WorkingEntryOrder(
    Guid DecisionId,
    string Symbol,
    Market Market,
    TradeSide Side,
    int Quantity,
    decimal Price,
    DateTimeOffset ApprovedAt,
    decimal FxRateToBase = 1m)
{
    /// <summary>基準通貨（USD）建ての承認価格。金額の算入はこの単価で行う（約定の <see cref="LedgerFill.PriceInBase"/> と同じ換算）。</summary>
    public decimal PriceInBase => Price * FxRateToBase;
}
