using ReportService.Common.Abstractions;
using ReportService.Features.Reports;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Runtime;

namespace ReportService.Infrastructure.ExternalServices;

// NFR（費用）, FR-06, FR-16, 05_trading-assumptions §6.1, #347, IADR-0219:
// 報告書生成のトークン使用量に単価を適用し LlmCostIncurred を publish する。
//
// 🔴 **#282 の是正点。** これまで報告書散文の LLM 費用は**どこにも計上されていなかった**（計測点が無かった）。
// 計画 §6.1 は報告書生成を月次上限の**対象外**とする一方で、「月報に用途別の実測値を記載する」ことを求める。
// 対象外＝計上しない、ではない。上限へ積むか否かの判別は購読側（費用統制サービス）が purpose で行うため、
// ここでは**用途を必ず載せて**発行する。
//
// #303, IADR-0122 決定1: 単価は要求側の希望モデルではなく**応答が名乗った実効モデル**から引く。
// 未知モデルの倒し先（0 ではなく最大単価＝過小計上を作らない）は LlmPriceTable が持つ。
//
// ADR-0013, IADR-0129, #354: 本アダプタは singleton（HttpReportNarrativeDrafter が singleton のため）。
// IMessageBus は scoped なので、singleton の IWolverineRuntime から MessageBus を作って発行する
// （MessageBusReportDraftPresentedNotifier と同じ形）。
public sealed class PublishingLlmUsageReporter(
    IWolverineRuntime runtime,
    IClock clock,
    LlmPriceTable priceTable,
    ILogger<PublishingLlmUsageReporter> logger) : ILlmUsageReporter
{
    public async Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default)
    {
        var price = priceTable.Resolve(usage.Model);
        var amount = LlmPricing.Compute(usage.InputTokens, usage.OutputTokens, price);

        await new MessageBus(runtime)
            .PublishAsync(new LlmCostIncurred(amount, clock.UtcNow, usage.Purpose, usage.Model))
            .ConfigureAwait(false);

        logger.LogDebug(
            "報告書生成の LLM 費用計上イベントを発行 purpose={Purpose} model={Model} in={InputTokens} out={OutputTokens} amount={Amount}",
            usage.Purpose, usage.Model, usage.InputTokens, usage.OutputTokens, amount);
    }
}
