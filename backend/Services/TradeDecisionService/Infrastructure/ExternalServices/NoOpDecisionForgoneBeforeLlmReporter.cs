using AiStockTrading.Shared.Contracts.Events;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// #1092, IADR-0462 決定4: 既定の no-op（publish しない）。テストが判断サービスを直接組む場合のためであり、
// **本番では配線されない**（Program.cs が PublishingDecisionForgoneBeforeLlmReporter を登録する）。
public sealed class NoOpDecisionForgoneBeforeLlmReporter : IDecisionForgoneBeforeLlmReporter
{
    public Task ReportAsync(TradeDecisionForgoneBeforeLlm forgone, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
