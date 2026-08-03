using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;

// FR-10, UC-01, UC-02, ADR-0003: 取引判断（TradeDecisionMade）を購読し、発注前に決定的に検証して
// OrderApproved / OrderRejected を発行する。判定ロジックは Application 層の OrderScreeningService に委譲する。
internal sealed class TradeDecisionMadeConsumer(
    OrderScreeningService screeningService,
    ILogger<TradeDecisionMadeConsumer> logger)
    : IConsumer<TradeDecisionMade>
{
    public async Task Consume(ConsumeContext<TradeDecisionMade> context)
    {
        var decision = context.Message;
        var outcome = screeningService.Screen(decision);

        if (outcome.IsApproved)
        {
            logger.LogInformation(
                "注文承認: DecisionId={DecisionId} 数量={Quantity}",
                decision.DecisionId, outcome.Approved!.ApprovedQuantity);
            await context.Publish(outcome.Approved!);
        }
        else
        {
            // FR-11: 拒否理由は監査・通知のため理由列挙つきで発行する。
            logger.LogWarning(
                "注文拒否: DecisionId={DecisionId} 理由={Reasons}",
                decision.DecisionId, string.Join(",", outcome.Rejected!.Reasons));
            await context.Publish(outcome.Rejected!);
        }
    }
}
