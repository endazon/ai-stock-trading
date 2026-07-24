using AiStockTrading.RiskManagement.Domain;
using AiStockTrading.TradeDecision.Application.Adapters;
using AiStockTrading.TradeDecision.Application.Ports;
using AiStockTrading.TradeDecision.Application.State;
using AiStockTrading.TradeDecision.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Trading;
using Microsoft.Extensions.Logging;

namespace AiStockTrading.TradeDecision.Application.Services;

// FR-04, FR-07, FR-10, FR-11, UC-01, UC-02, ADR-0003, IADR-0003/0004/0017/0037: 取引判断の中核。
// トリガー → 確定済み日報の方針＋リスク制約で LLM 判断（多数決・二段オーケストレーション・IADR-0039）→ 構造化解析
// → PositionSizer で数量確定 → TradeDecisionMade。
// 安全既定: 確定済み日報なし / Hold / 数量 0 は取引しない（発注意図を作らない）。
// options 未指定なら DecisionOrchestrationOptions.Default（1 票・スクリーニング無効）＝単発判断（IADR-0017）と等価。
public sealed class TradeDecisionService(
    ILlmCompletionClient llm,
    IDailyPolicyProvider policyProvider,
    ISizingContextProvider sizingProvider,
    IClock clock,
    ILogger<TradeDecisionService> logger,
    IRetrievalContextProvider? retrieval = null,
    DecisionOrchestrationOptions? options = null,
    IProfitabilityAssumptionsProvider? profitability = null,
    ProfitabilityGateOptions? profitabilityOptions = null,
    IDailyPolicyUnconfirmedNotifier? unconfirmedNotifier = null,
    ICurrentPriceProvider? currentPrice = null)
{
    // IADR-0039: LLM 呼び出しは多数決・二段のオーケストレータへ委譲する（プロンプト構築とサイジングは本サービスの責務）。
    private readonly DecisionOrchestrator _orchestrator =
        new(llm, options ?? DecisionOrchestrationOptions.Default, logger);

    // UC-01, FR-09, IADR-0096: 日報未確定（policy-null）で見送った際に確定を促す通知を促す出力ポート。
    // 未指定＝NoOp（何もしない＝現行のログのみ）。実発行（DailyPolicyUnconfirmed の publish・営業日 dedup）は Worker が
    // opt-in（TradeCycle:NotifyOnUnconfirmedPolicy）で差し替える。
    private readonly IDailyPolicyUnconfirmedNotifier _unconfirmedNotifier =
        unconfirmedNotifier ?? new NoOpDailyPolicyUnconfirmedNotifier();

    // FR-08, IADR-0072: RAG 取得ポート。未指定＝NoOp（常に空＝参考情報なし＝現行動作）。実結線は Worker が opt-in で差し替える。
    private readonly IRetrievalContextProvider _retrieval = retrieval ?? new NoOpRetrievalContextProvider();

    // FR-17, IADR-0076: 採算費用見積りの供給口。未指定＝NoOp（常に null＝未解決）。実見積りは Worker が opt-in で差し替える。
    private readonly IProfitabilityAssumptionsProvider _profitability =
        profitability ?? new NoOpProfitabilityAssumptionsProvider();

    // FR-17, IADR-0076: 採算評価ゲートの構成。未指定＝Default（無効＝現行挙動）。
    private readonly ProfitabilityGateOptions _profitabilityOptions = profitabilityOptions ?? ProfitabilityGateOptions.Default;

    // FR-02, IADR-0099: 判断文脈の現在値（価格文脈）供給口。未指定＝NoOp（IsEnabled=false・常に null＝現行動作）。
    // 実供給（MarketDataCurrentPriceProvider）は Worker が MarketData:Provider 設定時に opt-in で差し替える。
    private readonly ICurrentPriceProvider _currentPrice = currentPrice ?? new NoOpCurrentPriceProvider();

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
            // UC-01, FR-09, IADR-0096: 日報未確定による見送りを通知（確定を促す）。営業日単位の重複抑止は notifier 側。
            // fail-safe: 通知は取引判断のクリティカルパス外。発行失敗・例外で見送り（null 返却）を壊さない。キャンセルは伝播。
            await NotifyDailyPolicyUnconfirmedSafeAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var context = await sizingProvider.GetContextAsync(cancellationToken).ConfigureAwait(false);

        // FR-02, FR-10, IADR-0099 決定2/3: 権威ある現在値（価格文脈）を取得する（既定 NoOp＝null＝現行動作）。
        // fail-safe: 取得の例外は「現在値なし」に縮退（GetCurrentPriceSafeAsync）。キャンセルは伝播。
        var currentPrice = await GetCurrentPriceSafeAsync(trigger, cancellationToken).ConfigureAwait(false);

        // IADR-0099 決定3: 現在値ソースが有効化（IsEnabled=true）されているのに現在値が取れない（取得不可・鮮度切れ）とき
        // だけ、古い/無い価格で発注しないよう安全側（Hold・発注抑止）に倒す。未有効化（既定 no-op・IsEnabled=false）は
        // このゲートを適用せず現行挙動を保つ（既定で全銘柄 Hold にして SIMULATE 検証を壊さない）。
        if (_currentPrice.IsEnabled && currentPrice is null)
        {
            logger.LogInformation("現在値が取得できない/鮮度切れのため見送り（発注抑止・安全側）: {Symbol}", trigger.Symbol);
            return null;
        }

        // FR-08, IADR-0072: 収集情報・判断根拠を KB から RAG 取得して判断文脈に加える（既定＝空＝文脈なし＝現行動作）。
        // fail-safe: 取得は判断のクリティカルパス外。例外・遅延で判断を止めないよう、失敗は「文脈なし」に縮退する
        //（#18 アダプタ自体も fail-safe だが、独自アダプタ差し替え時の保険として判断境界でも握る）。
        var retrieved = await RetrieveContextSafeAsync(trigger, policy, cancellationToken).ConfigureAwait(false);

        // IADR-0039: 本判断プロンプトを構築し、多数決・二段をオーケストレータへ委譲する。一次スクリーニングプロンプトは
        // スクリーニング有効時のみ構築されるよう遅延ファクトリで渡す（既定＝無効の経路で無駄な構築をしない）。
        // IADR-0072 決定2: RAG 文脈は本判断のみに載せ、一次スクリーニング（費用統制）には載せない。
        // FR-17, IADR-0076 決定5: 採算ゲート有効時のみプロンプトに採算節を注入する（無効の既定は現行動作のプロンプトと一致）。
        var decisionPrompt = TradeDecisionPromptBuilder.Build(
            trigger, policy, context, retrieved, includeProfitability: _profitabilityOptions.Enabled,
            currentPrice: currentPrice);
        var orchestrated = await _orchestrator.DecideAsync(
            () => TradeDecisionPromptBuilder.BuildScreening(trigger, policy, context), decisionPrompt, cancellationToken)
            .ConfigureAwait(false);
        var decision = orchestrated.Decision;

        // FR-11: プロンプト・LLM 出力・根拠・票数・スクリーニング可否を記録する（永続監査は #17 連携）。
        logger.LogInformation(
            "LLM 判断: {Symbol} action={Action} rationale={Rationale} votes={Agreement}/{Total} screenedOut={ScreenedOut}",
            trigger.Symbol, decision.Action, decision.Rationale,
            orchestrated.AgreementVotes, orchestrated.TotalVotes, orchestrated.ScreenedOut);

        if (decision.Action == TradeAction.Hold)
        {
            return null; // 見送り
        }

        var side = decision.Action == TradeAction.Buy ? TradeSide.Buy : TradeSide.Sell;

        // FR-02, FR-10, IADR-0099 決定2: 発注に用いる参照価格を権威ある現在値へアンカリングする。現在値ありのときは
        // LLM の幻覚しうる ReferencePrice ではなく実市場価格でサイジング・損切り・採算 notional を効かせる。現在値なし
        // （既定 no-op）は従来どおり decision.ReferencePrice＝現行挙動。アンカリング後は IADR-0035 の不変量（損切り幅は
        // 参照価格より小さく正）を権威価格に対して再検証する（既定は Parser が保証済みのため素通り＝挙動不変）。
        var referencePrice = currentPrice ?? decision.ReferencePrice;
        if (referencePrice <= 0m || decision.StopLossDistancePerShare <= 0m
            || decision.StopLossDistancePerShare >= referencePrice)
        {
            logger.LogInformation(
                "参照価格が不正、または損切り幅が現在値以上のため見送り: {Symbol} referencePrice={ReferencePrice} stopLossDistance={StopLossDistance}",
                trigger.Symbol, referencePrice, decision.StopLossDistancePerShare);
            return null;
        }

        // IADR-0003: サイジングは判断サービスの責務。availableCapital は段階残枠と日次発注残枠の小さい方（IADR-0017）。
        var sizeFactor = PositionSizer.GetSizeFactor(context.ConsecutiveLosses, context.DrawdownRatio, context.Limits);
        var availableCapital = Math.Max(0m, Math.Min(context.StageCapitalRemaining, context.DailyOrderRemaining));
        var quantity = PositionSizer.CalculateCappedQuantity(
            context.Capital,
            context.Limits.PerTradeRiskRatio,
            decision.StopLossDistancePerShare,
            referencePrice,
            context.Limits.MaxOrderAmount,
            availableCapital,
            sizeFactor);

        if (quantity <= 0)
        {
            logger.LogInformation("サイジングで数量 0 のため見送り: {Symbol}", trigger.Symbol);
            return null;
        }

        // FR-17, 05_trading-assumptions §4, IADR-0076: 採算評価ゲート（opt-in・既定無効＝現行挙動）。
        // 有効時は往復の概算費用に対して想定利益が最小期待利益しきい値を満たすかを評価し、採算不成立・費用見積り不能は Hold に倒す。
        if (_profitabilityOptions.Enabled &&
            !await IsProfitableAsync(trigger, decision, referencePrice, quantity, cancellationToken).ConfigureAwait(false))
        {
            return null; // 採算不成立・見積り不能（安全側で見送り）
        }

        // FR-03/04, IADR-0035, IADR-0099: 損切り価格を算出して発注意図に載せる（#63 台帳へ永続化し市場監視の損切り検知に実値供給）。
        // ロングは参照価格より下、ショートは上に損切りラインを置く（StopLossEvaluator と対称）。参照価格はアンカリング済み。
        var stopLossPrice = side == TradeSide.Buy
            ? referencePrice - decision.StopLossDistancePerShare
            : referencePrice + decision.StopLossDistancePerShare;

        // IADR-0004: 発注意図には PositionEffect を必ず設定する。判断由来は新規建て（Open）。
        var intent = new OrderIntent(
            trigger.Symbol,
            trigger.Market,
            side,
            ProductType.Cash,
            context.Mode,
            quantity,
            referencePrice,
            PositionEffect.Open,
            stopLossPrice);

        return new TradeDecisionMade(Guid.NewGuid(), intent, decision.Rationale, clock.UtcNow);
    }

    // FR-17, 05_trading-assumptions §4, IADR-0076: 採算評価。数量確定後の約定代金に対する往復概算費用と最小期待利益倍率を
    // 設定サービス由来の見積り（_profitability）から取り、想定利益（LLM 由来・想定値幅 × 数量）と ProfitabilityGate で突き合わせる。
    // 採算成立（Viable）のみ true。採算不成立（NotViable）・費用見積り不能（Indeterminate＝前提条件未解決・実額未登録）は false（Hold）。
    // fail-safe: 見積り取得の例外は「見積り不能」に縮退して false（安全側）へ倒す。キャンセルは伝播させる。
    private async Task<bool> IsProfitableAsync(
        DecisionTrigger trigger, LlmDecision decision, decimal referencePrice, decimal quantity,
        CancellationToken cancellationToken)
    {
        // IADR-0099: notional はアンカリング済みの参照価格（現在値ありなら権威価格）× 数量で算出する。
        var notional = referencePrice * quantity;
        TradeCostAssessment? assessment;
        try
        {
            assessment = await _profitability.AssessAsync(trigger.Market, notional, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "採算費用の見積り取得に失敗しました（採算不能として見送り）: {Symbol}", trigger.Symbol);
            assessment = null;
        }

        var expectedGrossProfit = decision.ExpectedProfitPerShare * quantity;
        var verdict = ProfitabilityGate.Evaluate(
            expectedGrossProfit,
            assessment?.RoundTripCost,
            _profitabilityOptions.DecisionCostJpy,
            assessment?.MinimumProfitMultiple ?? 0m);

        if (verdict == ProfitabilityVerdict.Viable)
        {
            return true;
        }

        // FR-11: 採算見送りの根拠（想定利益・往復費用・倍率・版・判定）を記録する。
        logger.LogInformation(
            "採算評価により見送り: {Symbol} verdict={Verdict} expectedProfit={ExpectedProfit} roundTripCost={RoundTripCost} multiple={Multiple} decisionCost={DecisionCost} assumptionsVersion={Version}",
            trigger.Symbol, verdict, expectedGrossProfit, assessment?.RoundTripCost, assessment?.MinimumProfitMultiple,
            _profitabilityOptions.DecisionCostJpy, assessment?.AssumptionsVersion);
        return false;
    }

    // UC-01, FR-09, IADR-0096: 日報未確定通知の fail-safe ラッパ。通知は判断のクリティカルパス外のため、発行の例外・失敗は
    // 見送り（null 返却）を壊さないよう握って継続する。キャンセルは判断全体の停止要求のため伝播させる（縮退しない）。
    private async Task NotifyDailyPolicyUnconfirmedSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _unconfirmedNotifier.NotifyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "日報未確定の通知発行に失敗しました（見送りは継続）。");
        }
    }

    // FR-02, FR-10, IADR-0099 決定1: 現在値取得の fail-safe ラッパ。取得失敗（例外・遅延）は「現在値なし（null）」に
    // 縮退する。有効化時（IsEnabled=true）は呼び出し側が null を発注抑止（Hold）へ倒すため、例外は安全側に働く。
    // キャンセルは判断全体の停止要求のため伝播させる（縮退しない）。
    private async Task<decimal?> GetCurrentPriceSafeAsync(
        DecisionTrigger trigger, CancellationToken cancellationToken)
    {
        try
        {
            return await _currentPrice.GetCurrentPriceAsync(trigger, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "現在値の取得に失敗しました（現在値なしとして継続）: {Symbol}", trigger.Symbol);
            return null;
        }
    }

    // FR-08, IADR-0072 決定4: RAG 取得の fail-safe ラッパ。取得失敗（例外・遅延）は「文脈なし」に縮退し判断を継続する。
    // キャンセルは判断全体の停止要求のため伝播させる（縮退しない）。
    private async Task<IReadOnlyList<RetrievedContext>> RetrieveContextSafeAsync(
        DecisionTrigger trigger, DailyPolicy policy, CancellationToken cancellationToken)
    {
        try
        {
            return await _retrieval.GetContextAsync(trigger, policy, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "RAG 文脈の取得に失敗しました（文脈なしで判断を継続）: {Symbol}", trigger.Symbol);
            return [];
        }
    }
}
