using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;
using AppSvc = AiStockTrading.TradeDecision.Application.Services.TradeDecisionService;

namespace AiStockTrading.TradeDecision.Worker.Composable.Steps;

// FR-02, FR-04, UC-02, ADR-0003, IADR-0023: 市場監視の価格変動イベント（PriceMovementDetected）を購読し、AI 判断を行う
// （イベント駆動系統）。市場カレンダーで休場日（週末・祝日）はスキップする。判断が成立（発注意図あり）した場合のみ
// TradeDecisionMade を発行し、リスク管理（#12）の検証へ渡す。
internal sealed class PriceMovementDetectedConsumer(
    AppSvc decisionService,
    IMarketCalendar calendar,
    IClock clock,
    ILogger<PriceMovementDetectedConsumer> logger) : IConsumer<PriceMovementDetected>
{
    public async Task Consume(ConsumeContext<PriceMovementDetected> context)
    {
        var trigger = context.Message;
        if (!calendar.IsOpen(trigger.Market, clock.UtcNow))
        {
            // 休場日（週末・祝日）は価格変動サイクルを起動しない（祝日ガード）。
            logger.LogInformation("休場中のため価格変動サイクルをスキップ: {Symbol}", trigger.Symbol);
            return;
        }

        var decision = await decisionService.DecideAsync(trigger, context.CancellationToken);
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
