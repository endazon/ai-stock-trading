extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Llm;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Fx;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// 🔴 UC-02, FR-03, FR-04, ADR-0003, #1077, IADR-0452 決定1/3/4:
// **AI 判断が結論を出した後の見送りは、判断時点の価格つきで TradeDecisionHeld を発行する**（市場監視が急変の基準値を進める）。
//
// 計画の基準点は「前回 AI 判断を行った時点の価格」（04_workflows/02 §補足）。以前は TradeDecisionMade（発注意図あり）だけが
// 基準値を進め、Hold が続いた稼働 PoC（2026-09-28・定時判断 6 銘柄 × 17〜18 回がすべて Hold）で UC-02 が一度も発火しなかった。
//
// 🔴 **判断をしなかった見送り（LLM を呼ぶ前の 4 地点）と解析不能では出さない**（計画の文言「AI 判断を行った時点」に忠実に）。
// 🔴 **見送るかどうかの判定は変えない**。どのケースも従来どおり null（見送り）で、見送りの理由の計上も 1 件のままである。
public class DecisionHeldReportTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 14, 0, 0, TimeSpan.Zero);
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 9, 29), "様子見の方針");

    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";
    private const string SellJson =
        """{"action":"Sell","rationale":"利確","referencePrice":1000,"stopLossDistancePerShare":30}""";
    private const string HoldJson = """{"action":"Hold","rationale":"様子見"}""";
    private const string NonBaseBuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":300000,"stopLossDistancePerShare":7500}""";
    private const string Garbage = "判断できませんでした（JSON なし）";

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    // 呼ばれるたびに次の出力を返す（最後の出力を繰り返す）。多数決の票ごとに出力を変えるため。
    private sealed class SequenceLlm(params string[] outputs) : ILlmCompletionClient
    {
        private int _calls;

        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default) =>
            Task.FromResult(outputs[Math.Min(_calls++, outputs.Length - 1)]);
    }

    private sealed class FakePolicy(DailyPolicy? policy) : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(policy);
    }

    private sealed class FakeSizing(SizingContext ctx) : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(ctx);
    }

    // 実結線（IsEnabled=true）の保有照会。null は「不明」、0 は「保有なし」。
    private sealed class FakeHeld(int? signedQuantity, bool workingUnknown = false) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult(signedQuantity);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult(signedQuantity switch
            {
                null => null,
                0 => HeldPosition.None,
                { } q => new HeldPosition(q, 1_000m, q > 0 ? 970m : 1_030m),
            });

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
            string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult(workingUnknown ? null : WorkingEntryOrders.None);
    }

    private sealed class FakeCurrentPrice(decimal? price) : ICurrentPriceProvider
    {
        public bool IsEnabled => true;

        // #1035（#1079）: 価格供給は日中文脈つきの読み取りを返す。本スイートは価格だけを見る（日中文脈は不明）。
        public Task<CurrentPriceReading?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken ct = default) =>
            Task.FromResult(price is { } p ? new CurrentPriceReading(p, IntradayPriceContext.Unknown) : null);
    }

    private sealed class FakeFxRate(decimal? rate, FxRateFreshness freshness = FxRateFreshness.Fresh)
        : IFxRateProvider
    {
        public Task<decimal?> GetRateToBaseAsync(Market market, CancellationToken ct = default) =>
            Task.FromResult(freshness == FxRateFreshness.Expired ? null : rate);

        public Task<FxRateReading?> GetReadingAsync(Market market, CancellationToken ct = default) =>
            Task.FromResult(
                rate is not { } r || r <= 0m
                    ? null
                    : new FxRateReading(
                        new FxRate(MarketCurrency.Of(market), MarketCurrency.Base, r,
                            freshness == FxRateFreshness.Expired
                                ? new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)
                                : new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero)),
                        freshness));
    }

    /// <summary>発行された TradeDecisionHeld を記録する偽物（<b>本スイートの観測点</b>）。</summary>
    private sealed class RecordingHeldReporter : IDecisionHeldReporter
    {
        public List<TradeDecisionHeld> Reports { get; } = [];

        public Task ReportAsync(TradeDecisionHeld held, CancellationToken cancellationToken = default)
        {
            Reports.Add(held);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHeldReporter : IDecisionHeldReporter
    {
        public int Calls { get; private set; }

        public Task ReportAsync(TradeDecisionHeld held, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new InvalidOperationException("発行先が壊れている");
        }
    }

    private sealed class RecordingSkipReporter : IDecisionSkipReporter
    {
        public List<DecisionSkipReason> Reasons { get; } = [];

        public void Report(string trigger, DecisionSkipReason reason) => Reasons.Add(reason);
    }

    private static SizingContext Context(
        decimal stageRemaining = 50_000m, decimal dailyRemaining = 20_000m, decimal capital = 100_000m) =>
        new(capital, stageRemaining, dailyRemaining, 0, 0m,
            BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits());

    private static AppSvc Create(
        IDecisionHeldReporter heldReporter,
        ILlmCompletionClient llm,
        RecordingSkipReporter? skipReporter = null,
        bool withoutPolicy = false,
        SizingContext? ctx = null,
        ICurrentPriceProvider? currentPrice = null,
        IFxRateProvider? fxRate = null,
        IHeldPositionProvider? held = null,
        DecisionOrchestrationOptions? options = null,
        ProfitabilityGateOptions? profitabilityOptions = null) =>
        new(llm, new FakePolicy(withoutPolicy ? null : Policy), new FakeSizing(ctx ?? Context()),
            new FakeClock(), NullLogger<AppSvc>.Instance,
            options: options, profitabilityOptions: profitabilityOptions, currentPrice: currentPrice, fxRate: fxRate,
            heldPosition: held, skipReporter: skipReporter, heldReporter: heldReporter);

    private static AppSvc Create(IDecisionHeldReporter heldReporter, string llmOutput, RecordingSkipReporter? skipReporter = null,
        bool withoutPolicy = false, SizingContext? ctx = null, ICurrentPriceProvider? currentPrice = null,
        IFxRateProvider? fxRate = null, IHeldPositionProvider? held = null,
        ProfitabilityGateOptions? profitabilityOptions = null) =>
        Create(heldReporter, new SequenceLlm(llmOutput), skipReporter, withoutPolicy, ctx, currentPrice, fxRate, held,
            profitabilityOptions: profitabilityOptions);

    // 基準通貨（米国株）の価格変動トリガー。起点の価格は 1,040。
    private static DecisionTrigger MovementTrigger() =>
        DecisionTrigger.FromPriceMovement(
            new PriceMovementDetected(Guid.NewGuid(), "AAPL", Market.UnitedStates, 1_040m, 1_000m, 0.04m, Now));

    // 非基準通貨（日本株）の価格変動トリガー（鮮度切れの経路を踏むため）。
    private static DecisionTrigger NonBaseTrigger() =>
        DecisionTrigger.FromPriceMovement(
            new PriceMovementDetected(Guid.NewGuid(), "7203", Market.Japan, 315_000m, 300_000m, 0.05m, Now));

    private static DecisionTrigger ScheduledTrigger() =>
        DecisionTrigger.Scheduled("AAPL", Market.UnitedStates, Now);

    // 🔴 本件の中心（#1077 の症状）: **LLM が Hold と結論したら、判断時点の価格で TradeDecisionHeld を 1 件出す。**
    [Fact]
    public async Task LLMがHoldと判断したら判断時点の価格でTradeDecisionHeldを発行する()
    {
        var held = new RecordingHeldReporter();
        var skips = new RecordingSkipReporter();
        var service = Create(held, HoldJson, skips);

        var decision = await service.DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);

        decision.Should().BeNull("Hold は従来どおり見送りである（発注意図を作らない）");
        skips.Reasons.Should().Equal([DecisionSkipReason.LlmHold], "見送りの計上は従来どおり 1 件である");
        var report = held.Reports.Should().ContainSingle().Subject;
        report.Symbol.Should().Be("AAPL");
        report.Market.Should().Be(Market.UnitedStates);
        report.Price.Should().Be(1_040m, "現在値が無ければ起点（価格変動）の価格が判断時点の価格である");
        report.Reason.Should().Be(nameof(DecisionSkipReason.LlmHold));
        report.DecidedAt.Should().Be(Now);
        report.CycleTrigger.Should().Be(BusinessMetrics.TriggerPriceMovement);
    }

    // IADR-0452 決定3: 権威ある現在値（有効時）を起点の価格より優先する（TradeDecisionMade の参照価格と同じ向き）。
    [Fact]
    public async Task 現在値が有効なら現在値を判断時点の価格にする()
    {
        var held = new RecordingHeldReporter();
        var service = Create(held, HoldJson, currentPrice: new FakeCurrentPrice(1_055m));

        await service.DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);

        held.Reports.Should().ContainSingle().Which.Price.Should().Be(1_055m);
    }

    // 🔴 稼働 PoC の形（定時判断・現在値あり・全件 Hold）。定時の Hold でも基準値が作られる（銘柄追加直後の初期基準値もこれで作られる）。
    [Fact]
    public async Task 定時判断のHoldでも現在値があれば発行する()
    {
        var held = new RecordingHeldReporter();
        var service = Create(held, HoldJson, currentPrice: new FakeCurrentPrice(212.5m));

        await service.DecideAsync(ScheduledTrigger(), TestContext.Current.CancellationToken);

        var report = held.Reports.Should().ContainSingle().Subject;
        report.Price.Should().Be(212.5m);
        report.CycleTrigger.Should().Be(BusinessMetrics.TriggerScheduled);
    }

    // 残余（IADR-0452 §結果）: 現在値の供給が無効な構成の定時の Hold は判断時点の価格を持たない。価格を作らず発行しない。
    [Fact]
    public async Task 判断時点の価格が手元に無ければ発行しない()
    {
        var held = new RecordingHeldReporter();
        var skips = new RecordingSkipReporter();
        var service = Create(held, HoldJson, skips);

        var decision = await service.DecideAsync(ScheduledTrigger(), TestContext.Current.CancellationToken);

        decision.Should().BeNull();
        skips.Reasons.Should().Equal([DecisionSkipReason.LlmHold]);
        held.Reports.Should().BeEmpty("0 や推測の価格で基準値を動かさない");
    }

    // 🔴 否定形: **判断をしなかった見送り**（LLM を呼ぶ前の 4 地点）では出さない（計画の「AI 判断を行った時点」）。
    [Fact]
    public async Task 判断をしなかった見送りでは発行しない()
    {
        var cases = new (string Name, Func<RecordingHeldReporter, RecordingSkipReporter, AppSvc> Build, DecisionTrigger Trigger, DecisionSkipReason Reason)[]
        {
            ("日報未確定", (h, s) => Create(h, BuyJson, s, withoutPolicy: true), MovementTrigger(),
                DecisionSkipReason.DailyPolicyUnconfirmed),
            ("現在値なし", (h, s) => Create(h, BuyJson, s, currentPrice: new FakeCurrentPrice(null)), MovementTrigger(),
                DecisionSkipReason.CurrentPriceUnavailable),
            ("換算レート未解決", (h, s) => Create(h, NonBaseBuyJson, s, fxRate: new FakeFxRate(null)), NonBaseTrigger(),
                DecisionSkipReason.FxRateUnresolved),
            ("鮮度切れで保有なし", (h, s) => Create(h, NonBaseBuyJson, s, fxRate: new FakeFxRate(0.01m, FxRateFreshness.Expired),
                held: new FakeHeld(0)), NonBaseTrigger(), DecisionSkipReason.FxRateStaleNoHolding),
        };

        foreach (var c in cases)
        {
            var held = new RecordingHeldReporter();
            var skips = new RecordingSkipReporter();

            var decision = await c.Build(held, skips).DecideAsync(c.Trigger, TestContext.Current.CancellationToken);

            decision.Should().BeNull(c.Name);
            skips.Reasons.Should().Equal([c.Reason], c.Name);
            held.Reports.Should().BeEmpty($"{c.Name}（{c.Reason}）は AI 判断をしていない");
        }
    }

    // 🔴 LLM が Buy/Sell と結論したが統制が見送らせた場合も、AI 判断は行われたので出す（理由を載せる）。
    [Fact]
    public async Task 判断後に統制で見送った場合も理由つきで発行する()
    {
        var cases = new (Func<RecordingHeldReporter, RecordingSkipReporter, AppSvc> Build, DecisionTrigger Trigger, DecisionSkipReason Reason, decimal Price)[]
        {
            ((h, s) => Create(h, BuyJson, s, held: new FakeHeld(null)), MovementTrigger(),
                DecisionSkipReason.HoldingsUnknownOpen, 1_040m),
            ((h, s) => Create(h, SellJson, s, held: new FakeHeld(0)), MovementTrigger(),
                DecisionSkipReason.NakedShortOpen, 1_040m),
            ((h, s) => Create(h, NonBaseBuyJson, s, fxRate: new FakeFxRate(0.01m, FxRateFreshness.Expired),
                held: new FakeHeld(10)), NonBaseTrigger(), DecisionSkipReason.FxRateStaleOpen, 315_000m),
            ((h, s) => Create(h, BuyJson, s, ctx: Context(stageRemaining: 0m, dailyRemaining: 0m), held: new FakeHeld(10)),
                MovementTrigger(), DecisionSkipReason.SizingZeroQuantity, 1_040m),
            ((h, s) => Create(h, BuyJson, s, held: new FakeHeld(0, workingUnknown: true)), MovementTrigger(),
                DecisionSkipReason.WorkingEntriesUnknownOpen, 1_040m),
            // T-10-2312, #1176, IADR-0495 決定1: サイジングの名目額（1 株 × 1,000）が最小（equity 110,000 の 1%＝1,100）に満たない。
            ((h, s) => Create(h, BuyJson, s, ctx: Context(stageRemaining: 1_999m, dailyRemaining: 1_999m, capital: 110_000m),
                    held: new FakeHeld(0)),
                MovementTrigger(), DecisionSkipReason.SizedBelowMinimumNotional, 1_040m),
            // PR #1080 監査（生存変異 M2）: 残る判断後の 3 地点。
            // 現在値が 0（正でない）→ 参照価格が不正。判断時点の価格は正の候補（起点の価格）へ進む。
            ((h, s) => Create(h, BuyJson, s, currentPrice: new FakeCurrentPrice(0m), held: new FakeHeld(0)),
                MovementTrigger(), DecisionSkipReason.ReferencePriceInvalid, 1_040m),
            // 現在値 20 に対して LLM の損切り幅 30 ≧ 参照価格 → 損切り幅が不正。判断時点の価格は現在値。
            ((h, s) => Create(h, BuyJson, s, currentPrice: new FakeCurrentPrice(20m), held: new FakeHeld(0)),
                MovementTrigger(), DecisionSkipReason.StopLossDistanceInvalid, 20m),
            // 採算ゲート有効・費用見積り不能（既定の NoOp は常に未解決）→ 採算不成立で見送り。
            ((h, s) => Create(h, BuyJson, s, held: new FakeHeld(0),
                    profitabilityOptions: ProfitabilityGateOptions.Default with { Enabled = true }),
                MovementTrigger(), DecisionSkipReason.ProfitabilityNotViable, 1_040m),
            // 起点の価格も現在値も無い定時判断では、Buy/Sell の結論が出した参照価格（正）を使う（TradeDecisionMade と同じ源）。
            ((h, s) => Create(h, BuyJson, s, ctx: Context(stageRemaining: 0m, dailyRemaining: 0m), held: new FakeHeld(10)),
                ScheduledTrigger(), DecisionSkipReason.SizingZeroQuantity, 1_000m),
        };

        foreach (var c in cases)
        {
            var held = new RecordingHeldReporter();
            var skips = new RecordingSkipReporter();

            var decision = await c.Build(held, skips).DecideAsync(c.Trigger, TestContext.Current.CancellationToken);

            decision.Should().BeNull(c.Reason.ToString());
            skips.Reasons.Should().Equal([c.Reason], "見送りの理由は従来どおりである");
            var report = held.Reports.Should().ContainSingle(c.Reason.ToString()).Subject;
            report.Reason.Should().Be(c.Reason.ToString());
            report.Price.Should().Be(c.Price, c.Reason.ToString());
        }
    }

    // 判断後の 10 地点と判断前の 5 地点で語彙 15 値を過不足なく覆う（上の 2 表・LlmHold の試験が各地点を振る舞いで固定する。
    // 判断前の 5 地点目〔#1113 の EntryBlockedByRiskControls〕は EntryBlockersBeforeLlmTests が、判断後の 10 地点目
    // 〔T-10-1907, #1130 の AddOnBlockedByRiskControls〕は HeldAddOnBlockersTests が固定する）。
    [Fact]
    public void 判断前と判断後の見送りは語彙17値を過不足なく覆う()
    {
        // T-10-2322, #1176, IADR-0495: 判断前に EntryCapacityBelowMinimumNotional、判断後に SizedBelowMinimumNotional を足した（15 → 17）。
        DecisionSkipReason[] before =
        [
            DecisionSkipReason.DailyPolicyUnconfirmed, DecisionSkipReason.CurrentPriceUnavailable,
            DecisionSkipReason.FxRateUnresolved, DecisionSkipReason.FxRateStaleNoHolding,
            DecisionSkipReason.EntryBlockedByRiskControls, DecisionSkipReason.EntryCapacityBelowMinimumNotional,
        ];
        DecisionSkipReason[] after =
        [
            DecisionSkipReason.LlmHold, DecisionSkipReason.HoldingsUnknownOpen, DecisionSkipReason.NakedShortOpen,
            DecisionSkipReason.FxRateStaleOpen, DecisionSkipReason.SizingZeroQuantity,
            DecisionSkipReason.WorkingEntriesUnknownOpen, DecisionSkipReason.ReferencePriceInvalid,
            DecisionSkipReason.StopLossDistanceInvalid, DecisionSkipReason.ProfitabilityNotViable,
            DecisionSkipReason.AddOnBlockedByRiskControls, DecisionSkipReason.SizedBelowMinimumNotional,
        ];

        before.Concat(after).Should().BeEquivalentTo(Enum.GetValues<DecisionSkipReason>());
    }

    // 🔴 IADR-0248: **解析不能は結論ではない**（見送りと区別する）。単発・一次スクリーニングとも出さない。
    [Fact]
    public async Task 出力を解析できなかった場合は発行しない()
    {
        var single = new RecordingHeldReporter();
        await Create(single, Garbage).DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);
        single.Reports.Should().BeEmpty("二次の全票が解析不能");

        var screened = new RecordingHeldReporter();
        await Create(screened, new SequenceLlm(Garbage), options: new DecisionOrchestrationOptions { EnableScreening = true })
            .DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);
        screened.Reports.Should().BeEmpty("一次スクリーニングが解析不能");

        var allVotes = new RecordingHeldReporter();
        await Create(allVotes, new SequenceLlm(Garbage, Garbage, Garbage), options: new DecisionOrchestrationOptions { VoteCount = 3 })
            .DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);
        allVotes.Reports.Should().BeEmpty("3 票すべてが解析不能");
    }

    // 対の肯定形: 一次スクリーニングの Hold・一部の票だけ解析不能の多数決 Hold は結論である（出す）。
    [Fact]
    public async Task 一次スクリーニングのHoldと一部解析不能の多数決Holdは発行する()
    {
        var screened = new RecordingHeldReporter();
        await Create(screened, new SequenceLlm(HoldJson), options: new DecisionOrchestrationOptions { EnableScreening = true })
            .DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);
        screened.Reports.Should().ContainSingle().Which.Reason.Should().Be(nameof(DecisionSkipReason.LlmHold));

        var partial = new RecordingHeldReporter();
        await Create(partial, new SequenceLlm(Garbage, Garbage, HoldJson), options: new DecisionOrchestrationOptions { VoteCount = 3 })
            .DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);
        partial.Reports.Should().ContainSingle("解析できた票が 1 票ある");
    }

    // 🔴 否定形: **判断が成立（TradeDecisionMade）したときは出さない**（基準値は TradeDecisionMade の経路が進める・既存経路は不変）。
    [Fact]
    public async Task 判断が成立したときは発行しない()
    {
        var held = new RecordingHeldReporter();
        var service = Create(held, BuyJson, held: new FakeHeld(0));

        var decision = await service.DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);

        decision.Should().NotBeNull();
        decision!.Intent.Price.Should().Be(1_000m, "Buy/Sell の経路の参照価格は従来どおり（基準値はこの価格で進む）");
        held.Reports.Should().BeEmpty();
    }

    private sealed class DelegateHeldReporter(Func<CancellationToken, Task> onReport) : IDecisionHeldReporter
    {
        public Task ReportAsync(TradeDecisionHeld held, CancellationToken cancellationToken = default) =>
            onReport(cancellationToken);
    }

    // 🔴 PR #1080 監査（生存変異 M6）, IADR-0452 決定4: **本判断のキャンセルは伝える**（握って見送りにしない）。
    [Fact]
    public async Task 本判断のキャンセルは発行から伝播する()
    {
        using var cts = new CancellationTokenSource();
        var skips = new RecordingSkipReporter();
        var service = Create(
            new DelegateHeldReporter(ct =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }),
            new SequenceLlm(HoldJson), skips);

        var act = async () => await service.DecideAsync(MovementTrigger(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        skips.Reasons.Should().BeEmpty("キャンセルされた判断は見送りとして数えない");
    }

    // 🔴 PR #1080 監査（生存変異 V3）: **本判断のトークンが取り消された後でも、キャンセル以外の失敗は握って見送りを計上する。**
    // 伝えるのは「キャンセルされた」ことだけであり、キャンセル後に起きた別の失敗（発行先の故障）まで伝えると、
    // 見送りが「判断の失敗」へ化ける（握り条件をトークンだけで書く形 `when (!ct.IsCancellationRequested)` を赤にする）。
    [Fact]
    public async Task 本判断の取り消し後でもキャンセル以外の失敗は握って見送りを計上する()
    {
        using var cts = new CancellationTokenSource();
        var skips = new RecordingSkipReporter();
        var service = Create(
            new DelegateHeldReporter(_ =>
            {
                cts.Cancel();
                throw new InvalidOperationException("発行先が壊れている");
            }),
            new SequenceLlm(HoldJson), skips);

        var act = async () => await service.DecideAsync(MovementTrigger(), cts.Token);

        (await act.Should().NotThrowAsync()).Subject.Should().BeNull();
        skips.Reasons.Should().Equal([DecisionSkipReason.LlmHold]);
    }

    // 🔴 対: **本判断と無関係な打ち切り（発行先の内部の TaskCanceledException）は握り、見送りを計上する。**
    // 例外の型で伝播を決めると、ここで見送りが「判断の失敗」へ化ける。
    [Fact]
    public async Task 本判断と無関係な打ち切りは握って見送りを計上する()
    {
        var skips = new RecordingSkipReporter();
        var service = Create(
            new DelegateHeldReporter(_ => throw new TaskCanceledException("発行先の内部の打ち切り")),
            new SequenceLlm(HoldJson), skips);

        var act = async () => await service.DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);

        (await act.Should().NotThrowAsync()).Subject.Should().BeNull();
        skips.Reasons.Should().Equal([DecisionSkipReason.LlmHold]);
    }

    // 🔴 発行の失敗で見送りを壊さない（兄弟ポートの ...SafeAsync と同じ規律）。発行は試みられ、計上も 1 件のまま。
    [Fact]
    public async Task 発行が例外を投げても見送りはそのまま返る()
    {
        var held = new ThrowingHeldReporter();
        var skips = new RecordingSkipReporter();
        var service = Create(held, HoldJson, skips);

        var act = async () => await service.DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);

        (await act.Should().NotThrowAsync()).Subject.Should().BeNull();
        held.Calls.Should().Be(1);
        skips.Reasons.Should().Equal([DecisionSkipReason.LlmHold]);
    }
}
