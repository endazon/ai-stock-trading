using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;

// FR-20, FR-15, UC-06, IADR-0089: バックテスト verdict（BacktestEvaluated・BacktestService/#16）を購読し、
// 段階別実績ストア（IStagePerformanceStore）へ backtest 由来フィールド（BacktestPassed / BacktestMaxDrawdownRatio）
// のみ read-modify-write で射影する。運用系フィールド（ObservedMaxDrawdownRatio /
// SlippageAndCostWithinExpected / DailyLossLimitRespected）は別ドライバの供給源のため現行値を保全する。
// FR-20, #387, IADR-0148: **クラス C 統制違反件数は本行が持たない**（旧 ControlViolationCount 列は削除した）。
// 供給元は発注審査の観測ログであり、判定へは StageGate の必須引数として直接渡る。保全対象ではない。
// 射影は永続化・昇格判定の後段・非同期であり、未供給時は既定（BacktestPassed=false）＝昇格拒否の fail-safe を崩さない（#164）。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<BacktestEvaluated> から Wolverine のハンドラへ移行した。
public sealed class BacktestEvaluatedProjectionHandler(
    IStagePerformanceStore store,
    ILogger<BacktestEvaluatedProjectionHandler> logger)
{
    public void Handle(BacktestEvaluated message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // read-modify-write: backtest 由来フィールドのみ更新し、運用系フィールドは上書きしない。
        var current = store.GetCurrent();
        store.Save(current with
        {
            BacktestPassed = message.Passed,
            BacktestMaxDrawdownRatio = message.MaxDrawdownRatio,
        });
        logger.LogInformation(
            "バックテスト verdict を段階別実績へ射影: Passed={Passed} 最大DD={MaxDrawdown}",
            message.Passed, message.MaxDrawdownRatio);
    }
}
