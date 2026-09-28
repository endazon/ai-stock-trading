using AiStockTrading.Shared.Contracts.Events;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// UC-02, FR-03, #1077, IADR-0452 決定4: 判断後の見送りを TradeDecisionHeld として publish する。
// 市場監視が購読して急変の基準値を判断時点の価格へ進め、監査サービスが台帳へ記録する。
// ADR-0013, IADR-0129: 発行は Wolverine の IMessageBus（scoped）。PublishAsync は CancellationToken を取らない。
internal sealed class PublishingDecisionHeldReporter(
    IMessageBus bus,
    ILogger<PublishingDecisionHeldReporter> logger) : IDecisionHeldReporter
{
    public async Task ReportAsync(TradeDecisionHeld held, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(held);
        await bus.PublishAsync(held).ConfigureAwait(false);
        logger.LogInformation(
            "判断後の見送りを発行（急変の基準値を進める）: {Symbol}/{Market} price={Price} reason={Reason}",
            held.Symbol, held.Market, held.Price, held.Reason);
    }
}
