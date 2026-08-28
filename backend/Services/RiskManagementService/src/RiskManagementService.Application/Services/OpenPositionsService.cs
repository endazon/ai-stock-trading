using RiskManagementService.Application.Ports;
using RiskManagementService.Application.State;
using RiskManagementService.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace RiskManagementService.Application.Services;

// FR-03, FR-10, IADR-0030/0035: 市場監視（#10）の損切りライン検知へ供給する保有ポジションを、#63 取引台帳
// （IPortfolioLedgerStore）の射影から導出する。損切り価格は取引判断が決めた権威データ（IADR-0035）を優先し、
// 欠損する建玉（レガシー等）のみ既定損切り比率を平均取得単価へ適用した近似（IADR-0030）にフォールバックする。
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
            var stopLoss = p.StopLossPrice ?? (p.Side == TradeSide.Buy
                ? p.AverageEntryPrice * (1m - TradingDefaults.DefaultStopLossRatio)
                : p.AverageEntryPrice * (1m + TradingDefaults.DefaultStopLossRatio));

            result.Add(new OpenPositionView(
                p.Symbol, p.Market, p.Side, p.Quantity, p.AverageEntryPrice, stopLoss));
        }

        return result;
    }
}
