using AiStockTrading.RiskManagement.Application.Ports;
using AiStockTrading.RiskManagement.Application.Services;
using AiStockTrading.Shared.Contracts.Events;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace AiStockTrading.RiskManagement.Infrastructure.Composable.Steps;

// FR-05, FR-10, FR-11, #292, IADR-0118: 発注執行が観測したブローカ建玉を購読し、取引台帳の射影と突き合わせる。
//
// 乖離は検知・記録・通知のみで**是正しない**（自動で建玉を合わせにいく発注経路は作らない・IADR-0118）。
// 一過性の未反映（発注後〜約定が台帳へ届くまで）で鳴らないよう、報告可否は PositionDriftTracker が
// 連続観測条件とシグネチャ dedup で決める。
//
// ADR-0013, IADR-0129, #354: MassTransit の IConsumer<BrokerPositionsObserved> から Wolverine のハンドラへ移行した。
public sealed class BrokerPositionsObservedHandler(
    IPortfolioLedgerStore ledger,
    PositionDriftTracker tracker,
    IClock clock,
    ILogger<BrokerPositionsObservedHandler> logger)
{
    public async Task Handle(BrokerPositionsObserved message, IMessageBus bus)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(bus);

        var ledgerPositions = PortfolioProjection.ProjectOpenPositions(ledger.GetFills());
        var drifts = PositionDriftDetector.Detect(ledgerPositions, message.Positions);

        if (!tracker.ShouldReport(drifts))
        {
            logger.LogDebug(
                "建玉突合: 乖離 {Count} 件（報告条件を満たさないため発行しません）。台帳 {Ledger} 件 / ブローカ {Broker} 件。",
                drifts.Count, ledgerPositions.Count, message.Positions.Count);
            return;
        }

        logger.LogWarning(
            "建玉の乖離を検知しました（{Count} 件）: {Summary}。是正は行いません（利用者の判断に委ねます）。",
            drifts.Count,
            string.Join(", ", drifts.Select(d => $"{d.Symbol}/{d.Market} 台帳{d.LedgerQuantity}≠ブローカ{d.BrokerQuantity}")));

        await bus.PublishAsync(new PositionReconciliationDrift(drifts, message.ObservedAt, clock.UtcNow))
            .ConfigureAwait(false);
    }
}
