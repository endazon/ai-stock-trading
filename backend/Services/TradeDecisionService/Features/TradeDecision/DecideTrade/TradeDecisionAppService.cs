extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Infrastructure.ExternalServices;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Domain;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Fx;
using AiStockTrading.Shared.Infrastructure.Composable.Observability;
using Microsoft.Extensions.Logging;

namespace TradeDecisionService.Features.TradeDecision.DecideTrade;

// FR-04, FR-07, FR-10, FR-11, UC-01, UC-02, ADR-0003, IADR-0003/0004/0017/0037: 取引判断の中核。
// トリガー → 確定済み日報の方針＋リスク制約で LLM 判断（多数決・二段オーケストレーション・IADR-0039）→ 構造化解析
// → PositionSizer で数量確定 → TradeDecisionMade。
// 安全既定: 確定済み日報なし / Hold / 数量 0 は取引しない（発注意図を作らない）。
// options 未指定なら DecisionOrchestrationOptions.Default（1 票・スクリーニング無効）＝単発判断（IADR-0017）と等価。
public sealed class TradeDecisionAppService(
    ILlmCompletionClient llm,
    IDailyPolicyProvider policyProvider,
    ISizingContextProvider sizingProvider,
    IClock clock,
    ILogger<TradeDecisionAppService> logger,
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
    IScreeningReductionReporter? screeningReporter = null,
    IDecisionSkipReporter? skipReporter = null,
    IWatchlistProvider? watchlist = null,
    IDecisionHeldReporter? heldReporter = null,
    NewsCollectionStatusStore? newsStatus = null,
    IDecisionForgoneBeforeLlmReporter? forgoneReporter = null,
    IPositionQueryHealthReporter? positionQueryHealth = null,
    IEntryBlockersProvider? entryBlockers = null,
    IStopWidthFloorSource? stopWidthFloor = null,
    IDailyBarsProvider? dailyBars = null,
    MinimumEntryNotionalOptions? minimumEntryNotional = null)
{
    // 🔴 FR-10, #1176, IADR-0495 決定1・2: 新規建ての最小の名目額（equity 比）。未指定＝既定（1%）で**効く**（不在を「統制なし」に
    // しない。IADR-0163 決定2 の規律）。本番は Program.cs が Sizing:MinEntryNotionalRatio から読んで明示的に渡す（範囲外は起動を止める）。
    private readonly MinimumEntryNotionalOptions _minimumEntryNotional =
        minimumEntryNotional ?? MinimumEntryNotionalOptions.Default;

    // FR-04, ADR-0048 決定 2・3, #1118, IADR-0467 決定 3・6: 判断へ渡す出来高の日足の口。未指定＝NoOp（IsEnabled=false・要求しない）＝
    // プロンプトは従来の「出来高: 未提供」の行のまま。本番は Program.cs が DecisionVolume:Enabled（既定 false）で選んで明示的に渡す。
    private readonly IDailyBarsProvider _dailyBars = dailyBars ?? new NoOpDailyBarsProvider();

    // FR-10, ADR-0049 決定2, #1120, IADR-0465 決定1: 損切り幅の下限（ATR(14)）の供給口。未指定＝NoAtr（IsEnabled=false・常に null）＝
    // 参照価格（アンカー後）の 2% が下限として効く（配備までの暫定手段）。本番は Program.cs が StopWidthFloor:Atr14:Enabled（既定 false）で
    // 選んで明示的に渡す（#1122, IADR-0486 決定1）。
    private readonly IStopWidthFloorSource _stopWidthFloor = stopWidthFloor ?? new NoAtrStopWidthFloorSource();

    // 🔴 FR-10, FR-04, #1113, IADR-0463 決定 4: 銘柄単位の新規建ての可否（リスク管理が審査と同じ述語で答える）。未指定＝NoOp
    // （常に不明＝LLM を呼ぶ＝従来どおり）。本番は Program.cs が保有照会と同じ選び方で Http / Grpc を注入する。
    private readonly IEntryBlockersProvider _entryBlockers = entryBlockers ?? new NoOpEntryBlockersProvider();

    // FR-04, ADR-0020 決定2, #1081, IADR-0455: ニュースの状態（取得済み／欠測／未構成）の最新値。未指定＝null＝プロンプトは
    // 「ニュース: 不明」と書く（無言で省かない）。本番は Program.cs の singleton が注入され、定時の購読が記録する。
    private readonly NewsCollectionStatusStore? _newsStatus = newsStatus;

    // FR-04, #1034, IADR-0440 決定 2: 判断のプロンプトへ載せる監視銘柄の供給口（定時サイクルが判断対象を決める口と同じ登録）。
    // 未指定＝null＝プロンプトは「監視銘柄: 不明」と書く（空の一覧は渡さない）。本番は Program.cs の IWatchlistProvider が注入される。
    private readonly IWatchlistProvider? _watchlist = watchlist;

    // #1034, PR #1041 の監査 F5, IADR-0440 決定 2（2026-09-26 追記）: このインスタンスで監視銘柄を一度読めなかったら、以後の判断では
    // 照会せず不明とする。本サービスはスコープ登録で、Wolverine はメッセージ 1 件ごとにスコープを作るため、状態は**そのメッセージ
    // （定時サイクルなら 1 巡回）の中だけ**で持つ。市場監視が止まっているとき、1 巡回の銘柄ごとに照会の打ち切り（5 秒）を待たない。
    // 読めた一覧は覚えない（判断ごとに引き直す。巡回の途中の変更を所属の判定へ反映する）。
    private bool _watchlistUnavailable;

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

    // FR-04, FR-10, NFR-07, #891, IADR-0374: 見送りの理由を観測経路へ渡すポート。未指定＝NoOp（計上しない）。
    // 実計上（MetricsDecisionSkipReporter）は Worker が配線する。
    private readonly IDecisionSkipReporter _skipReporter = skipReporter ?? new NoOpDecisionSkipReporter();

    // 🔴 UC-02, FR-03, #1077, IADR-0452 決定4: 判断後の見送りを市場監視（急変の基準値）へ渡すポート。未指定＝NoOp。
    // 実発行（PublishingDecisionHeldReporter）は Worker が配線する。
    private readonly IDecisionHeldReporter _heldReporter = heldReporter ?? new NoOpDecisionHeldReporter();

    // 🔴 NFR, FR-04, FR-11, #1092, IADR-0462 決定4: LLM を呼ぶ前の見送りを監査台帳へ渡すポート。未指定＝NoOp。
    // 実発行（PublishingDecisionForgoneBeforeLlmReporter）は Worker が配線する。
    private readonly IDecisionForgoneBeforeLlmReporter _forgoneReporter =
        forgoneReporter ?? new NoOpDecisionForgoneBeforeLlmReporter();

    // 🔴 NFR, FR-10, #1092, IADR-0462 決定2: 保有照会・未約定の照会の状態の報告口（状態が変わったときだけ台帳へ出る）。
    // 本番は Worker が singleton（発行の実装）を配線する。未指定＝NoOp。
    private readonly IPositionQueryHealthReporter _positionQueryHealth =
        positionQueryHealth ?? NoOpPositionQueryHealthReporter.Instance;

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
    // #854, IADR-0351: 同じ照会口が判断プロンプトの保有状況（数量・取得単価・損切りライン）も供給する。
    // 不明のもとではプロンプトが「保有: 不明」と明示し、Hold を選ぶよう述べる（「保有なし」とは書かない）。
    // 実照会（HttpHeldPositionProvider）は Worker が RiskManagement:BaseUrl 設定時に差し替える。
    private readonly IHeldPositionProvider _heldPosition = heldPosition ?? new NoOpHeldPositionProvider();

    // 🔴 FR-04, FR-10, NFR-07, #891, IADR-0374: **見送りの唯一の出口。**
    //
    // 判断が発注意図を作らないときは、必ずここを通して理由を計上する（戻り値は従来どおり null）。
    // **計上点を見送り地点へ散らさない** —— 散らすと、次に見送りを足した人が計上を忘れても誰も気づかず、
    // その見送りは再び「ログにしか残らない」状態へ戻る（#891 の症状そのもの）。
    // 端点間レイテンシの計上が RecordCycleLatency の 1 か所に判断を集めているのと同じ規律である。
    //
    // 🔴 **呼び出し元の挙動は変わらない。** 本メソッドは観測だけを行い、見送るかどうかの判定には一切関与しない。
    //
    // 🔴 PR #919 監査, IADR-0374: **計上の失敗で見送りを壊さない**（兄弟ポートの ...SafeAsync と同じ規律）。
    // 現行の MetricsDecisionSkipReporter は Counter.Add だけで例外を出さないが、ポートである以上いつか別実装が
    // 挿さる。ここで例外が漏れると DecideAsync が throw し、呼び出し元は decisions{action=no-trade} を
    // 計上しない（＝見送りが「判断の失敗」へ化け、判断回数の内訳が歪む）。観測はクリティカルパス外である。
    // Report は CancellationToken を取らないため、ここで捕まる OperationCanceledException は本判断の
    // キャンセルではない。よって例外の種類で除外しない。
    private TradeDecisionMade? Skip(DecisionTrigger trigger, DecisionSkipReason reason)
    {
        try
        {
            _skipReporter.Report(trigger.MetricTrigger, reason);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "見送り理由の計上に失敗しました（見送りは継続します）: {Symbol} reason={Reason}",
                trigger.Symbol, reason);
        }

        return null;
    }

    // 🔴 UC-02, FR-03, #1077, IADR-0452 決定1/4: **AI 判断が結論を出した後の見送り**の出口。
    // 計画の基準点は「前回 AI 判断を行った時点の価格」であり、見送りも判断結果である。判断時点の価格が分かれば
    // TradeDecisionHeld を発行して市場監視の基準値を進め、そのうえで唯一の出口 Skip を通す（計上は Skip の 1 件のまま）。
    // judgedPrice が null（解析不能＝結論なし、または価格が手元に無い）なら発行しない。
    //
    // 🔴 **発行の失敗で見送りを壊さない**（兄弟ポートの ...SafeAsync と同じ規律）。伝えるのは**本判断のキャンセル**だけである
    // （PR #1080 監査, IADR-0452 決定4）。判定は例外の型ではなく本判断のトークンで行う —— 発行先の内部の打ち切り
    // （無関係な TaskCanceledException 等）まで伝えると、見送りが「判断の失敗」へ化け、見送りの計上が欠ける。
    private async Task<TradeDecisionMade?> SkipJudgedAsync(
        DecisionTrigger trigger, DecisionSkipReason reason, decimal? judgedPrice, CancellationToken cancellationToken)
    {
        if (judgedPrice is { } price)
        {
            try
            {
                await _heldReporter.ReportAsync(
                    new TradeDecisionHeld(
                        Guid.NewGuid(), trigger.Symbol, trigger.Market, price, reason.ToString(), clock.UtcNow,
                        trigger.MetricTrigger),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(
                    ex, "判断後の見送りの発行に失敗しました（見送りは継続します・基準値は進みません）: {Symbol} reason={Reason}",
                    trigger.Symbol, reason);
            }
        }

        return Skip(trigger, reason);
    }

    // 🔴 NFR, FR-04, FR-11, #1092, IADR-0462 決定4: **LLM を呼ぶ前の見送り**（4 地点。#1113 / IADR-0463 で 5 地点。#1176 / IADR-0495 で 6 地点。#1174 / IADR-0500 で 7 地点）の出口。1 回の見送りにつき 1 件
    // TradeDecisionForgoneBeforeLlm を発行してから、唯一の出口 Skip を通す（計上は Skip の 1 件のまま）。
    // 🔴 TradeDecisionHeld は出さない（判断をしていない見送りで急変の基準値を進めない。IADR-0452 決定1）。
    // 🔴 **発行の失敗で見送りを壊さない**（SkipJudgedAsync と同じ規律）。伝えるのは本判断のキャンセルだけである。
    private async Task<TradeDecisionMade?> SkipBeforeLlmAsync(
        DecisionTrigger trigger, DecisionForgoneBeforeLlmReason reason, CancellationToken cancellationToken)
    {
        try
        {
            await _forgoneReporter.ReportAsync(
                new TradeDecisionForgoneBeforeLlm(
                    Guid.NewGuid(), trigger.Symbol, trigger.Market, reason, clock.UtcNow, trigger.MetricTrigger),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                ex, "LLM を呼ぶ前の見送りの発行に失敗しました（見送りは継続します）: {Symbol} reason={Reason}",
                trigger.Symbol, reason);
        }

        return Skip(trigger, ToSkipReason(reason));
    }

    // #1092, IADR-0462 決定4: 台帳の語彙（7 値。#1113・#1176・#1174 で 1 値ずつ足した）→ 観測の語彙（DecisionSkipReason）。名前は同じ（試験が固定する）。
    internal static DecisionSkipReason ToSkipReason(DecisionForgoneBeforeLlmReason reason) => reason switch
    {
        DecisionForgoneBeforeLlmReason.DailyPolicyUnconfirmed => DecisionSkipReason.DailyPolicyUnconfirmed,
        DecisionForgoneBeforeLlmReason.CurrentPriceUnavailable => DecisionSkipReason.CurrentPriceUnavailable,
        DecisionForgoneBeforeLlmReason.FxRateUnresolved => DecisionSkipReason.FxRateUnresolved,
        DecisionForgoneBeforeLlmReason.FxRateStaleNoHolding => DecisionSkipReason.FxRateStaleNoHolding,
        DecisionForgoneBeforeLlmReason.EntryBlockedByRiskControls => DecisionSkipReason.EntryBlockedByRiskControls,
        DecisionForgoneBeforeLlmReason.EntryCapacityBelowMinimumNotional => DecisionSkipReason.EntryCapacityBelowMinimumNotional,
        DecisionForgoneBeforeLlmReason.EntryCapacityBelowOneShare => DecisionSkipReason.EntryCapacityBelowOneShare,
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "LLM を呼ぶ前の見送りの理由ではない"),
    };

    // 🔴 NFR, FR-10, #1092, IADR-0462 決定2: 保有照会・未約定の照会の成否を報告する。**実結線（IsEnabled）のときだけ**
    // （未結線の NoOp は常に不明を返すが、照会していないので失敗ではない）。報告は例外を投げない。
    private Task ReportHoldingsQueryAsync(PositionQuerySource source, bool succeeded) =>
        _heldPosition.IsEnabled ? _positionQueryHealth.ReportAsync(source, succeeded) : Task.CompletedTask;

    // 🔴 UC-02, FR-03, #1077, IADR-0452 決定1/3: 判断時点の価格。**結論を得ていない（解析不能）なら null**
    // （IADR-0248: 一次の解析不能、または二次の全票が解析不能）。価格は手元の実価格を優先する:
    // 現在値（有効時） → 起点の価格（価格変動トリガー） → LLM の参照価格（いずれも正のときだけ。Hold の参照価格は 0）。
    private static decimal? JudgedPriceOf(
        OrchestratedDecision orchestrated, decimal? currentPrice, DecisionTrigger trigger)
    {
        var concluded = !orchestrated.ScreeningUnparseable
            && !(orchestrated.TotalVotes > 0 && orchestrated.UnparseableVotes >= orchestrated.TotalVotes);
        if (!concluded)
        {
            return null;
        }

        // 正の値だけを候補にする（0 以下の現在値で後段の候補を塞がない。PR #1080 監査）。
        return Positive(currentPrice) ?? Positive(trigger.Price) ?? Positive(orchestrated.Decision.ReferencePrice);

        static decimal? Positive(decimal? value) => value is > 0m ? value : null;
    }

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
            return await SkipBeforeLlmAsync(trigger, DecisionForgoneBeforeLlmReason.DailyPolicyUnconfirmed, cancellationToken)
                .ConfigureAwait(false);
        }

        var context = await sizingProvider.GetContextAsync(cancellationToken).ConfigureAwait(false);

        // FR-02, FR-10, IADR-0099 決定2/3: 権威ある現在値（価格文脈）を取得する（既定 NoOp＝null＝現行動作）。
        // fail-safe: 取得の例外は「現在値なし」に縮退（GetCurrentPriceSafeAsync）。キャンセルは伝播。
        // FR-02, FR-04, #1035, IADR-0451: 供給は現在値と同じ取得の日中文脈（前日終値・始値・高安）を返す。ゲートと参照価格は現在値だけを見る。
        var priceReading = await GetCurrentPriceSafeAsync(trigger, cancellationToken).ConfigureAwait(false);
        var currentPrice = priceReading?.Price;
        var intraday = priceReading?.Intraday;

        // IADR-0099 決定3: 現在値ソースが有効化（IsEnabled=true）されているのに現在値が取れない（取得不可・鮮度切れ）とき
        // だけ、古い/無い価格で発注しないよう安全側（Hold・発注抑止）に倒す。未有効化（既定 no-op・IsEnabled=false）は
        // このゲートを適用せず現行挙動を保つ（既定で全銘柄 Hold にして SIMULATE 検証を壊さない）。
        if (_currentPrice.IsEnabled && currentPrice is null)
        {
            logger.LogInformation("現在値が取得できない/鮮度切れのため見送り（発注抑止・安全側）: {Symbol}", trigger.Symbol);
            return await SkipBeforeLlmAsync(trigger, DecisionForgoneBeforeLlmReason.CurrentPriceUnavailable, cancellationToken)
                .ConfigureAwait(false);
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
            return await SkipBeforeLlmAsync(trigger, DecisionForgoneBeforeLlmReason.FxRateUnresolved, cancellationToken)
                .ConfigureAwait(false);
        }

        var rateToBase = fxReading.Rate.Rate;

        // 🔴 FR-10, #506, ADR-0022 決定5, IADR-0197: 鮮度切れ（30 日超）は**新規建てだけを止める。手仕舞いは止めない。**
        //
        // 従来はここで一律 return しており、**出口が入口と同じゲートで塞がれていた**——
        // 建玉効果（Open / Close）が分かるのは LLM の判断後だからである。
        // **「止められない」より「閉じられない」ほうが危険である**（損失を抱えた建玉から出られない）。
        //
        // 費用の据え置き（IADR-0107 決定2）: 保有が無ければ建玉効果は Open にしかならないため、
        // **鮮度切れかつ保有なし（または不明）のときは即座に見送る**（LLM を呼ばない）。
        // 鮮度切れの経路では、先に引いた保有数を後段で再利用する（同じ照会を二重に打たない）。
        // ［#854］保有状況の照会そのものは、鮮度切れに限らず LLM 呼び出しの前に常に行うようになった（直下）。
        //
        // 🔴 FR-04, FR-10, ADR-0003, #854, IADR-0351 決定1: **保有状況は判断の入力である**（計画 ADR-0003 の判断入力
        // 「確定済み日報＋保有ポジション＋…」）。従来はここで引かず、LLM は保有を知らないまま毎サイクルを新規買いの是非として
        // 判断していた（実測: 2 夜連続で Buy しか出ず、当日枠を使い切るまで買い増した）。LLM 呼び出しの前に 1 回引き、
        // 本判断・一次スクリーニングの両プロンプトへ渡す。null＝不明（プロンプトは「不明」と明示し、保有なしとは書かない）。
        var heldPosition = await GetHeldPositionSafeAsync(trigger, cancellationToken).ConfigureAwait(false);

        // 🔴 FR-04, FR-10, ADR-0003, #934, IADR-0390 決定2/決定4: 当日の未約定の新規建て注文（約定済みの保有とは別の第 3 の状態）。
        // 実測: 指値 715 株が板に残っている間に、判断は「保有なし」を前提に同じ銘柄を重ねて買った。null＝不明
        // （プロンプトは「保有なし」と書かない。実結線なら下で新規建てを見送る）。
        var workingEntries = await GetWorkingEntryOrdersSafeAsync(trigger, cancellationToken).ConfigureAwait(false);

        int? preFetchedHeldQuantity = null;
        if (!fxReading.UsableForEntry)
        {
            // 鮮度切れの経路は従来どおり先読みの数量を後段で再利用する（同じ照会を二重に打たない・#506）。
            preFetchedHeldQuantity = heldPosition?.SignedQuantity;

            if (preFetchedHeldQuantity is not { } held || held == 0)
            {
                logger.LogInformation(
                    "換算レートが鮮度切れで保有も無いため見送り（新規建てのみ停止・手仕舞いは対象外）: " +
                    "{Symbol} market={Market} asOf={AsOf}",
                    trigger.Symbol, trigger.Market, fxReading.Rate.AsOf);
                return await SkipBeforeLlmAsync(
                        trigger, DecisionForgoneBeforeLlmReason.FxRateStaleNoHolding, cancellationToken)
                    .ConfigureAwait(false);
            }

            logger.LogWarning(
                "換算レートが鮮度切れだが保有があるため判断を続行する（手仕舞いのみ許可・ADR-0022 決定5）: " +
                "{Symbol} held={Held} asOf={AsOf}",
                trigger.Symbol, held, fxReading.Rate.AsOf);
        }

        // 🔴 FR-10, #1176, IADR-0495 決定2: **新規建てに使える金額の上限が最小の名目額に届かない銘柄は、LLM を呼ぶ前に見送る。**
        // 名目額はサイジングの金額キャップ（1 注文上限・段階残枠・日次残枠の最小）を超えないため、上限が equity × しきい値を下回れば
        // LLM の結論に依らず新規建ては必ず見送られる（下のサイジングの後の判定）。省くのは #1113 と同じ線引き（保有が既知で 0・未約定が既知で空。
        // この銘柄では LLM の結論は新規の買い〔必ず見送り〕・売り〔裸の新規売りとして必ず見送り〕・Hold しか無い）。資金・残枠が未供給（null）なら
        // 省かない（「分からない」を「届かない」と読まない。従来どおり LLM の後に数量 0 で見送る）。手元の値だけで決まるので照会より先に置く。
        if (heldPosition is { SignedQuantity: 0 } && workingEntries is { Any: false }
            && context is { Capital: { } entryEquity, StageCapitalRemaining: { } stageRemaining, DailyOrderRemaining: { } dailyRemaining }
            && MinimumEntryNotional.CapacityCannotReach(
                entryEquity,
                context.Limits.MaxOrderAmountFor(entryEquity),
                Math.Max(0m, Math.Min(stageRemaining, dailyRemaining)),
                _minimumEntryNotional.Ratio))
        {
            logger.LogInformation(
                "新規建てに使える金額の上限が最小の名目額に届かないため LLM を呼ばずに見送り（保有 0・未約定なし・IADR-0495）: " +
                "{Symbol} capacity={Capacity} minimum={Minimum} ratio={Ratio}",
                trigger.Symbol,
                Math.Min(context.Limits.MaxOrderAmountFor(entryEquity), Math.Max(0m, Math.Min(stageRemaining, dailyRemaining))),
                MinimumEntryNotional.MinimumFor(entryEquity, _minimumEntryNotional.Ratio),
                _minimumEntryNotional.Ratio);
            return await SkipBeforeLlmAsync(
                    trigger, DecisionForgoneBeforeLlmReason.EntryCapacityBelowMinimumNotional, cancellationToken)
                .ConfigureAwait(false);
        }

        // 🔴 FR-10, #1174, IADR-0500 決定1・2: **段階残枠と日次残枠の小さい方が現在値 × 1 株（基準通貨）に満たない銘柄は、LLM を呼ぶ前に見送る。**
        // サイジングの参照価格は現在値があれば現在値そのもの（下のアンカリング・IADR-0099 決定2）、換算は同じ rateToBase であり、金額キャップは
        // この残枠以下なので、判定が真なら LLM の結論（損切り幅）に依らず数量は必ず 0 になる（同じ式で比べる PositionSizer.CannotAffordOneShare）。
        // 線引きは上の #1176 と同じ（保有が既知で 0・未約定が既知で空）。省かない: 現在値が無い（LLM の参照価格を使う構成）・残枠が未供給（null）。
        // equity は使わない（1 株の判定は equity に依存しない）。🔴 **#1176 の判定の後に置く**——残枠が最小の名目額にも届かないときは資金の枯渇が
        // 原因であり、そちらの理由で記録する（本理由は「残枠はあるがこの銘柄の 1 株に届かない」に絞る）。ちょうど 1 株の価格は省かない。
        if (heldPosition is { SignedQuantity: 0 } && workingEntries is { Any: false }
            && currentPrice is > 0m
            && context is { StageCapitalRemaining: { } oneShareStage, DailyOrderRemaining: { } oneShareDaily }
            && PositionSizer.CannotAffordOneShare(
                Math.Max(0m, Math.Min(oneShareStage, oneShareDaily)), currentPrice.Value * rateToBase))
        {
            logger.LogInformation(
                "段階残枠・日次残枠が現在値 × 1 株に満たないため LLM を呼ばずに見送り（保有 0・未約定なし・IADR-0500）: " +
                "{Symbol} available={Available} priceInBase={PriceInBase}",
                trigger.Symbol, Math.Max(0m, Math.Min(oneShareStage, oneShareDaily)), currentPrice.Value * rateToBase);
            return await SkipBeforeLlmAsync(
                    trigger, DecisionForgoneBeforeLlmReason.EntryCapacityBelowOneShare, cancellationToken)
                .ConfigureAwait(false);
        }

        // 🔴 FR-10, FR-04, ADR-0003, #1113, IADR-0463 決定 1・4: **新規建てが審査で必ず拒否される銘柄は、LLM を呼ぶ前に見送る。**
        // 省けるのは「保有が既知で 0、かつ未約定の新規建てが既知で空」のときだけである —— この銘柄では LLM の結論は
        // 買いの新規建て（審査で必ず落ちる）か、売り（裸の新規ショートとして NakedShortOpen で必ず見送る）か、Hold しか無い。
        // 保有中・未約定あり・不明の銘柄では照会もしない（決済の判断は必ず残す。IADR-0358 決定 2 と同じ線引き）。
        // 可否はリスク管理が審査と同じ述語で答える（規則を判断側に持たない）。照会の失敗・未結線（不明）は LLM を呼ぶ側へ倒す。
        // 🔴 **審査は残す**（両端で止める）。ここで省くのは費用の最適化であって統制ではない。
        // ShortSide は見ない（保有 0 の売りは上のとおり必ず見送られる）。
        if (heldPosition is { SignedQuantity: 0 } && workingEntries is { Any: false }
            && await GetEntryBlockersSafeAsync(trigger, cancellationToken).ConfigureAwait(false) is { } blockers
            && blockers.ForEntry(TradeSide.Buy) is { Count: > 0 } longBlockers)
        {
            logger.LogInformation(
                "新規建てが審査で必ず拒否されるため LLM を呼ばずに見送り（保有 0・未約定なし・審査は不変・IADR-0463）: " +
                "{Symbol} reasons={Reasons}",
                trigger.Symbol, string.Join(",", longBlockers));
            return await SkipBeforeLlmAsync(
                    trigger, DecisionForgoneBeforeLlmReason.EntryBlockedByRiskControls, cancellationToken)
                .ConfigureAwait(false);
        }

        // 🔴 FR-10, FR-04, ADR-0003, #1130, IADR-0471 決定 1: **保有中の銘柄でも**新規建ての可否を読む（LLM は必ず呼ぶ＝決済の判断を残す）。
        // 口の答えは保有と無関係に「この銘柄のその方向の新規建て」の審査の述語であり、買い増し（ロングへの Buy）・売り増し（ショートへの Sell）
        // にもそのまま当たる。上の #1113 の関門（保有 0）とは条件が排他で、#1113 の経路は変わらない。照会の失敗・未結線（null）は従来どおり。
        var heldEntryBlockers = heldPosition is { SignedQuantity: not 0 }
            ? await GetEntryBlockersSafeAsync(trigger, cancellationToken).ConfigureAwait(false)
            : null;
        var addOnBlockers = heldEntryBlockers?.ForEntry(heldPosition!.IsLong ? TradeSide.Buy : TradeSide.Sell);

        // FR-08, IADR-0072: 収集情報・判断根拠を KB から RAG 取得して判断文脈に加える（既定＝空＝文脈なし＝現行動作）。
        // fail-safe: 取得は判断のクリティカルパス外。例外・遅延で判断を止めないよう、失敗は「文脈なし」に縮退する
        //（#18 アダプタ自体も fail-safe だが、独自アダプタ差し替え時の保険として判断境界でも握る）。
        var retrieved = await RetrieveContextSafeAsync(trigger, policy, cancellationToken).ConfigureAwait(false);

        // 🔴 FR-04, FR-02, #1034, IADR-0440 決定 2/6: 判断時点の監視銘柄（権威源＝市場監視から読めた一覧）。null＝不明
        // （プロンプトは「不明」と明示し、「監視銘柄なし」「この銘柄は対象外」とは書かない）。**読めないことでは見送らない**
        // ——判断の可否は従来どおりで、変わるのはプロンプトの文言だけである。見送りの判定の後に引く（見送る判断で照会しない）。
        var watchlist = await GetWatchlistForPromptSafeAsync(trigger, cancellationToken).ConfigureAwait(false);

        // IADR-0039: 本判断プロンプトを構築し、多数決・二段をオーケストレータへ委譲する。一次スクリーニングプロンプトは
        // スクリーニング有効時のみ構築されるよう遅延ファクトリで渡す（既定＝無効の経路で無駄な構築をしない）。
        // IADR-0072 決定2: RAG 文脈は本判断のみに載せ、一次スクリーニング（費用統制）には載せない。
        // FR-17, IADR-0076 決定5: 採算ゲート有効時のみプロンプトに採算節を注入する（無効の既定は現行動作のプロンプトと一致）。
        // FR-04, ADR-0020 決定2, #1081, IADR-0455: ニュースの状態（取得済み／欠測／未構成。期限切れ・未受信は null＝不明）を
        // 本判断・一次の両方へ同じ値で渡す（RAG を経由しない欠測の明示。一次は門であり、ここで欠けると本判断へ届かない）。
        var news = _newsStatus?.Current(clock.UtcNow);

        // 🔴 FR-04, ADR-0048 決定 2・3, #1118, IADR-0467 決定 4・6: 前営業日の出来高と 20 日平均比（日足から計算）。
        // **無効（既定）なら引かない**（要求 0 回＝取得枠に触れない・プロンプトは従来の「未提供」の行）。見送りの判定の後に引く
        // （見送る判断で取得しない）。**取得できないことで判断を止めない**（「未提供」と書いて続ける）。
        var volume = _dailyBars.IsEnabled
            ? DailyVolumeContext.From(await GetDailyBarsSafeAsync(trigger, cancellationToken).ConfigureAwait(false))
            : null;

        // 🔴 FR-10, ADR-0049 決定2, #1122, IADR-0486 決定2: 損切り幅の下限（ATR(14)）を**判断ごとに 1 回だけ、プロンプトの前に**読む。
        // 同じ値をプロンプト（ATR と下限の行）・下限の適用・発注意図の印・監査へ使う（プロンプトと適用で値が食い違わない）。
        // **無効（既定）なら読まない**（要求 0 回・プロンプトは従来のまま・下限は参照価格の 2%）。見送りの判定の後に読む（出来高と同じ位置。
        // 日足は出来高と同じ口・同じキャッシュ）。**得られないことで判断を止めない**（2% へ退避する）。
        var stopFloor = _stopWidthFloor.IsEnabled
            ? new StopWidthFloorContext(await GetStopWidthFloorSafeAsync(trigger, cancellationToken).ConfigureAwait(false))
            : null;

        var decisionPrompt = TradeDecisionPromptBuilder.Build(
            trigger, policy, context, retrieved, includeProfitability: _profitabilityOptions.Enabled,
            currentPrice: currentPrice, held: heldPosition, working: workingEntries, watchlist: watchlist, intraday: intraday,
            news: news, volume: volume, addOnBlockers: addOnBlockers, stopFloor: stopFloor);

        // #337, IADR-0247: 縮退制御が有効（スクリーニング有効かつ予算設定）なときだけ、スクリーニング入力
        // （方針・市況＝保護、RAG・ニュース＝削減可）へ縮退順序 ①分割→②RAG→③ニュース を適用する。
        // 未設定（既定）は従来プロンプト（参考情報なし・IADR-0072 決定2）＝現行挙動。
        // #1034, IADR-0440 決定 5: 監視銘柄節（保護分）の長さも見積りへ入れる。
        var screening = _options is { EnableScreening: true, ScreeningContextBudgetChars: { } budget }
            ? ScreeningContextAssembler.Assemble(trigger, policy, retrieved, currentPrice, budget, watchlist)
            : null;

        var orchestrated = await _orchestrator.DecideAsync(
            // #854, IADR-0351 決定4: 一次は門である（Hold で本判断が走らない）ため、保有状況は一次にも渡す。
            // 🔴 縮退制御なしの経路でも現在値を渡す（#860 の監査の指摘）。渡さないと、定時トリガー（価格を持たない）では
            // 一次の保有状況が常に「到達したかは不明」になり、門である一次だけが損切りライン到達を知らない。
            // #1034, IADR-0440 決定 1: 一次（門）にも監視銘柄を渡す（所属を誤読して落とすと本判断へ届かない）。
            () => screening is null
                ? TradeDecisionPromptBuilder.BuildScreening(
                    trigger, policy, context, currentPrice, held: heldPosition, working: workingEntries, watchlist: watchlist,
                    intraday: intraday, news: news, volume: volume, addOnBlockers: addOnBlockers)
                : TradeDecisionPromptBuilder.BuildScreening(
                    trigger, policy, context, currentPrice, screening.RetainedReferences, heldPosition, workingEntries,
                    watchlist, intraday, news, volume, addOnBlockers),
            decisionPrompt,
            // 🔴 FR-04, FR-10, #1187, IADR-0248: 二次本判断の解釈へ、プロンプトへ渡したのと同じ保有（null＝不明）を渡す。
            // 保有を決済する売買（ロング保有中の Sell・ショート保有中の Buy）では損切り幅を任意にする（決済は保有全量で損切り幅を
            // 使わない）。発注の建玉効果は下で LLM の後に引き直した保有で決める（IADR-0351 決定6）——ずれて新規建てになっても、
            // 新規建ての損切り幅の再検証（<= 0 → StopLossDistanceInvalid）が未使用の印 0 を必ず落とす。
            heldPosition?.SignedQuantity,
            cancellationToken)
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

        // 🔴 UC-02, FR-03, #1077, IADR-0452 決定1: ここから先の見送りは AI 判断の後である（基準点になる）。
        var judgedPrice = JudgedPriceOf(orchestrated, currentPrice, trigger);

        if (decision.Action == TradeAction.Hold)
        {
            return await SkipJudgedAsync(trigger, DecisionSkipReason.LlmHold, judgedPrice, cancellationToken)
                .ConfigureAwait(false); // 見送り
        }

        var side = decision.Action == TradeAction.Buy ? TradeSide.Buy : TradeSide.Sell;

        // FR-04, FR-05, FR-10, #292, IADR-0119: 保有建玉から建玉効果を決める。従来は Open がリテラル固定で、
        // LLM の Sell が「保有ロングの決済」ではなく新規ショート建てとして扱われていた（AI に出口が無かった）。
        // 鮮度切れの経路では上で先読み済み（ブローカ照会を二重に打たない・#506）。
        // #854, IADR-0351 決定6: それ以外の経路では、プロンプト用に引いた保有状況を**使い回さず引き直す**。LLM 呼び出しの間に
        // 逆指値が約定し得るため、決済の数量（保有全量）は発注直前の事実で決める（古い数量での決済は在庫を超え得る）。
        var heldQuantity = preFetchedHeldQuantity
            ?? await GetSignedHeldQuantitySafeAsync(trigger, cancellationToken).ConfigureAwait(false);
        // 🔴 FR-04, FR-10, ADR-0003, #865, IADR-0358: 保有状況の照会先が**実結線**されている（IsEnabled=true）のに
        // 保有が不明なら、新規建て（Open）を見送る。#860（IADR-0351 決定2）が載せたプロンプトの「不明なら Hold」は
        // **LLM への依頼であってコードの統制ではない** —— 従わなければ、保有を知らないままの新規買いが従来どおり通る。
        // 未結線（NoOp＝常に不明）の既定構成は「照会していない」であり、従来どおり新規建てを通す（IADR-0119 決定2）。
        var effect = PositionEffectResolver.Resolve(
            side, heldQuantity, requireKnownHoldingForOpen: _heldPosition.IsEnabled);
        if (effect.IsSkipped)
        {
            // 🔴 見送りの理由を取り違えない。**出口（Close）はここまで来ない** —— 不明のときは決済の分岐に入りようがなく、
            // 保有が判っていれば Close は上で確定している（FR-10「手仕舞いは止めない」）。
            if (side == TradeSide.Buy)
            {
                // #865, IADR-0358: 実結線の照会が不明を返した（照会失敗・例外）状態での新規建て。
                // 金額系の統制（1 注文上限・当日残枠・段階残枠）は sizing-context の照会が生きている前提であり、
                // 保有を知らないままの買い増しを止められない（#854 の実測）。
                logger.LogWarning(
                    "保有状況が不明なため新規建てを見送る（照会先は結線済み・手仕舞いは止めない・IADR-0358）: " +
                    "{Symbol} side={Side}",
                    trigger.Symbol, side);
                return await SkipJudgedAsync(trigger, DecisionSkipReason.HoldingsUnknownOpen, judgedPrice, cancellationToken)
                    .ConfigureAwait(false);
            }

            // 保有なし・不明での売り＝裸の新規ショート建て。現物のみ有効な段階では成立せず、取引ガードは方向を
            // 見ないため素通りしてブローカへ飛ぶ。ADR-0003（不確実なら Hold）に従い見送る。
            logger.LogInformation(
                "保有建玉が無い、または不明な売り判断のため見送り（裸の新規売りを出さない・IADR-0119）: {Symbol} held={Held}",
                trigger.Symbol, heldQuantity.HasValue ? heldQuantity.Value : "不明");
            return await SkipJudgedAsync(trigger, DecisionSkipReason.NakedShortOpen, judgedPrice, cancellationToken)
                .ConfigureAwait(false);
        }

        // 🔴 FR-02, FR-04, #1286, IADR-0521 決定 2: 監視銘柄の外の保有銘柄（保有のみ）は**出口専用**で判断している。
        // LLM が新規建て（買い増し・売り増し、または判断の間に保有が 0 になった後の新規建て）を返しても発注意図を作らず Hold に倒す。
        // 🔴 決済（Close）は対象外（!effect.IsClose）。監視銘柄の外への新規建ての可否は計画に定めが無いため、出口に限る。
        if (trigger.ExitOnly && !effect.IsClose)
        {
            logger.LogInformation(
                "監視銘柄の外の保有銘柄への新規建ては出さない（出口専用の判断・決済は対象外・IADR-0521）: {Symbol} side={Side} effect={Effect}",
                trigger.Symbol, side, effect.Effect);
            return await SkipJudgedAsync(trigger, DecisionSkipReason.ExitOnlyOpenOutsideWatchlist, judgedPrice, cancellationToken)
                .ConfigureAwait(false);
        }

        // 🔴 FR-04, FR-10, ADR-0003, #934, IADR-0390 決定5: 実結線のもとで未約定の新規建て注文が**不明**なら新規建てを見送る
        // （#865 / IADR-0358 と同じ形）。不明を「無い」と読めば、板に残った指値を知らないまま同じ銘柄を重ねて買う。
        // 手仕舞い（Close）は止めない —— 決済の数量は約定済みの保有だけで決まり、未約定の照会とは独立である。
        if (!effect.IsClose && _heldPosition.IsEnabled && workingEntries is null)
        {
            logger.LogWarning(
                "未約定の新規建て注文が不明なため新規建てを見送る（照会先は結線済み・手仕舞いは止めない・IADR-0390）: " +
                "{Symbol} side={Side}",
                trigger.Symbol, side);
            // 🔴 PR #940 監査, IADR-0374: 見送りは唯一の出口 Skip を通す（素の null は decision_skips にもアラートにも出ない）。
            return await SkipJudgedAsync(trigger, DecisionSkipReason.WorkingEntriesUnknownOpen, judgedPrice, cancellationToken)
                .ConfigureAwait(false);
        }

        // 🔴 FR-10, FR-04, ADR-0003, #1130, IADR-0471 決定 3: LLM の前に読んだ口が**この方向の新規建て**（保有中の銘柄の買い増し・売り増し）は
        // 審査で必ず拒否されると答えていたのに LLM がそれを返したら、発注意図を作らず Hold に倒す（判断後の見送り。TradeDecisionHeld を出す）。
        // 🔴 決済（Close）はここまで来ても対象外（!effect.IsClose）。照会していない・不明（null）なら倒さない（審査が止める）。
        // 🔴 **審査は残す**（両端で止める）。ここで倒すのは審査が必ず落とす注文だけで、統制を緩めない。
        if (!effect.IsClose && heldEntryBlockers?.ForEntry(side) is { Count: > 0 } addOnReasons)
        {
            logger.LogInformation(
                "買い増し・売り増しが審査で必ず拒否されるため Hold に倒す（LLM の結論を発注しない・決済は対象外・審査は不変・IADR-0471）: " +
                "{Symbol} side={Side} reasons={Reasons}",
                trigger.Symbol, side, string.Join(",", addOnReasons));
            return await SkipJudgedAsync(trigger, DecisionSkipReason.AddOnBlockedByRiskControls, judgedPrice, cancellationToken)
                .ConfigureAwait(false);
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
            return await SkipJudgedAsync(trigger, DecisionSkipReason.ReferencePriceInvalid, judgedPrice, cancellationToken)
                .ConfigureAwait(false);
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
            return await SkipJudgedAsync(trigger, DecisionSkipReason.FxRateStaleOpen, judgedPrice, cancellationToken)
                .ConfigureAwait(false);
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

            // NFR-01, NFR-02, #689, IADR-0307: 取引サイクルの起点を下流（承認・発注・記録）へ運ぶ。
            return new TradeDecisionMade(
                Guid.NewGuid(), closeIntent, ReconcileRationale(trigger, decision.Rationale, effect.CloseQuantity), clock.UtcNow,
                trigger.MetricTrigger, trigger.CycleStartedAt);
        }

        // 以降は新規建て（Open）の従来経路。IADR-0035 の不変量（損切り幅は参照価格より小さく正）を権威価格に対して
        // 再検証する（既定は Parser が保証済みのため素通り＝挙動不変）。
        // #1120, IADR-0465 決定1: この検証は **AI の幅**に対して先に行う（壊れた出力は「狭い」とは別であり、下限で救わない）。
        if (decision.StopLossDistancePerShare <= 0m || decision.StopLossDistancePerShare >= referencePrice)
        {
            logger.LogInformation(
                "損切り幅が不正、または現在値以上のため見送り: {Symbol} referencePrice={ReferencePrice} stopLossDistance={StopLossDistance}",
                trigger.Symbol, referencePrice, decision.StopLossDistancePerShare);
            return await SkipJudgedAsync(trigger, DecisionSkipReason.StopLossDistanceInvalid, judgedPrice, cancellationToken)
                .ConfigureAwait(false);
        }

        // 🔴 FR-10, ADR-0003, ADR-0049 決定1〜3, #1120, IADR-0465 決定1: 損切り幅に下限を掛ける（AI は上書きできない）。
        // 下限はアンカー後の参照価格で求める（ラインを引く価格と同じ。LLM の参照価格で求めると、窓の間に上がった分だけ下限を割る）。
        // 下限を割った幅は下限まで広げ、**見送らない**。以降のサイジング・ライン・発注意図・監査はすべて適用した幅を使う。
        // #1122, IADR-0486 決定2: ATR の下限はプロンプトの前に読んだ値を使う（読み直さない）。無効・得られないときは 2%。
        var stopWidth = StopWidthFloorPolicy.Apply(
            decision.StopLossDistancePerShare, StopWidthFloorPolicy.Resolve(stopFloor?.Supplied, referencePrice));
        // 下限で広げた幅が参照価格以上ならラインが成立しない（ロングは 0 以下）。2% の退避では起こらず、将来の ATR が
        // 価格以上を返した極端な場合だけに当たる。幅を価格未満へ縮めると下限を割るため、IADR-0035 の不変量で見送る。
        if (stopWidth.AppliedWidthPerShare >= referencePrice)
        {
            logger.LogWarning(
                "損切り幅の下限が現在値以上のため見送り（下限を割って縮めない・IADR-0465）: {Symbol} referencePrice={ReferencePrice} "
                    + "stopWidthFloor={StopWidthFloor} floorSource={FloorSource}",
                trigger.Symbol, referencePrice, stopWidth.FloorPerShare, stopWidth.FloorSource);
            return await SkipJudgedAsync(trigger, DecisionSkipReason.StopLossDistanceInvalid, judgedPrice, cancellationToken)
                .ConfigureAwait(false);
        }

        // FR-10, FR-17, #257, #364, IADR-0107 決定1/2: サイジングの入力を基準通貨（USD）へ揃える。資金・上限・残枠は基準通貨、
        // 参照価格・損切り幅は銘柄のローカル通貨のため、1 株あたり金額にレートを掛けてから PositionSizer へ渡す
        // （混在させると金額上限が桁で誤り、過大発注を招く）。基準通貨の市場はレート 1 で現行と同値。
        var referencePriceBase = referencePrice * rateToBase;
        var stopLossDistanceBase = stopWidth.AppliedWidthPerShare * rateToBase;

        // IADR-0003: サイジングは判断サービスの責務。availableCapital は段階残枠と日次発注残枠の小さい方（IADR-0017）。
        var sizeFactor = PositionSizer.GetSizeFactor(context.ConsecutiveLosses, context.DrawdownRatio, context.Limits);
        // FR-10, #869, ADR-0041 決定2, IADR-0354: 基準資金・残枠は**未供給（null）があり得る**（口座を照会できていない）。
        // 未供給は 0 として畳み、サイジングは数量 0 ＝ 見送りに倒れる（発注審査側も CapitalBaselineUnavailable で止める）。
        var capital = context.Capital ?? 0m;
        var availableCapital = Math.Max(
            0m, Math.Min(context.StageCapitalRemaining ?? 0m, context.DailyOrderRemaining ?? 0m));
        var quantity = PositionSizer.CalculateCappedQuantity(
            capital,
            context.Limits.PerTradeRiskRatio,
            stopLossDistanceBase,
            referencePriceBase,
            // FR-10, #329, IADR-0130 決定1: 1 注文金額上限は equity 比のため equity（context.Capital）から解決する。
            // 「1 取引リスク 1%」と「1 注文 25%」のどちらが厳しいかは CalculateCappedQuantity が min で採る。
            context.Limits.MaxOrderAmountFor(capital),
            availableCapital,
            sizeFactor);

        if (quantity <= 0)
        {
            logger.LogInformation("サイジングで数量 0 のため見送り: {Symbol}", trigger.Symbol);
            return await SkipJudgedAsync(trigger, DecisionSkipReason.SizingZeroQuantity, judgedPrice, cancellationToken)
                .ConfigureAwait(false);
        }

        // 🔴 FR-10, #1176, IADR-0495 決定1: **最小の名目額（equity × しきい値。既定 1%）に満たない新規建ては見送る**（建玉枠・承認を消費しない）。
        // 名目額は発注する数量 × 参照価格（基準通貨）。ちょうど等しいときは通す。新規建て（買い増し・売り増しを含む）だけで、決済は上で確定済み。
        // 審査は数量を減らさない（承認の数量＝意図の数量）ので、ここで判定した名目額がそのまま発注される。
        var notionalBase = quantity * referencePriceBase;
        if (MinimumEntryNotional.IsBelow(notionalBase, capital, _minimumEntryNotional.Ratio))
        {
            logger.LogInformation(
                "サイジングの名目額が最小の名目額に満たないため見送り（IADR-0495）: {Symbol} quantity={Quantity} notional={Notional} " +
                "minimum={Minimum} ratio={Ratio}",
                trigger.Symbol, quantity, notionalBase,
                MinimumEntryNotional.MinimumFor(capital, _minimumEntryNotional.Ratio), _minimumEntryNotional.Ratio);
            return await SkipJudgedAsync(trigger, DecisionSkipReason.SizedBelowMinimumNotional, judgedPrice, cancellationToken)
                .ConfigureAwait(false);
        }

        // FR-17, 05_trading-assumptions §4, IADR-0076: 採算評価ゲート（opt-in・既定無効＝現行挙動）。
        // 有効時は往復の概算費用に対して想定利益が最小期待利益しきい値を満たすかを評価し、採算不成立・費用見積り不能は Hold に倒す。
        // IADR-0107 決定2: 費用・最小期待利益は基準通貨で登録されている（計画 05_trading-assumptions §2）ため、
        // notional・想定利益も基準通貨で突き合わせる。
        if (_profitabilityOptions.Enabled &&
            !await IsProfitableAsync(
                trigger, decision, referencePriceBase, rateToBase, quantity, cancellationToken).ConfigureAwait(false))
        {
            return await SkipJudgedAsync(trigger, DecisionSkipReason.ProfitabilityNotViable, judgedPrice, cancellationToken)
                .ConfigureAwait(false); // 採算不成立・見積り不能（安全側で見送り）
        }

        // FR-03/04, IADR-0035, IADR-0099: 損切り価格を算出して発注意図に載せる（#63 台帳へ永続化し市場監視の損切り検知に実値供給）。
        // ロングは参照価格より下、ショートは上に損切りラインを置く（StopLossEvaluator と対称）。参照価格はアンカリング済み。
        // #1120, IADR-0465 決定1: 幅は下限を掛けた幅（ADR-0049 決定1「損切りの実行機構は下限を掛けた後のラインを使う」）。
        var stopLossPrice = side == TradeSide.Buy
            ? referencePrice - stopWidth.AppliedWidthPerShare
            : referencePrice + stopWidth.AppliedWidthPerShare;

        // FR-10, FR-04, #1104, IADR-0460 決定1〜3, #1120, IADR-0465 決定3: 損切り幅の観測（ログ。判断を変えない）。
        // AI の幅の傾向（比率・日中の値幅に対する倍率）と、下限・出所・適用した幅・広げたかを並べて出す。
        LogStopWidth(trigger, side, quantity, decision, referencePrice, intraday, stopWidth, stopLossPrice, stopFloor);

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
            rateToBase,
            // 🔴 FR-10, ADR-0049 決定1, #1122, IADR-0486 決定5: このラインを下限を掛けてから引いた印（出所）。発注執行が発注結果の記録と
            // 予約の行に残し、既存の S1 への下限の遡及（IADR-0472）がこの行を広げない（ATR の下限は参照価格の 2% より狭いことがある）。
            StopFloorSource: stopWidth.FloorSource);

        // NFR-01, NFR-02, #689, IADR-0307: 取引サイクルの起点を下流（承認・発注・記録）へ運ぶ。
        // FR-10, FR-11, ADR-0049 決定3, #1120, IADR-0465 決定2: 下限を掛けた結果を監査台帳へ残す（判断の記録に載せる）。
        return new TradeDecisionMade(
            Guid.NewGuid(), intent, ReconcileRationale(trigger, decision.Rationale, quantity), clock.UtcNow,
            trigger.MetricTrigger, trigger.CycleStartedAt, stopWidth);
    }

    // FR-10, ADR-0049 決定2, #1120, IADR-0465 決定1, #1122, IADR-0486 決定2: 下限の供給口を読む（fail-safe）。例外（キャンセルを除く）は
    // null（＝得られない）に縮退し、適用の側（StopWidthFloorPolicy.Resolve）が参照価格（アンカー後）の 2% へ退避する。
    // **下限が得られないことを理由に見送らない**（ADR-0049「ATR が得られないときは参照価格の 2%」）。キャンセルは伝える。
    private async Task<StopWidthFloor?> GetStopWidthFloorSafeAsync(
        DecisionTrigger trigger, CancellationToken cancellationToken)
    {
        try
        {
            return await _stopWidthFloor
                .GetFloorAsync(trigger.Symbol, trigger.Market, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(
                ex, "損切り幅の下限の供給に失敗しました（参照価格の 2% を下限とします）: {Symbol}", trigger.Symbol);
            return null;
        }
    }

    // FR-10, FR-04, #1104, IADR-0460 決定1/決定3: 新規建ての損切り幅の観測値を構造化 Information ログへ出す。
    // 不明（日中の値幅が無い）は 0 ではなく「不明」と書く。
    // #1120, IADR-0465 決定3: stopWidth・比率・倍率は **AI の幅**のまま（AI の提案の傾向を測る）。下限・出所・適用した幅・
    // 広げたかを足す。監査台帳へは TradeDecisionMade.StopWidth が残す（IADR-0465 決定2）。
    private void LogStopWidth(
        DecisionTrigger trigger, TradeSide side, int quantity, LlmDecision decision, decimal anchoredPrice,
        IntradayPriceContext? intraday, StopWidthFloorApplication stopWidth, decimal stopLossPrice,
        StopWidthFloorContext? stopFloor)
    {
        var observed = StopWidthObservation.Of(
            decision.ReferencePrice, anchoredPrice, decision.StopLossDistancePerShare, intraday, stopWidth);

        logger.LogInformation(
            "損切り幅の観測（新規建て・LLM の幅と下限）: {Symbol} side={Side} quantity={Quantity} "
                + "llmReferencePrice={LlmReferencePrice} anchoredPrice={AnchoredPrice} anchorDiff={AnchorDifference} "
                + "stopWidth={StopWidthPerShare} stopWidthPct={StopWidthPercent} intradayRange={IntradayRange} "
                + "stopWidthToRange={StopWidthToIntradayRange} floor={StopWidthFloor} floorSource={FloorSource} "
                + "appliedWidth={AppliedStopWidth} widened={Widened} stopLossPrice={StopLossPrice} atr14={Atr14}",
            trigger.Symbol, side, quantity,
            observed.LlmReferencePrice, observed.AnchoredPrice, observed.AnchorDifference,
            observed.WidthPerShare, observed.WidthPercentOfAnchored,
            (object?)observed.IntradayRange ?? Unknown, (object?)observed.WidthToIntradayRange ?? Unknown,
            observed.FloorPerShare, observed.FloorSource, observed.AppliedWidthPerShare, observed.Widened,
            stopLossPrice,
            // #1122, IADR-0486 決定3: ATR(14) の値（下限の出所が ATR のときだけ。無効・得られないときは「不明」）。
            stopWidth.FloorSource == StopWidthFloorSource.Atr14 && stopFloor?.Atr14?.Atr is { } atr ? atr : Unknown);
    }

    // #1104, IADR-0460 決定3: 観測ログで値が無いことの表記（0 と読まれない）。
    internal const string Unknown = "不明";

    // FR-04, FR-10, FR-11, ADR-0040 決定5, #822, IADR-0343 決定2: 発行する記録の根拠文をシステムが決めた数量と突合する。
    // 🔴 数量（サイジング・保有全量）はここでは変えない。根拠文の株数が食い違えば LLM の文言を保ったまま注記を追記し、
    // WARN を残す。TradeDecisionMade.Rationale は監査台帳・報告書（ITradeRationaleSource）の唯一の供給元であるため、
    // 発行点で直せば全消費者へ届く。
    private string ReconcileRationale(DecisionTrigger trigger, string rationale, int quantity)
    {
        var reconciled = RationaleQuantityReconciler.Reconcile(rationale, quantity);
        if (reconciled.Mismatched)
        {
            logger.LogWarning(
                "根拠文の株数言及がシステムの数量と食い違うため注記を追記（数量は統制値で決まる・ADR-0040 決定5）: " +
                "{Symbol} quantity={Quantity}",
                trigger.Symbol, quantity);
        }

        return reconciled.Rationale;
    }

    // FR-04, FR-05, #292, IADR-0119: 保有建玉の照会（fail-safe ラッパ）。
    // 例外・キャンセル以外の失敗は **null（不明）** に縮退する。0（保有なし）へ倒すと「保有していない」と誤断定し、
    // 裸の新規売りを通してしまうため、この区別を境界でも守る（アダプタ自体も同じ契約）。
    private async Task<int?> GetSignedHeldQuantitySafeAsync(
        DecisionTrigger trigger, CancellationToken cancellationToken)
    {
        int? quantity;
        try
        {
            quantity = await _heldPosition
                .GetSignedQuantityAsync(trigger.Symbol, trigger.Market, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "保有建玉の照会に失敗しました（不明として扱います）: {Symbol}", trigger.Symbol);
            quantity = null;
        }

        await ReportHoldingsQueryAsync(PositionQuerySource.TradeDecisionHoldings, quantity is not null).ConfigureAwait(false);
        return quantity;
    }

    // FR-04, FR-10, ADR-0003, #854, IADR-0351 決定1/決定2: 判断プロンプトへ載せる保有状況の照会（fail-safe ラッパ）。
    // 例外・キャンセル以外の失敗は **null（不明）** に縮退する。保有なしへ倒すと、LLM は保有を知らないまま新規買いの是非として
    // 判断する（#854 の実測そのもの）。プロンプトは不明を「不明」と明示し、Hold を選ぶよう述べる。
    private async Task<HeldPosition?> GetHeldPositionSafeAsync(
        DecisionTrigger trigger, CancellationToken cancellationToken)
    {
        HeldPosition? held;
        try
        {
            held = await _heldPosition
                .GetPositionAsync(trigger.Symbol, trigger.Market, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "保有状況の照会に失敗しました（不明として扱います）: {Symbol}", trigger.Symbol);
            held = null;
        }

        await ReportHoldingsQueryAsync(PositionQuerySource.TradeDecisionHoldings, held is not null).ConfigureAwait(false);
        return held;
    }

    // FR-04, FR-10, #934, IADR-0390 決定2: 未約定の新規建て注文の照会（fail-safe ラッパ）。
    // 例外・キャンセル以外の失敗は **null（不明）** に縮退する。「無い」へ倒すと、判断は板に残った指値を知らないまま
    // 「保有なし」を前提に同じ銘柄を重ねて買う（#934 の実測そのもの）。
    private async Task<WorkingEntryOrders?> GetWorkingEntryOrdersSafeAsync(
        DecisionTrigger trigger, CancellationToken cancellationToken)
    {
        WorkingEntryOrders? working;
        try
        {
            working = await _heldPosition
                .GetWorkingEntryOrdersAsync(trigger.Symbol, trigger.Market, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "未約定の新規建て注文の照会に失敗しました（不明として扱います）: {Symbol}", trigger.Symbol);
            working = null;
        }

        await ReportHoldingsQueryAsync(PositionQuerySource.TradeDecisionWorkingEntries, working is not null)
            .ConfigureAwait(false);
        return working;
    }

    // 🔴 FR-10, FR-04, #1113, IADR-0463 決定 4: 新規建ての可否の照会（fail-safe ラッパ）。例外・キャンセル以外の失敗は
    // **null（不明）**に縮退する（＝LLM を呼ぶ。見送らない）。本判断のキャンセルは伝える。
    private async Task<EntryBlockers?> GetEntryBlockersSafeAsync(
        DecisionTrigger trigger, CancellationToken cancellationToken)
    {
        try
        {
            return await _entryBlockers.GetAsync(trigger.Symbol, trigger.Market, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "新規建ての可否の照会に失敗しました（不明として扱い LLM を呼びます）: {Symbol}", trigger.Symbol);
            return null;
        }
    }

    // FR-04, ADR-0048 決定 2, #1118, IADR-0467 決定 4: 日足の照会（fail-safe ラッパ）。例外は **null（取得できない＝出来高は未提供）**
    // に縮退する（判断を止めない）。本判断のキャンセルは伝える。
    private async Task<ConfirmedDailyBars?> GetDailyBarsSafeAsync(
        DecisionTrigger trigger, CancellationToken cancellationToken)
    {
        try
        {
            return await _dailyBars.GetConfirmedBarsAsync(trigger.Symbol, trigger.Market, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "日足の照会に失敗しました（出来高は未提供として判断を続けます）: {Symbol}", trigger.Symbol);
            return null;
        }
    }

    // FR-04, #1034, IADR-0440 決定 2: 判断のプロンプトへ載せる監視銘柄の照会（fail-safe ラッパ）。
    // 未配線・権威源から読めない・例外は **null（不明）** に縮退する。空の一覧（＝監視銘柄なし）へ倒すと、LLM に
    // 「この銘柄は対象外」と読ませることになる（#1034 の誤読をシステムが作る）。キャンセルは伝播させる。
    private async Task<IReadOnlyList<WatchedSymbol>?> GetWatchlistForPromptSafeAsync(
        DecisionTrigger trigger, CancellationToken cancellationToken)
    {
        if (_watchlist is null || _watchlistUnavailable)
            return null;

        try
        {
            var read = await _watchlist.GetAuthoritativeWatchlistAsync(cancellationToken).ConfigureAwait(false);
            _watchlistUnavailable = read is null;
            return read;
        }
        // 🔴 PR #1041 の監査 F6: 供給口自身の打ち切り（呼び出し側が止めていないのに出る OperationCanceledException）も不明へ縮退する。
        // 種類で除外すると、市場監視の遅延だけで判断全体が中断される。止めるのは呼び出し側のキャンセルのときだけである。
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "監視銘柄の照会に失敗しました（プロンプトには不明と書きます）: {Symbol}", trigger.Symbol);
            _watchlistUnavailable = true;
            return null;
        }
    }

    // FR-17, 05_trading-assumptions §4, IADR-0076: 採算評価。数量確定後の約定代金に対する往復概算費用と最小期待利益倍率を
    // 設定サービス由来の見積り（_profitability）から取り、想定利益（LLM 由来・想定値幅 × 数量）と ProfitabilityGate で突き合わせる。
    // 採算成立（Viable）のみ true。採算不成立（NotViable）・費用見積り不能（Indeterminate＝前提条件未解決・実額未登録）は false（Hold）。
    // fail-safe: 見積り取得の例外は「見積り不能」に縮退して false（安全側）へ倒す。キャンセルは伝播させる。
    private async Task<bool> IsProfitableAsync(
        DecisionTrigger trigger, LlmDecision decision, decimal referencePriceBase, decimal fxRateToBase,
        int quantity, CancellationToken cancellationToken)
    {
        // IADR-0099: notional はアンカリング済みの参照価格（現在値ありなら権威価格）× 数量で算出する。
        // IADR-0107: 参照価格は基準通貨へ換算済み（費用見積りの単位と揃える）。
        var notional = referencePriceBase * quantity;
        TradeCostAssessment? assessment;
        try
        {
            // #1217, IADR-0508 決定1: 数量を渡す（往復費用に取引諸費用〔米国株の売りの TAF は株数比例〕を含める）。
            assessment = await _profitability.AssessAsync(trigger.Market, quantity, notional, cancellationToken)
                .ConfigureAwait(false);
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
    private async Task<CurrentPriceReading?> GetCurrentPriceSafeAsync(
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
