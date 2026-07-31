using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using AiStockTrading.TradeDecision.Application.Ports;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Worker.Composable.Adapters;

// NFR（費用）, FR-04, IADR-0055 決定2/3: トークン使用量に単価を適用し LlmCostIncurred を publish する。
// 単価既定 0（未設定）でも publish する: 金額 0 は統制判定に無害で、計上経路の健全性を保てるため（IADR-0055 根拠）。
// 費用統制サービスが購読して月次台帳へ計上する（HTTP /costs/record は OwnerOnly のため使わない）。
// #303, IADR-0122: 単価は global 単一ペアではなく**応答が名乗った実効モデル**から引く（用途別モデル割当により
// trade-decision=sonnet-5 / report-*=fable-5・opus-5・sonnet-5 と混在するため）。未知モデルの倒し先は
// LlmPriceTable が持つ（0 ではなく最大単価＝過小計上を作らない）。
internal sealed class PublishingLlmUsageReporter(
    IPublishEndpoint publishEndpoint,
    IClock clock,
    LlmPriceTable priceTable,
    ILogger<PublishingLlmUsageReporter> logger) : ILlmUsageReporter
{
    public async Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default)
    {
        var price = priceTable.Resolve(usage.Model);
        var amount = LlmPricing.Compute(usage.InputTokens, usage.OutputTokens, price);
        await publishEndpoint.Publish(new LlmCostIncurred(amount, clock.UtcNow), cancellationToken).ConfigureAwait(false);
        logger.LogDebug("LLM 費用計上イベントを発行 model={Model} in={InputTokens} out={OutputTokens} amount={Amount}",
            usage.Model, usage.InputTokens, usage.OutputTokens, amount);
    }
}
