using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;
using Wolverine;
using AppSvc = AiStockTrading.TradeDecision.Application.Services.TradeDecisionService;

namespace AiStockTrading.TradeDecision.Infrastructure.Composable.Steps;

// FR-02, FR-04, UC-02, ADR-0003, IADR-0023: 市場監視の価格変動イベント（PriceMovementDetected）を購読し、AI 判断を行う
// （イベント駆動系統）。市場カレンダーで休場日（週末・祝日）はスキップする。判断が成立（発注意図あり）した場合のみ
// TradeDecisionMade を発行し、リスク管理（#12）の検証へ渡す。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<PriceMovementDetected> から Wolverine のハンドラへ移行した。
// ConsumeContext<T> は消え、メッセージ本体・IMessageBus・CancellationToken をメソッド引数で受け取る。
// IADR-0129 決定 9 によりハンドラ型は public sealed とする（Wolverine は public でない型を受け付けない）。
public sealed class PriceMovementDetectedHandler(
    AppSvc decisionService,
    IMarketCalendar calendar,
    IClock clock,
    ILogger<PriceMovementDetectedHandler> logger)
{
    public async Task Handle(PriceMovementDetected message, IMessageBus bus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(bus);

        if (!calendar.IsOpen(message.Market, clock.UtcNow))
        {
            // 休場日（週末・祝日）は価格変動サイクルを起動しない（祝日ガード）。
            logger.LogInformation("休場中のため価格変動サイクルをスキップ: {Symbol}", message.Symbol);
            return;
        }

        var decision = await decisionService.DecideAsync(message, cancellationToken).ConfigureAwait(false);
        if (decision is null)
        {
            // FR-07/安全既定: 方針なし・Hold・見送りは取引しない。
            return;
        }

        logger.LogInformation(
            "取引判断: DecisionId={DecisionId} {Symbol} {Side} 数量={Quantity}",
            decision.DecisionId, decision.Intent.Symbol, decision.Intent.Side, decision.Intent.Quantity);
        await bus.PublishAsync(decision).ConfigureAwait(false);
    }
}
