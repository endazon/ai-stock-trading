using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Infrastructure.Composable.Llm;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace TradeDecisionService.Infrastructure.ExternalServices;

// NFR（費用）, FR-04, IADR-0055 決定2/3: トークン使用量に単価を適用し LlmCostIncurred を publish する。
// 単価既定 0（未設定）でも publish する: 金額 0 は統制判定に無害で、計上経路の健全性を保てるため（IADR-0055 根拠）。
// 費用統制サービスが購読して月次台帳へ計上する（HTTP /costs/record は OwnerOnly のため使わない）。
// #303, IADR-0122: 単価は global 単一ペアではなく**応答が名乗った実効モデル**から引く（用途別モデル割当により
// trade-decision=sonnet-5 / report-*=fable-5・opus-5・sonnet-5 と混在するため）。未知モデルの倒し先は
// LlmPriceTable が持つ（0 ではなく最大単価＝過小計上を作らない）。
//
// NFR（費用）, 05_trading-assumptions §6.1, #347, IADR-0218: **用途（purpose）を必ず載せる。**
// 月次 LLM 費用上限（15,000 円）の対象は取引判断サイクルのみであり、対象範囲の判別は購読側
// （LlmCostIncurredHandler）が purpose だけを見て行う。載せ忘れると上限側へ倒れる（過小計上を作らない既定）。
//
// 🔴 #335, IADR-0212: 用途は**計測ごと**（`usage.Purpose`）に受け取る。構築時に固定していた頃は、二段判断の
// 一次スクリーニング（trade-decision-screening）の費用が本判断（trade-decision）として積まれ、
// **層別の内訳が取れなかった**。計上側が purpose を決めてはならない —— 決めてよいのは egress だけである。
public sealed class PublishingLlmUsageReporter(
    IMessageBus bus,
    IClock clock,
    LlmPriceTable priceTable,
    ILogger<PublishingLlmUsageReporter> logger) : ILlmUsageReporter
{
    public async Task ReportAsync(LlmUsage usage, CancellationToken cancellationToken = default)
    {
        var price = priceTable.Resolve(usage.Model);
        var amount = LlmPricing.Compute(usage.InputTokens, usage.OutputTokens, price);
        // ADR-0013, IADR-0129, #354: 発行は Wolverine の IMessageBus（scoped）。PublishAsync は CancellationToken を取らない。
        await bus.PublishAsync(new LlmCostIncurred(amount, clock.UtcNow, usage.Purpose, usage.Model)).ConfigureAwait(false);
        logger.LogDebug(
            "LLM 費用計上イベントを発行 purpose={Purpose} model={Model} in={InputTokens} out={OutputTokens} amount={Amount}",
            usage.Purpose, usage.Model, usage.InputTokens, usage.OutputTokens, amount);
    }
}
