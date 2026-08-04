using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;
using Wolverine;
using AppSvc = AiStockTrading.OrderExecution.Application.Services.OrderExecutionService;

namespace AiStockTrading.OrderExecution.Infrastructure.Composable.Steps;

// FR-05, UC-01, UC-02, ADR-0003: リスク管理が承認した注文（OrderApproved・損切りの Close 含む）を購読し、
// ブローカへ発注して結果を OrderExecuted として発行する。発注ロジックは Application 層に委譲する。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<OrderApproved> から Wolverine のハンドラへ移行した。
// **本ハンドラは IADR-0129 決定 3（DisableConventionalLocalRouting）の直接の受益者である**:
// OrderApproved は発行元の RiskManagementService 自身も購読しており、Wolverine の既定のままだと発行が
// RiskManagement のプロセス内に閉じ、本サービスへ一通も届かない（＝発注が一件も執行されない）。
// IADR-0129 決定 9 によりハンドラ型は public sealed とする。
public sealed class OrderApprovedHandler(
    AppSvc executionService,
    ILogger<OrderApprovedHandler> logger)
{
    public async Task Handle(OrderApproved message, IMessageBus bus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(bus);

        var executed = await executionService.ExecuteAsync(message, cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "発注執行: DecisionId={DecisionId} OrderId={OrderId} 状態={Status} 約定数={Filled} 平均価格={AvgPrice}",
            executed.DecisionId, executed.OrderId, executed.Status, executed.FilledQuantity, executed.AveragePrice);

        await bus.PublishAsync(executed).ConfigureAwait(false);
    }
}
