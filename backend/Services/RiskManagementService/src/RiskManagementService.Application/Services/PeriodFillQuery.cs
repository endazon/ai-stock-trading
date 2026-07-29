using AiStockTrading.RiskManagement.Application.State;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-06, FR-16, IADR-0115 決定5, #280: 取引台帳の約定を期間（取引日）で絞る純関数。
// 報告書サービス（#14）が日報/週報/月報の数値集計のため s2s 同期照会する GET /risk-controls/fills の実体。
//
// 取引日は PortfolioProjection.TradeDate（JST 境界）で解釈する。統制・射影と同じ境界を使うことで、
// 「日次上限が見ている 1 日」と「日報が集計する 1 日」がずれない。
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
                var tradingDay = PortfolioProjection.TradeDate(f.ExecutedAt);
                return tradingDay >= fromInclusive && tradingDay <= toInclusive;
            })
            .OrderBy(f => f.ExecutedAt)];
    }
}
