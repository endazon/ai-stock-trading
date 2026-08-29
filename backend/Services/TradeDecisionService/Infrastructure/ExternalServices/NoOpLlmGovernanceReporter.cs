using AiStockTrading.Shared.Contracts.Llm;
using TradeDecisionService.Features.TradeDecision;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, ADR-0017 決定2/決定4, #335, IADR-0216: 割当統制の記録の安全既定。外部へ publish せず何もしない。
// 実発行（LlmFallbackFired / TradeDecisionSkipped）は Worker が PublishingLlmGovernanceReporter を
// 明示配線したときのみ有効になる（NoOpLlmUsageReporter と同じ方針）。
//
// ⚠️ **記録が無くても「取引判断を見送る」統制自体は働く**（見送りは HttpLlmCompletionClient が Hold を返すことで
// 成立し、本ポートの実装に依存しない）。本ポートが担うのは可観測性であり、統制の可否ではない。
public sealed class NoOpLlmGovernanceReporter : ILlmGovernanceReporter
{
    public Task FallbackFiredAsync(
        LlmAssignmentEvaluation evaluation, string purpose, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DecisionSkippedAsync(
        string purpose, string reason, string? expectedModel, string? effectiveModel,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
