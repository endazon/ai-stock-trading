using AiStockTrading.Configuration.Client.Ports;
using AiStockTrading.Configuration.Domain;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.TradeDecision.Application.Ports;

namespace AiStockTrading.TradeDecision.Worker.Composable.Adapters;

// FR-17, 05_trading-assumptions §4, IADR-0076 決定1: 採算費用見積りを設定サービスの版付き前提条件（#19・IADR-0063）と
// 既存の概算費用関数（CostCalculator・IADR-0021）から供給する。CostControl(#139・IADR-0065) と同じ薄いアダプタ方針で、
// キャッシュ・AssumptionsChanged 無効化・fail-safe は共有クライアント（IAssumptionsProvider）に委ねる（二重キャッシュを作らない）。
//
// fail-safe（IADR-0076 決定3）: 前提条件が未解決（IsResolved=false＝一度も取得できていない）なら null を返し、
// 採算評価は「見積り不能」＝安全側 Hold に倒れる。実額（手数料）が登録され版が解決されて初めてゲートが有意に働く。
internal sealed class AssumptionsProfitabilityProvider(IAssumptionsProvider assumptions) : IProfitabilityAssumptionsProvider
{
    public async Task<TradeCostAssessment?> AssessAsync(
        Market market, decimal notional, CancellationToken cancellationToken = default)
    {
        var current = await assumptions.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        // 未解決（既定値へのフェイルセーフ）なら費用見積り不能として null（採算ゲートは Hold へ倒れる）。
        // 実額未登録の既定手数料は 0 のため、解決前に費用 0 でしきい値を緩めない（決定3）。
        if (!current.IsResolved)
        {
            return null;
        }

        var roundTripCost = CostCalculator.EstimateRoundTripCost(current.Assumptions, market, notional);
        return new TradeCostAssessment(
            roundTripCost, current.Assumptions.MinimumExpectedProfitMultiple, current.Version);
    }
}
