using AiStockTrading.Shared.Contracts.Trading;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-17, IADR-0076: 採算費用見積りの安全既定。設定サービスへ接続せず常に null（＝前提条件未解決＝採算不能）を返す。
// これにより Application は設定サービス非依存で成立し、採算ゲート有効時は fail-safe に Hold へ倒れる（決定3）。
// 実費用見積り（AssumptionsProfitabilityProvider）は Worker が Configuration:BaseUrl 設定時に明示配線したときのみ有効。
public sealed class NoOpProfitabilityAssumptionsProvider : IProfitabilityAssumptionsProvider
{
    public Task<TradeCostAssessment?> AssessAsync(
        Market market, decimal notional, CancellationToken cancellationToken = default) =>
        Task.FromResult<TradeCostAssessment?>(null);
}
