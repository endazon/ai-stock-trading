using AiStockTrading.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;
using AppSvc = AiStockTrading.OrderExecution.Application.Services.OrderExecutionService;

namespace AiStockTrading.OrderExecution.Worker.Composable.Steps;

// FR-05, UC-01, UC-02, ADR-0003: リスク管理が承認した注文（OrderApproved・損切りの Close 含む）を購読し、
// ブローカへ発注して結果を OrderExecuted として発行する。発注ロジックは Application 層に委譲する。
internal sealed class OrderApprovedConsumer(
    AppSvc executionService,
    ILogger<OrderApprovedConsumer> logger) : IConsumer<OrderApproved>
{
    public async Task Consume(ConsumeContext<OrderApproved> context)
    {
        var executed = await executionService.ExecuteAsync(context.Message, context.CancellationToken);

        logger.LogInformation(
            "発注執行: DecisionId={DecisionId} OrderId={OrderId} 状態={Status} 約定数={Filled} 平均価格={AvgPrice}",
            executed.DecisionId, executed.OrderId, executed.Status, executed.FilledQuantity, executed.AveragePrice);

        await context.Publish(executed);
    }
}
