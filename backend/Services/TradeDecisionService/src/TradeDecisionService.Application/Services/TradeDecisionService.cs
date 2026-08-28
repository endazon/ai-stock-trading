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
    ICurrentPriceProvider? currentPrice = null,
    IFxRateProvider? fxRate = null,
    IHeldPositionProvider? heldPosition = null,
    RetrievalSourcePolicy? retrievalSourcePolicy = null,
    IFxSourceStatusNotifier? statusNotifier = null,
    IScreeningReductionReporter? screeningReporter = null)
{
    // FR-04, ADR-0003, #252, IADR-0169 決定2: RAG 取得文脈の出典限定。
    // **未指定は「限定しない」ではなく Default（＝安全側の許可リスト）である。**
    // 不在が統制の無効を意味する形にはしない（IADR-0163 決定2 の規律）。
    private readonly RetrievalSourcePolicy _retrievalSourcePolicy = retrievalSourcePolicy ?? RetrievalSourcePolicy.Default;

    // IADR-0039: LLM 呼び出しは多数決・二段のオーケストレータへ委譲する（プロンプト構築とサイジングは本サービスの責務）。
    private readonly DecisionOrchestrator _orchestrator =
        new(llm, options ?? DecisionOrchestrationOptions.Default, logger);

    // #337, IADR-0247: スクリーニング入力の縮退（予算・順序）の構成。オーケストレータと同じ実効値を共有する。
    private readonly DecisionOrchestrationOptions _options = options ?? DecisionOrchestrationOptions.Default;

    // FR-06, FR-11, #337, IADR-0247: 縮退発生の記録経路（監査台帳・月報集計）。未指定＝NoOp。
    // 実発行（PublishingScreeningReductionReporter）は Worker が配線する。
    private readonly IScreeningReductionReporter _screeningReporter =
        screeningReporter ?? new NoOpScreeningReductionReporter();

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

    // #381 停止側 / IADR-0198: 未配線なら報告しない（NoOp を差さず null のままにして、判定を 1 箇所に置く）。
    private readonly IFxSourceStatusNotifier? _statusNotifier = statusNotifier;

    // FR-10, FR-17, #257, #364, IADR-0107/0152: 基準通貨（USD）への換算レートの供給口。未指定＝基準通貨の市場だけ
    // レート 1（米国株は現行どおり／日本株は解決不能＝新規建て見送り）。実供給は Worker が Fx:Provider 設定時に差し替える。
    private readonly IFxRateProvider _fxRate = fxRate ?? new BaseCurrencyOnlyFxRateProvider();

    // FR-04, FR-05, FR-10, #292, IADR-0119: 判断由来の決済（AI の出口）に用いる保有建玉の照会口。
    // 未指定＝NoOp（常に null＝不明）。不明のもとでは売り判断が見送りへ倒れる（裸の新規売りを出さない）。
    // 実照会（HttpHeldPositionProvider）は Worker が RiskManagement:BaseUrl 設定時に差し替える。
    private readonly IHeldPositionProvider _heldPosition = heldPosition ?? new NoOpHeldPositionProvider();

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

        // FR-10, FR-17, #257, #364, IADR-0107 決定2/3: 発注意図を作る前に基準通貨（USD）への換算レートを確定させる。
        // 解決できない（レート源未設定・取得失敗・鮮度切れ）非基準通貨の銘柄は、誤った実効上限で発注せず見送る。
        // 基準通貨の市場（米国株）は常に 1 が返る。LLM 呼び出しより前に倒すことで無駄な費用も避ける。
        var fxReading = await GetFxReadingSafeAsync(trigger.Market, cancellationToken).ConfigureAwait(false);
        if (fxReading is null)
        {
            // 通貨は市場から導けるため、ここでは市場だけを記録する（未定義の市場でも記録が例外で欠けないように）。
            // **値がまったく無い場合は決済も出さない** —— 決済意図へ載せる換算率が無く、既定の 1m を載せると
            // 監査台帳（FR-11・7 年保持）へ JPY を USD として記録することになる（#506）。
            logger.LogInformation(
                "基準通貨への換算レートが解決できないため見送り（発注抑止・安全側）: {Symbol} market={Market}",
                trigger.Symbol, trigger.Market);
            return null;
        }

        var rateToBase = fxReading.Rate.Rate;

        // 🔴 FR-10, #506, ADR-0022 決定5, IADR-0197: 鮮度切れ（30 日超）は**新規建てだけを止める。手仕舞いは止めない。**
        //
        // 従来はここで一律 return しており、**出口が入口と同じゲートで塞がれていた**——
        // 建玉効果（Open / Close）が分かるのは LLM の判断後だからである。
        // **「止められない」より「閉じられない」ほうが危険である**（損失を抱えた建玉から出られない）。
        //
        // 費用の据え置き（IADR-0107 決定2）: 保有が無ければ建玉効果は Open にしかならないため、
        // **鮮度切れかつ保有なしのときだけ保有数を先に引いて即座に見送る**（LLM を呼ばない）。
        // 正常時の経路は変えない。先読みした保有数は後段で再利用する（ブローカ照会を二重に打たない）。
        int? preFetchedHeldQuantity = null;
        if (!fxReading.UsableForEntry)
        {
            preFetchedHeldQuantity =
                await GetSignedHeldQuantitySafeAsync(trigger, cancellationToken).ConfigureAwait(false);

            if (preFetchedHeldQuantity is not { } held || held == 0)
            {
                logger.LogInformation(
                    "換算レートが鮮度切れで保有も無いため見送り（新規建てのみ停止・手仕舞いは対象外）: " +
                    "{Symbol} market={Market} asOf={AsOf}",
                    trigger.Symbol, trigger.Market, fxReading.Rate.AsOf);
                return null;
            }

            logger.LogWarning(
                "換算レートが鮮度切れだが保有があるため判断を続行する（手仕舞いのみ許可・ADR-0022 決定5）: " +
                "{Symbol} held={Held} asOf={AsOf}",
                trigger.Symbol, held, fxReading.Rate.AsOf);
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

        // #337, IADR-0247: 縮退制御が有効（スクリーニング有効かつ予算設定）なときだけ、スクリーニング入力
        // （方針・市況＝保護、RAG・ニュース＝削減可）へ縮退順序 ①分割→②RAG→③ニュース を適用する。
        // 未設定（既定）は従来プロンプト（参考情報なし・IADR-0072 決定2）＝現行挙動。
        var screening = _options is { EnableScreening: true, ScreeningContextBudgetChars: { } budget }
            ? ScreeningContextAssembler.Assemble(trigger, policy, retrieved, currentPrice, budget)
            : null;

        var orchestrated = await _orchestrator.DecideAsync(
            () => screening is null
                ? TradeDecisionPromptBuilder.BuildScreening(trigger, policy, context)
                : TradeDecisionPromptBuilder.BuildScreening(
                    trigger, policy, context, currentPrice, screening.RetainedReferences),
            decisionPrompt, cancellationToken)
            .ConfigureAwait(false);
        var decision = orchestrated.Decision;

        // #337, IADR-0247: 縮退（分割・切り詰め・解消不能な超過）が発生したら記録する（planning#53 の裁定・
        // 月報の件数記載）。fail-safe: 記録は判断のクリティカルパス外。発行失敗で判断を壊さない。キャンセルは伝播。
        if (screening is { Plan.ReductionOccurred: true })
        {
            await ReportScreeningReductionSafeAsync(trigger, screening, cancellationToken).ConfigureAwait(false);
        }

        // FR-11: プロンプト・LLM 出力・根拠・票数・スクリーニング可否を記録する（永続監査は #17 連携）。
        // #337（#290 吸収）, IADR-0248: 解析不能（unparseableVotes / screeningUnparseable）は見送りと区別して残す。
        logger.LogInformation(
            "LLM 判断: {Symbol} action={Action} rationale={Rationale} votes={Agreement}/{Total} screenedOut={ScreenedOut} "
                + "unparseableVotes={UnparseableVotes} screeningUnparseable={ScreeningUnparseable}",
            trigger.Symbol, decision.Action, decision.Rationale,
            orchestrated.AgreementVotes, orchestrated.TotalVotes, orchestrated.ScreenedOut,
            orchestrated.UnparseableVotes, orchestrated.ScreeningUnparseable);

        if (decision.Action == TradeAction.Hold)
        {
            return null; // 見送り
        }

        var side = decision.Action == TradeAction.Buy ? TradeSide.Buy : TradeSide.Sell;

        // FR-04, FR-05, FR-10, #292, IADR-0119: 保有建玉から建玉効果を決める。従来は Open がリテラル固定で、
        // LLM の Sell が「保有ロングの決済」ではなく新規ショート建てとして扱われていた（AI に出口が無かった）。
        // 鮮度切れの経路では上で先読み済み（ブローカ照会を二重に打たない・#506）。
        var heldQuantity = preFetchedHeldQuantity
            ?? await GetSignedHeldQuantitySafeAsync(trigger, cancellationToken).ConfigureAwait(false);
        var effect = PositionEffectResolver.Resolve(side, heldQuantity);
        if (effect.IsSkipped)
        {
            // 保有なし・不明での売り＝裸の新規ショート建て。現物のみ有効な段階では成立せず、取引ガードは方向を
            // 見ないため素通りしてブローカへ飛ぶ。ADR-0003（不確実なら Hold）に従い見送る。
            logger.LogInformation(
                "保有建玉が無い、または不明な売り判断のため見送り（裸の新規売りを出さない・IADR-0119）: {Symbol} held={Held}",
                trigger.Symbol, heldQuantity.HasValue ? heldQuantity.Value : "不明");
            return null;
        }

        // FR-02, FR-10, IADR-0099 決定2: 発注に用いる参照価格を権威ある現在値へアンカリングする。現在値ありのときは
        // LLM の幻覚しうる ReferencePrice ではなく実市場価格でサイジング・損切り・採算 notional を効かせる。現在値なし
        // （既定 no-op）は従来どおり decision.ReferencePrice＝現行挙動。
        var referencePrice = currentPrice ?? decision.ReferencePrice;
        if (referencePrice <= 0m)
        {
            logger.LogInformation(
                "参照価格が不正のため見送り: {Symbol} referencePrice={ReferencePrice}",
                trigger.Symbol, referencePrice);
            return null;
        }

        // #292, IADR-0119: 決済（手仕舞い）はここで確定する。数量は保有数の全量で、以下は**通さない**。
        //   - サイジング: 出口の数量は保有数であって新規建てのリスク基準サイズではない。
        //   - 採算ゲート（IADR-0076）: 最小期待利益で撤退を止めてはならない（損失を止める決済が通らなくなる）。
        //   - 損切り幅の検証: 決済注文に損切り価格は無い（StopLossPrice=null・IADR-0035 は建玉側が保持する）。
        // 発注前スクリーニングは通すが、RiskEvaluator の isEntry=(PositionEffect==Open) により kill switch・pause・
        // ロックアウト・段階資金上限・同日再エントリーは構造的に素通りする（FR-10「手仕舞いは止めない」）。
        // 🔴 FR-10, #506, ADR-0022 決定5: 鮮度切れで**新規建てを止めるのはここである**（ゲートではない）。
        // ゲートで止めると出口まで塞がるため、**建玉効果が確定したこの地点まで判断を遅らせている**。
        // 保有があっても LLM が Buy（買い増し）と言えば Open であり、その場合は止める。
        if (!fxReading.UsableForEntry && !effect.IsClose)
        {
            logger.LogInformation(
                "換算レートが鮮度切れのため新規建てを見送る（手仕舞いは止めない・ADR-0022 決定5）: " +
                "{Symbol} effect={Effect} asOf={AsOf}",
                trigger.Symbol, effect.Effect, fxReading.Rate.AsOf);
            return null;
        }

        if (effect.IsClose)
        {
            var closeIntent = new OrderIntent(
                trigger.Symbol,
                trigger.Market,
                side,
                ProductType.Cash,
                context.Mode,
                effect.CloseQuantity,
                referencePrice,
                PositionEffect.Close,
                StopLossPrice: null,
                FxRateToBase: rateToBase);

            logger.LogInformation(
                "判断由来の決済: {Symbol} {Side} 数量={Quantity}（保有全量・統制で止めない）",
                trigger.Symbol, side, effect.CloseQuantity);

            // 🔴 FR-11, #381 停止側, IADR-0198 決定3: **鮮度切れの値で取引した事実を残す。**
            // 監査台帳の行は観測日の列を持たないため、**イベントに載せることが 7 年保持へ入れる唯一の経路**である。
            // 抑止しない——取引は 1 件ずつ残さなければ後から件数も金額も復元できない。
            if (!fxReading.UsableForEntry)
            {
                await ReportClosedWithStaleRateSafeAsync(
                    trigger, effect.CloseQuantity, rateToBase, fxReading, cancellationToken).ConfigureAwait(false);
            }

            return new TradeDecisionMade(Guid.NewGuid(), closeIntent, decision.Rationale, clock.UtcNow);
        }

        // 以降は新規建て（Open）の従来経路。IADR-0035 の不変量（損切り幅は参照価格より小さく正）を権威価格に対して
        // 再検証する（既定は Parser が保証済みのため素通り＝挙動不変）。
        if (decision.StopLossDistancePerShare <= 0m || decision.StopLossDistancePerShare >= referencePrice)
        {
            logger.LogInformation(
                "損切り幅が不正、または現在値以上のため見送り: {Symbol} referencePrice={ReferencePrice} stopLossDistance={StopLossDistance}",
                trigger.Symbol, referencePrice, decision.StopLossDistancePerShare);
            return null;
        }

        // FR-10, FR-17, #257, #364, IADR-0107 決定1/2: サイジングの入力を基準通貨（USD）へ揃える。資金・上限・残枠は基準通貨、
        // 参照価格・損切り幅は銘柄のローカル通貨のため、1 株あたり金額にレートを掛けてから PositionSizer へ渡す
        // （混在させると金額上限が桁で誤り、過大発注を招く）。基準通貨の市場はレート 1 で現行と同値。
        var referencePriceBase = referencePrice * rateToBase;
        var stopLossDistanceBase = decision.StopLossDistancePerShare * rateToBase;

        // IADR-0003: サイジングは判断サービスの責務。availableCapital は段階残枠と日次発注残枠の小さい方（IADR-0017）。
        var sizeFactor = PositionSizer.GetSizeFactor(context.ConsecutiveLosses, context.DrawdownRatio, context.Limits);
        var availableCapital = Math.Max(0m, Math.Min(context.StageCapitalRemaining, context.DailyOrderRemaining));
        var quantity = PositionSizer.CalculateCappedQuantity(
            context.Capital,
            context.Limits.PerTradeRiskRatio,
            stopLossDistanceBase,
            referencePriceBase,
            // FR-10, #329, IADR-0130 決定1: 1 注文金額上限は equity 比のため equity（context.Capital）から解決する。
            // 「1 取引リスク 1%」と「1 注文 25%」のどちらが厳しいかは CalculateCappedQuantity が min で採る。
            context.Limits.MaxOrderAmountFor(context.Capital),
            availableCapital,
            sizeFactor);

        if (quantity <= 0)
        {
            logger.LogInformation("サイジングで数量 0 のため見送り: {Symbol}", trigger.Symbol);
            return null;
        }

        // FR-17, 05_trading-assumptions §4, IADR-0076: 採算評価ゲート（opt-in・既定無効＝現行挙動）。
        // 有効時は往復の概算費用に対して想定利益が最小期待利益しきい値を満たすかを評価し、採算不成立・費用見積り不能は Hold に倒す。
        // IADR-0107 決定2: 費用・最小期待利益は基準通貨で登録されている（計画 05_trading-assumptions §2）ため、
        // notional・想定利益も基準通貨で突き合わせる。
        if (_profitabilityOptions.Enabled &&
            !await IsProfitableAsync(
                trigger, decision, referencePriceBase, rateToBase, quantity, cancellationToken).ConfigureAwait(false))
        {
            return null; // 採算不成立・見積り不能（安全側で見送り）
        }

        // FR-03/04, IADR-0035, IADR-0099: 損切り価格を算出して発注意図に載せる（#63 台帳へ永続化し市場監視の損切り検知に実値供給）。
        // ロングは参照価格より下、ショートは上に損切りラインを置く（StopLossEvaluator と対称）。参照価格はアンカリング済み。
        var stopLossPrice = side == TradeSide.Buy
            ? referencePrice - decision.StopLossDistancePerShare
            : referencePrice + decision.StopLossDistancePerShare;

        // IADR-0004: 発注意図には PositionEffect を必ず設定する。ここへ到達するのは新規建て（Open）のみで、
        // 決済（Close）は上で確定済み（#292, IADR-0119）。
        // IADR-0107 決定1: 価格・損切り価格はローカル通貨のまま載せ（発注執行がそのまま注文価格に用いる）、
        // 統制・台帳が基準通貨で判定できるよう確定したレートを同伴させる。
        var intent = new OrderIntent(
            trigger.Symbol,
            trigger.Market,
            side,
            ProductType.Cash,
            context.Mode,
            quantity,
            referencePrice,
            PositionEffect.Open,
            stopLossPrice,
            rateToBase);

        return new TradeDecisionMade(Guid.NewGuid(), intent, decision.Rationale, clock.UtcNow);
    }

    // FR-04, FR-05, #292, IADR-0119: 保有建玉の照会（fail-safe ラッパ）。
    // 例外・キャンセル以外の失敗は **null（不明）** に縮退する。0（保有なし）へ倒すと「保有していない」と誤断定し、
    // 裸の新規売りを通してしまうため、この区別を境界でも守る（アダプタ自体も同じ契約）。
    private async Task<int?> GetSignedHeldQuantitySafeAsync(
        DecisionTrigger trigger, CancellationToken cancellationToken)
    {
        try
        {
            return await _heldPosition
                .GetSignedQuantityAsync(trigger.Symbol, trigger.Market, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "保有建玉の照会に失敗しました（不明として扱います）: {Symbol}", trigger.Symbol);
            return null;
        }
    }

    // FR-17, 05_trading-assumptions §4, IADR-0076: 採算評価。数量確定後の約定代金に対する往復概算費用と最小期待利益倍率を
    // 設定サービス由来の見積り（_profitability）から取り、想定利益（LLM 由来・想定値幅 × 数量）と ProfitabilityGate で突き合わせる。
    // 採算成立（Viable）のみ true。採算不成立（NotViable）・費用見積り不能（Indeterminate＝前提条件未解決・実額未登録）は false（Hold）。
    // fail-safe: 見積り取得の例外は「見積り不能」に縮退して false（安全側）へ倒す。キャンセルは伝播させる。
    private async Task<bool> IsProfitableAsync(
        DecisionTrigger trigger, LlmDecision decision, decimal referencePriceBase, decimal fxRateToBase,
        decimal quantity, CancellationToken cancellationToken)
    {
        // IADR-0099: notional はアンカリング済みの参照価格（現在値ありなら権威価格）× 数量で算出する。
        // IADR-0107: 参照価格は基準通貨へ換算済み（費用見積りの単位と揃える）。
        var notional = referencePriceBase * quantity;
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

        // 想定利益（LLM 由来）は 1 株あたりのローカル通貨額のため、費用と同じ基準通貨へ換算してから突き合わせる。
        var expectedGrossProfit = decision.ExpectedProfitPerShare * fxRateToBase * quantity;
        var verdict = ProfitabilityGate.Evaluate(
            expectedGrossProfit,
            assessment?.RoundTripCost,
            _profitabilityOptions.DecisionCostJpy,
            assessment?.MinimumProfitMultiple ?? 0m,
            assessment?.CapitalGainsTaxRate ?? 0m);

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

    /// <summary>
    /// 鮮度切れでの決済を可視化経路へ報告する。
    /// <para>
    /// 🔴 <b>可視化の失敗で決済を止めない。</b> ここは計画が「手仕舞いは止めない」と定めた経路であり
    /// （ADR-0022 決定5）、<b>記録できないことを理由に決済を止めるのは本末転倒である。</b>
    /// ただし<b>飲み込んだ事実はログへ残す</b>——この 1 件は台帳に残らない。
    /// </para>
    /// </summary>
    private async Task ReportClosedWithStaleRateSafeAsync(
        DecisionTrigger trigger,
        int quantity,
        decimal rateToBase,
        FxRateReading reading,
        CancellationToken cancellationToken)
    {
        if (_statusNotifier is null)
        {
            return;
        }

        var age = clock.UtcNow - reading.Rate.AsOf;

        try
        {
            await _statusNotifier
                .ReportClosedWithStaleRateAsync(
                    trigger.Symbol,
                    trigger.Market,
                    CurrencyFormat.CodeOf(MarketCurrency.Of(trigger.Market)),
                    quantity,
                    rateToBase,
                    reading.Rate.AsOf,
                    age,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "鮮度切れのレートでの決済を可視化経路へ報告できませんでした（{Symbol}）。" +
                "**この決済は「古いレートで出た」記録が台帳に残りません**（決済自体は続行します）。",
                trigger.Symbol);
        }
    }

    // FR-10, FR-17, #257, IADR-0107 決定3: 換算レート取得の fail-safe ラッパ。取得失敗（例外）は「レート無し（null）」に
    // 縮退する。呼び出し側が null を新規建ての見送りへ倒すため、例外は安全側（過大発注を招かない側）に働く。
    // キャンセルは判断全体の停止要求のため伝播させる（縮退しない）。
    private async Task<FxRateReading?> GetFxReadingSafeAsync(Market market, CancellationToken cancellationToken)
    {
        try
        {
            return await _fxRate.GetReadingAsync(market, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "基準通貨への換算レート取得に失敗しました（レート無しとして継続）: {Market}", market);
            return null;
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
    //
    // FR-04, ADR-0003, #252, IADR-0169 決定2: 取得結果は**出典で限定してから**プロンプトへ渡す。
    // **絞り込みは取得側（アダプタ）ではなくここで行う** —— 守るのは「注入点」であって特定の provider 実装ではない。
    // 別の provider を挿しても統制が抜けない位置に置く（IADR-0163 決定2 と同じ考え方）。
    // #337, IADR-0247: 縮退発生の記録（fail-safe ラッパ）。発行の例外は握って判断を続ける（キャンセルは伝播）。
    private async Task ReportScreeningReductionSafeAsync(
        DecisionTrigger trigger, ScreeningContextAssembler.AssembledScreeningContext screening,
        CancellationToken cancellationToken)
    {
        var plan = screening.Plan;
        try
        {
            await _screeningReporter.ReportAsync(
                new ScreeningContextReduced(
                    [trigger.Symbol],
                    plan.Batches.Count,
                    plan.SplitOccurred,
                    plan.DroppedRagCount,
                    plan.DroppedNewsCount,
                    plan.UnresolvableOverflow,
                    screening.BudgetChars,
                    clock.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "スクリーニング縮退の記録発行に失敗しました（判断は継続します）: {Symbol}", trigger.Symbol);
        }
    }

    private async Task<IReadOnlyList<RetrievedContext>> RetrieveContextSafeAsync(
        DecisionTrigger trigger, DailyPolicy policy, CancellationToken cancellationToken)
    {
        IReadOnlyList<RetrievedContext> retrieved;
        try
        {
            retrieved = await _retrieval.GetContextAsync(trigger, policy, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "RAG 文脈の取得に失敗しました（文脈なしで判断を継続）: {Symbol}", trigger.Symbol);
            return [];
        }

        return FilterBySource(retrieved, trigger);
    }

    // FR-04, ADR-0003, #252, IADR-0169 決定2/決定3: 出典限定と、その**可視化**。
    //
    // **黙って無効化しないことが本メソッドの主眼である。** 出典限定には「RAG を丸ごと黙って無効化する」失敗モードが
    // ある —— KB 側がタグを返さない構成では全件が除外され、**「文脈なし」で正常動作しているように見える**。
    // ヒットがあったのに全件落ちたときは Warning を出し、観測されたタグを添える（原因を追える形で残す）。
    private IReadOnlyList<RetrievedContext> FilterBySource(
        IReadOnlyList<RetrievedContext> retrieved, DecisionTrigger trigger)
    {
        if (retrieved.Count == 0)
            return retrieved;

        var allowed = _retrievalSourcePolicy.Filter(retrieved);
        if (allowed.Count == retrieved.Count)
            return allowed;

        var observedTags = string.Join(
            ", ",
            retrieved.SelectMany(r => r.Tags).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct());

        if (allowed.Count == 0)
        {
            logger.LogWarning(
                "RAG 文脈が出典限定で全件除外されました（文脈なしで判断を継続）: {Symbol} / 取得 {Total} 件 / "
                    + "観測されたタグ: [{ObservedTags}] / 許可: [{AllowedTags}]。"
                    + "KB がタグを返していない可能性があります（この場合 RAG は実質無効です）。",
                trigger.Symbol,
                retrieved.Count,
                observedTags,
                string.Join(", ", _retrievalSourcePolicy.AllowedTags));
        }
        else
        {
            logger.LogDebug(
                "RAG 文脈を出典限定で絞り込みました: {Symbol} / {Allowed}/{Total} 件 / 観測されたタグ: [{ObservedTags}]",
                trigger.Symbol, allowed.Count, retrieved.Count, observedTags);
        }

        return allowed;
    }
}
