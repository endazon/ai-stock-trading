using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;

// FR-10, FR-03, UC-02, ADR-0003, IADR-0015: 市場監視の損切りイベント（StopLossTriggered）を購読し、
// LLM を迂回して決済（Close）注文を機械的に発行する。損切りは無条件執行のためスクリーニングを通さない。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<StopLossTriggered> から Wolverine のハンドラへ移行した。
// ここで発行する OrderApproved は本サービス自身も購読しているため、IADR-0129 決定 3 が無いと
// 発注執行へ届かず損切りが執行されない（TradeDecisionMadeHandler と同じ経路）。
public sealed class StopLossTriggeredHandler(
    StopLossExecutionService executionService,
    ILogger<StopLossTriggeredHandler> logger)
{
    public async Task Handle(StopLossTriggered message, IMessageBus bus)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(bus);

        var approval = executionService.BuildCloseApproval(message);

        // FR-11: 損切り実行を監査・通知の起点として記録する（永続監査は #17・通知は #15）。
        logger.LogWarning(
            "損切りの機械執行: {Symbol}/{Market} 建玉方向={PositionSide} 数量={Quantity} 損切り価格={StopLossPrice} → 決済 {CloseSide}",
            message.Symbol, message.Market, message.PositionSide, message.Quantity,
            message.StopLossPrice, approval.Intent.Side);

        await bus.PublishAsync(approval).ConfigureAwait(false);
    }
}
