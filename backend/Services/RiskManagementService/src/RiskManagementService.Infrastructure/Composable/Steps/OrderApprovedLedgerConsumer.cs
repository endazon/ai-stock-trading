using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;

// FR-10, FR-05, IADR-0018: 承認済み注文（OrderApproved）を購読し、Intent（銘柄・方向・建玉効果）を DecisionId で
// 取引台帳に記録する。後続の OrderExecuted（銘柄・方向を持たない）を DecisionId で相関して補完するための土台。
// 通常経路（TradeDecisionMadeConsumer）・損切り機械執行（StopLossExecutionService）の両方の承認を統一的に取り込む。
internal sealed class OrderApprovedLedgerConsumer(
    IPortfolioLedgerStore ledger,
    ILogger<OrderApprovedLedgerConsumer> logger)
    : IConsumer<OrderApproved>
{
    public Task Consume(ConsumeContext<OrderApproved> context)
    {
        var m = context.Message;
        // 冪等（同一 DecisionId の再送は無視）はストア側で担保する。
        ledger.AppendApproval(m.DecisionId, m.Intent, m.ApprovedAt);
        logger.LogDebug(
            "台帳に承認を記録: DecisionId={DecisionId} 銘柄={Symbol} 効果={Effect}",
            m.DecisionId, m.Intent.Symbol, m.Intent.PositionEffect);
        return Task.CompletedTask;
    }
}
