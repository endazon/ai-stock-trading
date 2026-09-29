using AiStockTrading.Shared.Contracts.Events;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// NFR, FR-04, FR-11, #1092, IADR-0462 決定4: LLM を呼ぶ前の見送りを TradeDecisionForgoneBeforeLlm として publish する。
// 監査サービスだけが購読して台帳へ記録する（通知しない）。
// ADR-0013, IADR-0129: 発行は Wolverine の IMessageBus（scoped）。PublishAsync は CancellationToken を取らない
// （PublishingDecisionHeldReporter と同じ形。見送りはハンドラを例外で終わらせないので、処理の成功とともに外へ出る）。
internal sealed class PublishingDecisionForgoneBeforeLlmReporter(
    IMessageBus bus,
    ILogger<PublishingDecisionForgoneBeforeLlmReporter> logger) : IDecisionForgoneBeforeLlmReporter
{
    public async Task ReportAsync(TradeDecisionForgoneBeforeLlm forgone, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(forgone);
        await bus.PublishAsync(forgone).ConfigureAwait(false);
        logger.LogDebug(
            "LLM を呼ぶ前の見送りを発行（監査台帳へ記録）: {Symbol}/{Market} reason={Reason}",
            forgone.Symbol, forgone.Market, forgone.Reason);
    }
}
