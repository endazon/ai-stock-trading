using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Services;

// FR-10, FR-03, UC-02, ADR-0003, IADR-0015: 損切りの機械執行。市場監視の StopLossTriggered から決済（Close）の
// OrderApproved を組み立てる。損切りは「必ず実行」（kill switch・ロックアウト・相場操縦ガードで止めない）ため、
// 発注前スクリーニング（RiskEvaluator）を通さず無条件に承認を発行する。
public sealed class StopLossExecutionService(IRiskSettingsStore settingsStore, IClock clock)
{
    public OrderApproved BuildCloseApproval(StopLossTriggered triggered)
    {
        ArgumentNullException.ThrowIfNull(triggered);

        var settings = settingsStore.GetCurrent();

        // 決済は建玉方向の反対売買。ロング（Buy 建て）は Sell、ショート（Sell 建て）は Buy で手仕舞う。
        var closeSide = triggered.PositionSide == TradeSide.Buy ? TradeSide.Sell : TradeSide.Buy;

        // ProductType は現物のみ有効な現段階では Cash。Mode は現行段階の動作モード（Paper/Live）。
        var intent = new OrderIntent(
            triggered.Symbol,
            triggered.Market,
            closeSide,
            ProductType.Cash,
            settings.Stage.Mode,
            triggered.Quantity,
            triggered.Price,
            PositionEffect.Close);

        // 先行する TradeDecisionMade は無い（LLM 迂回）。冪等性のため DecisionId は StopLossTriggered.EventId から
        // 決定的に採る（IADR-0015）。MassTransit の再送で同一イベントが再処理されても同じ DecisionId になり、
        // 発注執行（#13）側の DecisionId ベース重複排除がすり抜けない。
        return new OrderApproved(triggered.EventId, intent, triggered.Quantity, clock.UtcNow);
    }
}
