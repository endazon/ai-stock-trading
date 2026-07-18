using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.RiskManagement.Worker.Composable.Steps;

// FR-20, FR-15, UC-06, IADR-0089: バックテスト verdict（BacktestEvaluated・BacktestService/#16）を購読し、
// 段階別実績ストア（IStagePerformanceStore）へ backtest 由来フィールド（BacktestPassed / BacktestMaxDrawdownRatio）
// のみ read-modify-write で射影する。運用系フィールド（ObservedMaxDrawdownRatio / ControlViolationCount /
// SlippageAndCostWithinExpected / DailyLossLimitRespected）は別ドライバの供給源のため現行値を保全する。
// 射影は永続化・昇格判定の後段・非同期であり、未供給時は既定（BacktestPassed=false）＝昇格拒否の fail-safe を崩さない（#164）。
internal sealed class BacktestEvaluatedProjectionConsumer(
    IStagePerformanceStore store,
    ILogger<BacktestEvaluatedProjectionConsumer> logger)
    : IConsumer<BacktestEvaluated>
{
    public Task Consume(ConsumeContext<BacktestEvaluated> context)
    {
        var m = context.Message;
        // read-modify-write: backtest 由来フィールドのみ更新し、運用系フィールドは上書きしない。
        var current = store.GetCurrent();
        store.Save(current with
        {
            BacktestPassed = m.Passed,
            BacktestMaxDrawdownRatio = m.MaxDrawdownRatio,
        });
        logger.LogInformation(
            "バックテスト verdict を段階別実績へ射影: Passed={Passed} 最大DD={MaxDrawdown}",
            m.Passed, m.MaxDrawdownRatio);
        return Task.CompletedTask;
    }
}
