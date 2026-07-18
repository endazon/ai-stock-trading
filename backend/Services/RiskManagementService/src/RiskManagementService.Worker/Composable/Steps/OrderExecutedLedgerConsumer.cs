using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.RiskManagement.Worker.Composable.Steps;

// FR-10, FR-05, IADR-0018: 約定（OrderExecuted）を購読し、取引台帳へ記録する。銘柄・方向は先行の承認（OrderApproved）を
// DecisionId で相関して補完する。全量約定（Filled）のみを記録する（ペーパー経路は単一 Filled を発行。部分約定＝実ブローカーの
// 累積更新は moomoo 実発注（ADR-0002・#132）の解禁後に対応する）。冪等（同一 OrderId の再送は無視）はストア側で担保する。
internal sealed class OrderExecutedLedgerConsumer(
    IPortfolioLedgerStore ledger,
    ILogger<OrderExecutedLedgerConsumer> logger)
    : IConsumer<OrderExecuted>
{
    public Task Consume(ConsumeContext<OrderExecuted> context)
    {
        var m = context.Message;

        // 約定していない結果（受付・失注・取消・拒否・部分約定）は台帳に載せない。
        if (m.Status != OrderStatus.Filled || m.FilledQuantity <= 0)
            return Task.CompletedTask;

        var recorded = ledger.AppendFill(m.DecisionId, m.OrderId, m.FilledQuantity, m.AveragePrice, m.ExecutedAt);
        if (!recorded)
        {
            // 相関する承認が台帳に無い異常（承認は約定に先行して発行されるため通常は発生しない）。
            logger.LogWarning(
                "約定に相関する承認が台帳に無いため記録をスキップ: DecisionId={DecisionId} OrderId={OrderId}",
                m.DecisionId, m.OrderId);
        }

        return Task.CompletedTask;
    }
}
