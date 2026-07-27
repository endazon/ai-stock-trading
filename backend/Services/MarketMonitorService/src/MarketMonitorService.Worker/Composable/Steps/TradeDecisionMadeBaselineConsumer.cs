using AiStockTrading.MarketMonitor.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.MarketMonitor.Worker.Composable.Steps;

// FR-03, UC-02: 取引判断の確定（TradeDecisionMade）を購読し、対象銘柄の基準値を「判断時点価格」へ更新する。
// これにより変動率が「前回 AI 判断時点価格比」で計算される（前日終値ではない。04_workflows/02）。
//
// IADR-0106, #258: クラス名に `Baseline`（本 consumer の関心事）を含めるのは命名の好みではなく機能要件である。
// MassTransit の既定エンドポイント名（キュー名）は consumer クラス名のみから導かれ namespace を含まないため、
// RiskManagementService の `TradeDecisionMadeConsumer` と同名にすると同一キュー `TradeDecisionMade` を
// 共有して competing consumer になり、取引判断を取り合って取りこぼす。名前でキューを分離する。
internal sealed class TradeDecisionMadeBaselineConsumer(
    IPriceBaselineStore baselineStore,
    ILogger<TradeDecisionMadeBaselineConsumer> logger) : IConsumer<TradeDecisionMade>
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
