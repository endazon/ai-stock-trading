using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.MarketMonitor.Worker.Composable.Steps;

// FR-03, UC-02: 取引判断の確定（TradeDecisionMade）を購読し、対象銘柄の基準値を「判断時点価格」へ更新する。
// これにより変動率が「前回 AI 判断時点価格比」で計算される（前日終値ではない。04_workflows/02）。
internal sealed class TradeDecisionMadeConsumer(
    IPriceBaselineStore baselineStore,
    ILogger<TradeDecisionMadeConsumer> logger) : IConsumer<TradeDecisionMade>
{
    public Task Consume(ConsumeContext<TradeDecisionMade> context)
    {
        var intent = context.Message.Intent;
        baselineStore.SetBaseline(intent.Symbol, intent.Market, intent.Price);
        logger.LogInformation(
            "基準値を更新: {Symbol}/{Market} = {Price}", intent.Symbol, intent.Market, intent.Price);
        return Task.CompletedTask;
    }
}
