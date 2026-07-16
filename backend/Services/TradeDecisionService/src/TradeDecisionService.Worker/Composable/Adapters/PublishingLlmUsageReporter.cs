using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Domain;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Worker.Composable.Adapters;

// NFR（費用）, FR-04, IADR-0055 決定2/3: トークン使用量に単価を適用し LlmCostIncurred を publish する。
// 単価既定 0（未設定）でも publish する: 金額 0 は統制判定に無害で、計上経路の健全性を保てるため（IADR-0055 根拠）。
// 費用統制サービスが購読して月次台帳へ計上する（HTTP /costs/record は OwnerOnly のため使わない）。
internal sealed class PublishingLlmUsageReporter(
    IPublishEndpoint publishEndpoint,
    IClock clock,
    decimal inputPer1kTokens,
    decimal outputPer1kTokens,
    ILogger<PublishingLlmUsageReporter> logger) : ILlmUsageReporter
{
    public async Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default)
    {
        var amount = LlmPricing.Compute(usage.InputTokens, usage.OutputTokens, inputPer1kTokens, outputPer1kTokens);
        await publishEndpoint.Publish(new LlmCostIncurred(amount, clock.UtcNow), cancellationToken).ConfigureAwait(false);
        logger.LogDebug("LLM 費用計上イベントを発行 in={InputTokens} out={OutputTokens} amount={Amount}",
            usage.InputTokens, usage.OutputTokens, amount);
    }
}
