
namespace RiskManagementService.Features.RiskManagement.GetFills;

// FR-06, FR-16, IADR-0115 決定5, #280, #337（#249 吸収）, IADR-0246: 取引台帳の約定を期間（取引日）で絞る純関数。
// 報告書サービス（#14）が日報/週報/月報の数値集計のため s2s 同期照会する GET /risk-controls/fills の実体。
//
// 取引日は PortfolioProjection.TradeDate（**約定の市場の現地取引日**）で解釈する。統制・射影と同じ境界を
// 使うことで、「日次上限が見ている 1 日」と「日報が集計する 1 日」がずれない——この一致は境界を
// 市場別解釈へ移しても不変条件である（片側だけ JST に残すとずれが復活する）。
public static class PeriodFillQuery
{
    /// <summary>取引日が [fromInclusive, toInclusive] に入る約定を約定時刻の昇順で返す。逆順の期間は空。</summary>
    public static IReadOnlyList<LedgerFill> InTradingDayRange(
        IReadOnlyList<LedgerFill> fills,
        DateOnly fromInclusive,
        DateOnly toInclusive)
    {
        ArgumentNullException.ThrowIfNull(fills);

        if (fromInclusive > toInclusive)
            return [];

        return [.. fills
            .Where(f =>
            {
                var tradingDay = PortfolioProjection.TradeDate(f.ExecutedAt, f.Market);
                return tradingDay >= fromInclusive && tradingDay <= toInclusive;
            })
            .OrderBy(f => f.ExecutedAt)];
    }
}
