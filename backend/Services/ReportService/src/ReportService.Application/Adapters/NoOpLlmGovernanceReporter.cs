using AiStockTrading.Report.Application.Ports;
using AiStockTrading.Shared.Contracts.Llm;

namespace AiStockTrading.Report.Application.Adapters;

// FR-06, ADR-0017 決定4, #335, IADR-0217: 割当逸脱の通知の安全既定。外部へ publish せず何もしない。
// 実発行（LlmFallbackFired）は Worker が発行実装を明示配線したときのみ有効になる。
public sealed class NoOpLlmGovernanceReporter : ILlmGovernanceReporter
{
    public Task FallbackFiredAsync(
        LlmAssignmentEvaluation evaluation, string purpose, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
