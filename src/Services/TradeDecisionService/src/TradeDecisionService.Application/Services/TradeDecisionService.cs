using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.State;
using AiStockTrading.TradeDecision.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Application.Services;

// FR-04, FR-07, FR-10, FR-11, UC-01, UC-02, ADR-0003, IADR-0003/0004/0017: 取引判断の中核。
// トリガー → 確定済み日報の方針＋リスク制約で LLM 判断 → 構造化解析 → PositionSizer で数量確定 → TradeDecisionMade。
// 安全既定: 確定済み日報なし / Hold / 数量 0 は取引しない（発注意図を作らない）。
public sealed class TradeDecisionService(
    ILlmCompletionClient llm,
    IDailyPolicyProvider policyProvider,
    ISizingContextProvider sizingProvider,
    IClock clock,
    ILogger<TradeDecisionService> logger)
{
    // 価格変動イベント（イベント駆動系統）の起点。DecisionTrigger へ写像して合流する。
    public Task<TradeDecisionMade?> DecideAsync(
        PriceMovementDetected trigger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        return DecideAsync(DecisionTrigger.FromPriceMovement(trigger), cancellationToken);
    }

    // FR-02, IADR-0023: 定時・イベント両系統の合流点。DecisionTrigger を受けて同一ロジックで判断する。
    public async Task<TradeDecisionMade?> DecideAsync(
        DecisionTrigger trigger, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trigger);

        // FR-07: 確定済み日報の方針が無ければ取引しない（確定前方針は不適用）。IADR-0028: 報告書サービスを同期照会（依存先障害は null）。
        var policy = await policyProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            logger.LogInformation("確定済み日報の方針が無いため取引しない: {Symbol}", trigger.Symbol);
            return null;
        }

        var context = await sizingProvider.GetContextAsync(cancellationToken).ConfigureAwait(false);
        var prompt = TradeDecisionPromptBuilder.Build(trigger, policy, context);
        var output = await llm.CompleteAsync(prompt, cancellationToken).ConfigureAwait(false);
        var decision = TradeDecisionParser.Parse(output);

        // FR-11: プロンプト・LLM 出力・根拠を記録する（永続監査は #17 連携）。
        logger.LogInformation(
            "LLM 判断: {Symbol} action={Action} rationale={Rationale}",
            trigger.Symbol, decision.Action, decision.Rationale);

        if (decision.Action == TradeAction.Hold)
        {
            return null; // 見送り
        }

        var side = decision.Action == TradeAction.Buy ? TradeSide.Buy : TradeSide.Sell;

        // IADR-0003: サイジングは判断サービスの責務。availableCapital は段階残枠と日次発注残枠の小さい方（IADR-0017）。
        var sizeFactor = PositionSizer.GetSizeFactor(context.ConsecutiveLosses, context.DrawdownRatio, context.Limits);
        var availableCapital = Math.Max(0m, Math.Min(context.StageCapitalRemaining, context.DailyOrderRemaining));
        var quantity = PositionSizer.CalculateCappedQuantity(
            context.Capital,
            context.Limits.PerTradeRiskRatio,
            decision.StopLossDistancePerShare,
            decision.ReferencePrice,
            context.Limits.MaxOrderAmount,
            availableCapital,
            sizeFactor);

        if (quantity <= 0)
        {
            logger.LogInformation("サイジングで数量 0 のため見送り: {Symbol}", trigger.Symbol);
            return null;
        }

        // FR-03/04, IADR-0035: 損切り価格を算出して発注意図に載せる（#63 台帳へ永続化し市場監視の損切り検知に実値供給）。
        // ロングは参照価格より下、ショートは上に損切りラインを置く（StopLossEvaluator と対称）。
        var stopLossPrice = side == TradeSide.Buy
            ? decision.ReferencePrice - decision.StopLossDistancePerShare
            : decision.ReferencePrice + decision.StopLossDistancePerShare;

        // IADR-0004: 発注意図には PositionEffect を必ず設定する。判断由来は新規建て（Open）。
        var intent = new OrderIntent(
            trigger.Symbol,
            trigger.Market,
            side,
            ProductType.Cash,
            context.Mode,
            quantity,
            decision.ReferencePrice,
            PositionEffect.Open,
            stopLossPrice);

        return new TradeDecisionMade(Guid.NewGuid(), intent, decision.Rationale, clock.UtcNow);
    }
}
