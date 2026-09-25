using AiStockTrading.Shared.Kernel.Trading;

namespace RiskManagementService.Features.RiskManagement.GetOpenPositions;

// FR-03, FR-10, IADR-0030/0035: 市場監視（#10）の損切りライン検知へ供給する保有ポジションを、#63 取引台帳
// （IPortfolioLedgerStore）の射影から導出する。損切り価格は取引判断が決めた権威データ（IADR-0035）を優先し、
// 欠損する建玉（レガシー等）のみ既定損切り比率を平均取得単価へ適用した近似（IADR-0030）にフォールバックする。
//
// FR-10, FR-03, #936, IADR-0393: 射影の損切り価格は「保有中のエントリー（ロット）のうち最も保護的なライン」である。
// 🔴 **損切り価格の記録を持たないロットが混じるときは、そのロットを近似で見積もって候補に入れ、最も保護的な値を採る。**
// 記録の無いロットを「ラインが無い」と読んで捨てると、不明を無いと取り違えることになる（ラインの分からない建玉が
// 他のロットのラインでしか守られない）。すべてのロットに記録があれば近似は入らない（従来どおり実値だけ）。
public sealed class OpenPositionsService(IPortfolioLedgerStore ledger)
{
    public IReadOnlyList<OpenPositionView> Build()
    {
        var positions = PortfolioProjection.ProjectOpenPositions(ledger.GetFills());

        var result = new List<OpenPositionView>(positions.Count);
        foreach (var p in positions)
        {
            // IADR-0035: 取引判断が決めた損切り価格（権威データ）があれば実値を用いる。無い建玉（レガシー/欠損）は
            // 既定比率の近似にフォールバックする（IADR-0030）。近似はロングが取得単価より下、ショートが上。
            // #957, IADR-0399: 市場監視（応答にラインが無い行）と同じ式を使う（共有の StopLossApproximation）。
            var approximated = StopLossApproximation.Approximate(p.Side, p.AverageEntryPrice);
            var stopLoss = p.StopLossPrice is not { } known
                ? approximated
                : p.StopLossUnknown
                    ? PortfolioProjection.MostProtective(p.Side, [known, approximated])!.Value
                    : known;

            result.Add(new OpenPositionView(
                p.Symbol, p.Market, p.Side, p.Quantity, p.AverageEntryPrice, stopLoss));
        }

        return result;
    }
}
