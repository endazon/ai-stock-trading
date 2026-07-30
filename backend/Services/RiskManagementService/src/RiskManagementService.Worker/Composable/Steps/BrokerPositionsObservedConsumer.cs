using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.Shared.Contracts.Events;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.RiskManagement.Worker.Composable.Steps;

// FR-05, FR-10, FR-11, #292, IADR-0118: 発注執行が観測したブローカ建玉を購読し、取引台帳の射影と突き合わせる。
//
// 乖離は検知・記録・通知のみで**是正しない**（自動で建玉を合わせにいく発注経路は作らない・IADR-0118）。
// 一過性の未反映（発注後〜約定が台帳へ届くまで）で鳴らないよう、報告可否は PositionDriftTracker が
// 連続観測条件とシグネチャ dedup で決める。
internal sealed class BrokerPositionsObservedConsumer(
    IPortfolioLedgerStore ledger,
    PositionDriftTracker tracker,
    IClock clock,
    ILogger<BrokerPositionsObservedConsumer> logger)
    : IConsumer<BrokerPositionsObserved>
{
    public async Task Consume(ConsumeContext<BrokerPositionsObserved> context)
    {
        var observed = context.Message;

        var ledgerPositions = PortfolioProjection.ProjectOpenPositions(ledger.GetFills());
        var drifts = PositionDriftDetector.Detect(ledgerPositions, observed.Positions);

        if (!tracker.ShouldReport(drifts))
        {
            logger.LogDebug(
                "建玉突合: 乖離 {Count} 件（報告条件を満たさないため発行しません）。台帳 {Ledger} 件 / ブローカ {Broker} 件。",
                drifts.Count, ledgerPositions.Count, observed.Positions.Count);
            return;
        }

        logger.LogWarning(
            "建玉の乖離を検知しました（{Count} 件）: {Summary}。是正は行いません（利用者の判断に委ねます）。",
            drifts.Count,
            string.Join(", ", drifts.Select(d => $"{d.Symbol}/{d.Market} 台帳{d.LedgerQuantity}≠ブローカ{d.BrokerQuantity}")));

        await context.Publish(new PositionReconciliationDrift(drifts, observed.ObservedAt, clock.UtcNow));
    }
}
