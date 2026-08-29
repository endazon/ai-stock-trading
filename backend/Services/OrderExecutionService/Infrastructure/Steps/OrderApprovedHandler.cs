using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using Microsoft.Extensions.Logging;
using Wolverine;
using AppSvc = OrderExecutionService.Features.OrderExecution.OrderExecutionAppService;

namespace OrderExecutionService.Infrastructure.Steps;

// FR-05, UC-01, UC-02, ADR-0003: リスク管理が承認した注文（OrderApproved・損切りの Close 含む）を購読し、
// ブローカへ発注して結果を OrderExecuted として発行する。発注ロジックは Application 層に委譲する。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<OrderApproved> から Wolverine のハンドラへ移行した。
// **本ハンドラは IADR-0129 決定 3（DisableConventionalLocalRouting）の直接の受益者である**:
// OrderApproved は発行元の RiskManagementService 自身も購読しており、Wolverine の既定のままだと発行が
// RiskManagement のプロセス内に閉じ、本サービスへ一通も届かない（＝発注が一件も執行されない）。
// IADR-0129 決定 9 によりハンドラ型は public sealed とする。
//
// NFR-07, #287, IADR-0255: 発注の健全性メトリクス（発注結果と、発注に届かなかった見送り）はここで計上する。
// **見送りは注文状態を持たない**ため、ブローカーの拒否（OrderStatus.Rejected）とは別の計器で数える。
public sealed class OrderApprovedHandler(
    AppSvc executionService,
    BusinessMetrics metrics,
    ILogger<OrderApprovedHandler> logger)
{
    public async Task Handle(OrderApproved message, IMessageBus bus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(bus);

        var result = await executionService.ExecuteAsync(message, cancellationToken).ConfigureAwait(false);

        // FR-05, ADR-0002（SPOF）, #331, IADR-0211: 見送り（OpenD 切断・逆指値を張れない Open）は
        // **例外を投げずに**正常終了する——投げると Wolverine の共通再試行がキューで再送し、
        // 「キューイングせず見送り」の裁定に反する。再発注は次の取引判断からのみ。
        if (result.Forgone is { } forgone)
        {
            metrics.RecordOrderDispatchForgone(forgone.Reason);
            logger.LogWarning(
                "発注見送り: DecisionId={DecisionId} 銘柄={Symbol} 理由={Reason}（再試行しません）",
                forgone.DecisionId, forgone.Intent.Symbol, forgone.Reason);
            await bus.PublishAsync(forgone).ConfigureAwait(false);
            return;
        }

        var executed = result.Executed!;
        metrics.RecordOrderExecuted(executed.Status, executed.Provider);
        logger.LogInformation(
            "発注執行: DecisionId={DecisionId} OrderId={OrderId} 状態={Status} 約定数={Filled} 平均価格={AvgPrice}",
            executed.DecisionId, executed.OrderId, executed.Status, executed.FilledQuantity, executed.AveragePrice);

        await bus.PublishAsync(executed).ConfigureAwait(false);

        // FR-10, #331, IADR-0210: 保護逆指値の結果。Placed はリスク管理が台帳の承認行へ結線し、
        // CoverageLost は監査・Critical 通知（および手仕舞いレグの台帳結線）へ流れる。
        if (result.StopPlaced is { } stopPlaced)
        {
            logger.LogInformation(
                "保護逆指値を発注: EntryDecisionId={EntryDecisionId} StopOrderId={StopOrderId} トリガー={Trigger} 試行={Attempt}",
                stopPlaced.EntryDecisionId, stopPlaced.StopOrderId, stopPlaced.TriggerPrice, stopPlaced.Attempt);
            await bus.PublishAsync(stopPlaced).ConfigureAwait(false);
        }

        if (result.CoverageLost is { } coverageLost)
        {
            logger.LogWarning(
                "保護逆指値が成立せず建玉解消: EntryDecisionId={EntryDecisionId} 銘柄={Symbol} 原因={Cause} 対処={Remediation} 数量={Quantity}",
                coverageLost.EntryDecisionId, coverageLost.Symbol, coverageLost.Cause,
                coverageLost.Remediation, coverageLost.Quantity);
            await bus.PublishAsync(coverageLost).ConfigureAwait(false);
        }
    }
}
