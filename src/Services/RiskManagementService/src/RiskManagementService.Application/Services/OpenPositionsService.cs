using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.State;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-03, FR-10, IADR-0030: 市場監視（#10）の損切りライン検知へ供給する保有ポジションを、#63 取引台帳
// （IPortfolioLedgerStore）の射影から導出する。損切り価格は権威データが現契約に無いため、既定損切り比率を
// 平均取得単価へ適用した近似値とする（過渡的措置・後続で実値化）。
public sealed class OpenPositionsService(IPortfolioLedgerStore ledger)
{
    public IReadOnlyList<OpenPositionView> Build()
    {
        var positions = PortfolioProjection.ProjectOpenPositions(ledger.GetFills());

        var result = new List<OpenPositionView>(positions.Count);
        foreach (var p in positions)
        {
            // ロングは取得単価より下、ショートは上に損切りラインを置く（StopLossEvaluator と対称）。
            var stopLoss = p.Side == TradeSide.Buy
                ? p.AverageEntryPrice * (1m - TradingDefaults.DefaultStopLossRatio)
                : p.AverageEntryPrice * (1m + TradingDefaults.DefaultStopLossRatio);

            result.Add(new OpenPositionView(
                p.Symbol, p.Market, p.Side, p.Quantity, p.AverageEntryPrice, stopLoss));
        }

        return result;
    }
}
