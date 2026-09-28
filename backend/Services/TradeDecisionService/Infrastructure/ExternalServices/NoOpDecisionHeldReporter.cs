using AiStockTrading.Shared.Contracts.Events;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// #1077, IADR-0451 決定4: 既定の no-op（publish しない）。テストが判断サービスを直接組む場合のためであり、
// **本番では配線されない**（Program.cs が PublishingDecisionHeldReporter を登録する）。
public sealed class NoOpDecisionHeldReporter : IDecisionHeldReporter
{
    public Task ReportAsync(TradeDecisionHeld held, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
