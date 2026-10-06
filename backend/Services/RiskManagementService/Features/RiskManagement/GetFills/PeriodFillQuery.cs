
namespace RiskManagementService.Features.RiskManagement.GetFills;

// FR-06, FR-16, IADR-0115 決定5, #280, #337（#249 吸収）, IADR-0246: 取引台帳の約定を期間（取引日）で絞る純関数。
// 報告書サービス（#14）が日報/週報/月報の数値集計のため s2s 同期照会する GET /risk-controls/fills の実体。
//
// 取引日は PortfolioProjection.TradeDate（**約定の市場の現地取引日**）で解釈する。統制・射影と同じ境界を
// 使うことで、「日次上限が見ている 1 日」と「日報が集計する 1 日」がずれない——この一致は境界を
// 市場別解釈へ移しても不変条件である（片側だけ JST に残すとずれが復活する）。
//
// FR-06, #1172, IADR-0492 決定 3: 報告書サービスは報告書の期間（JST の営業日）をそのまま [from, to] に渡さない
// （同じ日付で引くと、米国のセッションは 16:00 JST の生成時点でまだ始まっていない）。生成境界までに閉場した
// セッションの窓の外包で引き、市場ごとに絞り直す。本関数の契約（市場の現地取引日の [from, to]）は変えない。
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

        // FR-06, FR-16, FR-11, #849, IADR-0350 決定 4: **乖離の取り込み行は約定ではないため返さない。**
        // 報告書は本列を約定として畳み込む（実現損益・勝率・費用の概算・取引履歴）。取り込み行を混ぜると
        // 「平均取得単価で売った損益 0 の決済」が**確定値として**集計・表示される——システム外の売買の価格は
        // 分からないのであって、0 ではない。
        return [.. fills
            .Where(f => !f.IsDriftAdoption)
            .Where(f =>
            {
                var tradingDay = PortfolioProjection.TradeDate(f.ExecutedAt, f.Market);
                return tradingDay >= fromInclusive && tradingDay <= toInclusive;
            })
            .OrderBy(f => f.ExecutedAt)];
    }
}
