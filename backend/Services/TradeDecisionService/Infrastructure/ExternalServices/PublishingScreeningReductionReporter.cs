using AiStockTrading.Shared.Contracts.Events;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-02, FR-04, FR-06, FR-11, #337, IADR-0247: スクリーニング入力の縮退発生を ScreeningContextReduced として
// publish する。監査サービスが台帳へ記録し、月報の期間集計（分割と切り詰めを分けて数える）の集計経路になる。
// ADR-0013, IADR-0129, #354: 発行は Wolverine の IMessageBus（scoped）。PublishAsync は CancellationToken を取らない。
internal sealed class PublishingScreeningReductionReporter(
    IMessageBus bus,
    ILogger<PublishingScreeningReductionReporter> logger) : IScreeningReductionReporter
{
    public async Task ReportAsync(ScreeningContextReduced reduction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reduction);
        await bus.PublishAsync(reduction).ConfigureAwait(false);
        logger.LogWarning(
            "スクリーニング入力の縮退を記録: symbols={Symbols} batches={Batches} split={Split} "
                + "droppedRag={DroppedRag} droppedNews={DroppedNews} unresolvable={Unresolvable}",
            string.Join(",", reduction.Symbols), reduction.BatchCount, reduction.Split,
            reduction.DroppedRagCount, reduction.DroppedNewsCount, reduction.UnresolvableOverflow);
    }
}
