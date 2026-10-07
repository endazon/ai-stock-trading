using TradeDecisionService.Infrastructure.ExternalServices;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Kernel.Trading;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-17, 05_trading-assumptions §4, IADR-0076 決定1: 採算費用見積りを設定サービスの版付き前提条件（#19・IADR-0063）と
// 既存の概算費用関数（CostCalculator・IADR-0021）から供給する。CostControl(#139・IADR-0065) と同じ薄いアダプタ方針で、
// キャッシュ・AssumptionsChanged 無効化・fail-safe は共有クライアント（IAssumptionsProvider）に委ねる（二重キャッシュを作らない）。
//
// fail-safe（IADR-0076 決定3）: 前提条件が未解決（IsResolved=false＝一度も取得できていない）なら null を返し、
// 採算評価は「見積り不能」＝安全側 Hold に倒れる。実額（手数料）が登録され版が解決されて初めてゲートが有意に働く。
public sealed class AssumptionsProfitabilityProvider(IAssumptionsProvider assumptions) : IProfitabilityAssumptionsProvider
{
    public async Task<TradeCostAssessment?> AssessAsync(
        Market market, int quantity, decimal notional, CancellationToken cancellationToken = default)
    {
        var current = await assumptions.GetCurrentAsync(cancellationToken).ConfigureAwait(false);

        // 未解決（既定値へのフェイルセーフ）なら費用見積り不能として null（採算ゲートは Hold へ倒れる）。
        // 実額未登録の既定手数料は 0 のため、解決前に費用 0 でしきい値を緩めない（決定3）。
        if (!current.IsResolved)
        {
            return null;
        }

        // FR-17, 計画 ADR-0035 決定 5, 05_trading-assumptions §4, #1217, IADR-0508 決定1: 往復費用は取引諸費用（米国株の売りの SEC・TAF）を含む。
        var roundTrip = CostCalculator.EstimateRoundTripCostBreakdown(current.Assumptions, market, quantity, notional);

        // 🔴 #1217, IADR-0508 決定2（IADR-0076 決定3 の維持）: **利用者が登録する費用（手数料＋為替スプレッド相当）が 0 なら見積り不能。**
        // 取引諸費用は計画の暫定値で常に埋まるため、それだけで往復費用が正になる。ゲートの「往復費用 ≤ 0 は見送り」は
        // 「手数料（moomoo の実額）が未登録」の安全網であり、諸費用だけを基準にしきい値をほぼ 0 へ緩めてはならない。
        if (roundTrip.RegisteredCost <= 0m)
        {
            return null;
        }

        return new TradeCostAssessment(
            roundTrip.Total, current.Assumptions.MinimumExpectedProfitMultiple, current.Version,
            current.Assumptions.CapitalGainsTaxRate);
    }
}
