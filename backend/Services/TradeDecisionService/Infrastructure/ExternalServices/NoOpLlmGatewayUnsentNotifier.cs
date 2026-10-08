using AiStockTrading.Shared.Contracts.Llm;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, FR-09, #1267, IADR-0517: Sent=false の連続の通知の安全既定（何もしない）。
// Hold へ倒す統制は本ポートに依存しない（HttpLlmCompletionClient が担う）。
public sealed class NoOpLlmGatewayUnsentNotifier : ILlmGatewayUnsentNotifier
{
    public Task ReportUnsentAsync(
        string purpose, LlmGatewayUnsentCause cause, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task ReportSentAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
