using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// FR-04, FR-09, FR-11, ADR-0017 決定2/決定4, #335, IADR-0216/0217:
// 割当統制の事実を publish する実装。AuditService（台帳＝月報集計の供給元）と
// NotificationService（Discord 警告）が購読する。
//
// ADR-0013, IADR-0129, #354: 発行は Wolverine の IMessageBus（scoped）。PublishAsync は CancellationToken を取らない。
// 本アダプタは HttpLlmCompletionClient（scoped）から呼ばれるため scoped で登録してよい
// （PublishingLlmUsageReporter と同じ形）。
public sealed class PublishingLlmGovernanceReporter(
    IMessageBus bus,
    IClock clock,
    ILogger<PublishingLlmGovernanceReporter> logger) : ILlmGovernanceReporter
{
    public async Task FallbackFiredAsync(
        LlmAssignmentEvaluation evaluation, string purpose, CancellationToken cancellationToken = default)
    {
        await bus.PublishAsync(new LlmFallbackFired(
            purpose, evaluation.ExpectedModel, evaluation.EffectiveModel, evaluation.Outcome.ToString(), clock.UtcNow))
            .ConfigureAwait(false);

        logger.LogWarning(
            "LLM の割当どおりのモデルで応答していません purpose={Purpose} expected={Expected} effective={Effective} outcome={Outcome}",
            purpose, evaluation.ExpectedModel, evaluation.EffectiveModel, evaluation.Outcome);
    }

    public async Task DecisionSkippedAsync(
        string purpose, string reason, string? expectedModel, string? effectiveModel,
        CancellationToken cancellationToken = default)
    {
        await bus.PublishAsync(new TradeDecisionSkipped(purpose, reason, expectedModel, effectiveModel, clock.UtcNow))
            .ConfigureAwait(false);

        // 🔴 ログレベルは Warning に留める（Error にしない）。ADR-0017 決定2 のとおり見送りは**正常な結果**であり、
        // Error で出すと運用が「障害」として扱い、善意のフォールバック追加を招く。
        logger.LogWarning(
            "割当モデルが使えないため取引判断を見送りました（発注も行いません。設計上の正常な結果です） purpose={Purpose} reason={Reason} expected={Expected} effective={Effective}",
            purpose, reason, expectedModel, effectiveModel);
    }
}
