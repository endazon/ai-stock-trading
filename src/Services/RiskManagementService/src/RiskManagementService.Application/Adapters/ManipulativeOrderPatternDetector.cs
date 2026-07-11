using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.RiskManagement.Domain.Manipulation;
using AiStockTrading.Shared.Contracts.Trading;

namespace AiStockTrading.RiskManagement.Application.Adapters;

// FR-19, ADR-0007, IADR-0006/0037: 相場操縦パターン判定ポートの具体実装。
// 注文アクティビティ源から intent の（銘柄, 市場）の直近窓を取得し、純関数コア ManipulationPatternAnalyzer で判定する。
// RiskEvaluator/OrderScreeningService から「ガード有効かつ本検出器注入時のみ」呼ばれる（IADR-0006）。
public sealed class ManipulativeOrderPatternDetector(
    IOrderActivitySource activitySource,
    IClock clock,
    ManipulationDetectionSettings settings) : IManipulativeOrderPatternDetector
{
    public bool IsSuspectedManipulation(OrderIntent intent, PortfolioSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(intent);

        var window = activitySource.GetRecentActivity(
            intent.Symbol, intent.Market, clock.UtcNow, settings.LookbackWindow);

        return ManipulationPatternAnalyzer.Analyze(window, settings).IsSuspected;
    }
}
