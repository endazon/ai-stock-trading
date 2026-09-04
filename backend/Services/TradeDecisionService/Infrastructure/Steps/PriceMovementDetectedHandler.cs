using System.Diagnostics;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using Microsoft.Extensions.Logging;
using Wolverine;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Infrastructure.Steps;

// FR-02, FR-04, UC-02, ADR-0003, IADR-0023: 市場監視の価格変動イベント（PriceMovementDetected）を購読し、AI 判断を行う
// （イベント駆動系統）。市場カレンダーで休場日（週末・祝日）はスキップする。判断が成立（発注意図あり）した場合のみ
// TradeDecisionMade を発行し、リスク管理（#12）の検証へ渡す。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<PriceMovementDetected> から Wolverine のハンドラへ移行した。
// ConsumeContext<T> は消え、メッセージ本体・IMessageBus・CancellationToken をメソッド引数で受け取る。
// IADR-0129 決定 9 によりハンドラ型は public sealed とする（Wolverine は public でない型を受け付けない）。
//
// NFR-07, #287, IADR-0255: 取引サイクルの健全性メトリクス（判断回数・内訳・レイテンシ）はここで計上する。
// **依存は必須にする**——省略可能引数にすると Program.cs から配線が消えてもコンパイルが通り、
// テストは全緑のまま計上だけが静かに止まる（IADR-0163 決定2 と同じ規律）。
public sealed class PriceMovementDetectedHandler(
    AppSvc decisionService,
    IMarketCalendar calendar,
    IClock clock,
    BusinessMetrics metrics,
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

        // NFR-01, #689, IADR-0307: 端点間レイテンシの起点は **message.DetectedAt**（検知時刻）である。
        // DecisionTrigger.FromPriceMovement が起点として載せ、TradeDecisionMade → OrderApproved →
        // OrderExecuted と運ばれる。ここで現在時刻へ置き換えると、検知から配送までの区間が計測から消える。
        //
        // NFR-07, #287: 判断の所要は「判断が成立したか」に関わらず計上する（見送りも 1 回の判断である）。
        var started = Stopwatch.GetTimestamp();
        var decision = await decisionService.DecideAsync(message, cancellationToken).ConfigureAwait(false);
        metrics.RecordTradeDecisionDuration(
            BusinessMetrics.TriggerPriceMovement, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        metrics.RecordTradeDecision(BusinessMetrics.TriggerPriceMovement, decision?.Intent.Side);

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
