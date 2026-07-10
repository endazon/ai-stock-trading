using AiStockTrading.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;
using AppSvc = AiStockTrading.TradeDecision.Application.Services.TradeDecisionService;

namespace AiStockTrading.TradeDecision.Worker.Composable.Steps;

// FR-04, UC-02, ADR-0003: 市場監視の価格変動イベント（PriceMovementDetected）を購読し、AI 判断を行う。
// 判断が成立（発注意図あり）した場合のみ TradeDecisionMade を発行し、リスク管理（#12）の検証へ渡す。
internal sealed class PriceMovementDetectedConsumer(
    AppSvc decisionService,
    ILogger<PriceMovementDetectedConsumer> logger) : IConsumer<PriceMovementDetected>
{
    public async Task Consume(ConsumeContext<PriceMovementDetected> context)
    {
        var decision = await decisionService.DecideAsync(context.Message, context.CancellationToken);
        if (decision is null)
        {
            // FR-07/安全既定: 方針なし・Hold・見送りは取引しない。
            return;
        }

        logger.LogInformation(
            "取引判断: DecisionId={DecisionId} {Symbol} {Side} 数量={Quantity}",
            decision.DecisionId, decision.Intent.Symbol, decision.Intent.Side, decision.Intent.Quantity);
        await context.Publish(decision);
    }
}
