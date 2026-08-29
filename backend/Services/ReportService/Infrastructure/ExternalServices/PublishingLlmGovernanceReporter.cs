using ReportService.Common.Abstractions;
using ReportService.Features.Reports;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Runtime;

namespace ReportService.Infrastructure.ExternalServices;

// FR-06, FR-09, FR-11, ADR-0017 決定4-(2)/(3), #335, IADR-0217:
// 報告書生成でピン以外のモデルが応答した事実を publish する。NotificationService が警告として通知し（②）、
// AuditService が台帳へ記録する（③月報集計の供給元）。
//
// ADR-0013, IADR-0129, #354: 本アダプタは singleton（HttpReportNarrativeDrafter が singleton のため）。
// IMessageBus は scoped なので singleton の IWolverineRuntime から MessageBus を作る。
public sealed class PublishingLlmGovernanceReporter(
    IWolverineRuntime runtime,
    IClock clock,
    ILogger<PublishingLlmGovernanceReporter> logger) : ILlmGovernanceReporter
{
    public async Task FallbackFiredAsync(
        LlmAssignmentEvaluation evaluation, string purpose, CancellationToken cancellationToken = default)
    {
        await new MessageBus(runtime)
            .PublishAsync(new LlmFallbackFired(
                purpose, evaluation.ExpectedModel, evaluation.EffectiveModel, evaluation.Outcome.ToString(), clock.UtcNow))
            .ConfigureAwait(false);

        logger.LogWarning(
            "報告書散文が割当どおりのモデルで生成されていません purpose={Purpose} expected={Expected} effective={Effective} outcome={Outcome}",
            purpose, evaluation.ExpectedModel, evaluation.EffectiveModel, evaluation.Outcome);
    }
}
