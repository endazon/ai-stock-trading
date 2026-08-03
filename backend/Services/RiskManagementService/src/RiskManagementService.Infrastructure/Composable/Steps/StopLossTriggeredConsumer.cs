using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;

// FR-10, FR-03, UC-02, ADR-0003, IADR-0015: 市場監視の損切りイベント（StopLossTriggered）を購読し、
// LLM を迂回して決済（Close）注文を機械的に発行する。損切りは無条件執行のためスクリーニングを通さない。
internal sealed class StopLossTriggeredConsumer(
    StopLossExecutionService executionService,
    ILogger<StopLossTriggeredConsumer> logger) : IConsumer<StopLossTriggered>
{
    public async Task Consume(ConsumeContext<StopLossTriggered> context)
    {
        var triggered = context.Message;
        var approval = executionService.BuildCloseApproval(triggered);

        // FR-11: 損切り実行を監査・通知の起点として記録する（永続監査は #17・通知は #15）。
        logger.LogWarning(
            "損切りの機械執行: {Symbol}/{Market} 建玉方向={PositionSide} 数量={Quantity} 損切り価格={StopLossPrice} → 決済 {CloseSide}",
            triggered.Symbol, triggered.Market, triggered.PositionSide, triggered.Quantity,
            triggered.StopLossPrice, approval.Intent.Side);

        await context.Publish(approval);
    }
}
