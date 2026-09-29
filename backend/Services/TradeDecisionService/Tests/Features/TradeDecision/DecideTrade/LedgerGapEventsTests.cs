extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AiStockTrading.Shared.Infrastructure.Composable.Adapters.Fx;
using AiStockTrading.Shared.Infrastructure.Composable.Observability;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// 🔴 NFR, FR-04, FR-10, FR-11, #1092, IADR-0462 決定2・決定4: 取引判断の側で、ログとメトリクスにしか残らなかった 2 つを台帳へ出す。
//   - LLM を呼ぶ前の見送り（4 地点）: 1 回につき 1 件の TradeDecisionForgoneBeforeLlm（TradeDecisionHeld は流用しない）。
//   - 保有照会・未約定の照会の成否: 発生源を分けて報告する（状態が変わったときだけ出るのは報告口の側）。
// 🔴 **見送るかどうかの判定は変えない**。どのケースも従来どおり null（見送り）で、見送りの理由の計上も 1 件のままである。
public class LedgerGapEventsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 14, 0, 0, TimeSpan.Zero);
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 9, 30), "様子見の方針");

    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";
    private const string HoldJson = """{"action":"Hold","rationale":"様子見"}""";
    private const string NonBaseBuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":300000,"stopLossDistancePerShare":7500}""";

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class FixedLlm(string output) : ILlmCompletionClient
    {
        public int Calls { get; private set; }

        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(output);
        }
    }

    private sealed class FakePolicy(DailyPolicy? policy) : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult(policy);
    }

    private sealed class FakeSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new SizingContext(100_000m, 50_000m, 20_000m, 0, 0m,
                BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits()));
    }

    // 保有照会。held / working が null なら不明。throws なら例外。enabled=false は未結線（NoOp 相当）。
    private sealed class FakeHeld(int? held, bool workingUnknown = false, bool throws = false, bool enabled = true)
        : IHeldPositionProvider
    {
        public bool IsEnabled => enabled;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            throws ? throw new InvalidOperationException("リスク管理に届かない") : Task.FromResult(held);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            throws
                ? throw new InvalidOperationException("リスク管理に届かない")
                : Task.FromResult(held switch
                {
                    null => null,
                    0 => HeldPosition.None,
                    { } q => new HeldPosition(q, 1_000m, 970m),
                });

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
            string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult(workingUnknown ? null : WorkingEntryOrders.None);
    }

    // 照会の最中に判断が止められた（呼び出し側がトークンを取り消した）ことを模す: 指定の照会で CTS を取り消してから
    // OperationCanceledException を投げる。他の照会は成功（保有なし・未約定なし・決済の数量 10）を返す。
    private sealed class CancellingHeld(string cancelAt, CancellationTokenSource cts) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public List<string> Calls { get; } = [];

        private Exception Cancel(string name)
        {
            Calls.Add(name);
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        }

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            cancelAt == "決済の数量" ? throw Cancel("決済の数量") : Task.FromResult<int?>(10);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            cancelAt == "保有状況" ? throw Cancel("保有状況") : Task.FromResult<HeldPosition?>(new HeldPosition(10, 1_000m, 970m));

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
            string symbol, Market market, CancellationToken ct = default) =>
            cancelAt == "未約定" ? throw Cancel("未約定") : Task.FromResult<WorkingEntryOrders?>(WorkingEntryOrders.None);
    }

    private sealed class FakeCurrentPrice(decimal? price) : ICurrentPriceProvider
    {
        public bool IsEnabled => true;

        public Task<CurrentPriceReading?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken ct = default) =>
            Task.FromResult(price is { } p ? new CurrentPriceReading(p, IntradayPriceContext.Unknown) : null);
    }

    private sealed class FakeFxRate(decimal? rate, FxRateFreshness freshness = FxRateFreshness.Fresh) : IFxRateProvider
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
                                : new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero)),
                        freshness));
    }

    private sealed class RecordingForgone : IDecisionForgoneBeforeLlmReporter
    {
        public List<TradeDecisionForgoneBeforeLlm> Reports { get; } = [];

        public bool Throw { get; init; }

        /// <summary>発行口が投げる例外を作る（null なら <see cref="Throw"/> に従う）。呼ばれた時点のトークンを受け取る。</summary>
        public Func<CancellationToken, Exception>? Throws { get; init; }

        public Task ReportAsync(TradeDecisionForgoneBeforeLlm forgone, CancellationToken cancellationToken = default)
        {
            Reports.Add(forgone);
            if (Throws is not null)
                throw Throws(cancellationToken);
            return Throw ? throw new InvalidOperationException("発行先が壊れている") : Task.CompletedTask;
        }
    }

    private sealed class RecordingHeldReporter : IDecisionHeldReporter
    {
        public List<TradeDecisionHeld> Reports { get; } = [];

        public Task ReportAsync(TradeDecisionHeld held, CancellationToken cancellationToken = default)
        {
            Reports.Add(held);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSkips : IDecisionSkipReporter
    {
        public List<DecisionSkipReason> Reasons { get; } = [];

        public void Report(string trigger, DecisionSkipReason reason) => Reasons.Add(reason);
    }

    private sealed class RecordingHealth : IPositionQueryHealthReporter
    {
        public List<(PositionQuerySource Source, bool Succeeded)> Reports { get; } = [];

        public Task ReportAsync(PositionQuerySource source, bool succeeded, string? failureKind = null)
        {
            Reports.Add((source, succeeded));
            return Task.CompletedTask;
        }
    }

    private sealed record Probe(
        AppSvc Service, FixedLlm Llm, RecordingForgone Forgone, RecordingHeldReporter Held, RecordingSkips Skips,
        RecordingHealth Health);

    private static Probe Create(
        string llmOutput = BuyJson, bool withoutPolicy = false, ICurrentPriceProvider? currentPrice = null,
        IFxRateProvider? fxRate = null, IHeldPositionProvider? held = null, RecordingForgone? forgone = null)
    {
        var llm = new FixedLlm(llmOutput);
        var f = forgone ?? new RecordingForgone();
        var h = new RecordingHeldReporter();
        var s = new RecordingSkips();
        var health = new RecordingHealth();
        var service = new AppSvc(
            llm, new FakePolicy(withoutPolicy ? null : Policy), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            currentPrice: currentPrice, fxRate: fxRate, heldPosition: held, skipReporter: s, heldReporter: h,
            forgoneReporter: f, positionQueryHealth: health);
        return new Probe(service, llm, f, h, s, health);
    }

    private static DecisionTrigger MovementTrigger() =>
        DecisionTrigger.FromPriceMovement(
            new PriceMovementDetected(Guid.NewGuid(), "AAPL", Market.UnitedStates, 1_040m, 1_000m, 0.04m, Now));

    private static DecisionTrigger NonBaseTrigger() =>
        DecisionTrigger.Scheduled("7203", Market.Japan, Now);

    public static TheoryData<string> BeforeLlmCases() =>
        ["日報未確定", "現在値なし", "換算レート未解決", "鮮度切れで保有なし"];

    private static (Probe Probe, DecisionTrigger Trigger, DecisionForgoneBeforeLlmReason Reason) BeforeLlm(string name) => name switch
    {
        "日報未確定" => (Create(withoutPolicy: true), MovementTrigger(), DecisionForgoneBeforeLlmReason.DailyPolicyUnconfirmed),
        "現在値なし" => (Create(currentPrice: new FakeCurrentPrice(null)), MovementTrigger(),
            DecisionForgoneBeforeLlmReason.CurrentPriceUnavailable),
        "換算レート未解決" => (Create(NonBaseBuyJson, fxRate: new FakeFxRate(null)), NonBaseTrigger(),
            DecisionForgoneBeforeLlmReason.FxRateUnresolved),
        _ => (Create(NonBaseBuyJson, fxRate: new FakeFxRate(0.01m, FxRateFreshness.Expired), held: new FakeHeld(0)),
            NonBaseTrigger(), DecisionForgoneBeforeLlmReason.FxRateStaleNoHolding),
    };

    // ---- T-10-1772: LLM を呼ぶ前の 4 地点で 1 件ずつ出す ----
    [Theory]
    [MemberData(nameof(BeforeLlmCases))]
    public async Task T_10_1772_LLMを呼ぶ前の見送りは理由つきで1件だけ台帳へ出しTradeDecisionHeldは出さない(string name)
    {
        var (probe, trigger, reason) = BeforeLlm(name);

        var decision = await probe.Service.DecideAsync(trigger, TestContext.Current.CancellationToken);

        decision.Should().BeNull(name);
        probe.Llm.Calls.Should().Be(0, $"{name}: 前提として LLM を呼んでいない");
        var e = probe.Forgone.Reports.Should().ContainSingle(name).Subject;
        e.Reason.Should().Be(reason);
        e.Symbol.Should().Be(trigger.Symbol);
        e.Market.Should().Be(trigger.Market);
        e.OccurredAt.Should().Be(Now);
        e.CycleTrigger.Should().Be(trigger.MetricTrigger);
        e.EventId.Should().NotBe(Guid.Empty);
        probe.Skips.Reasons.Should().Equal([AppSvc.ToSkipReason(reason)], "見送りの計上は従来どおり 1 件");
        probe.Held.Reports.Should().BeEmpty("判断をしていない見送りで急変の基準値を進めない（IADR-0452 決定1）");
    }

    // T-10-1772（否定形）: LLM を呼んだ後の見送りでは出さない（そちらは TradeDecisionHeld が台帳へ出す）。
    [Fact]
    public async Task T_10_1772_LLMを呼んだ後の見送りでは出さない()
    {
        var probe = Create(HoldJson, currentPrice: new FakeCurrentPrice(1_050m), held: new FakeHeld(0));

        await probe.Service.DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);

        probe.Llm.Calls.Should().BeGreaterThan(0);
        probe.Forgone.Reports.Should().BeEmpty();
        probe.Held.Reports.Should().ContainSingle();
        probe.Skips.Reasons.Should().Equal([DecisionSkipReason.LlmHold]);
    }

    // T-10-1772: 発行の失敗で見送りを壊さない（計上も 1 件のまま）。
    [Fact]
    public async Task T_10_1772_発行に失敗しても見送りと計上は変わらない()
    {
        var probe = Create(withoutPolicy: true, forgone: new RecordingForgone { Throw = true });

        var decision = await probe.Service.DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);

        decision.Should().BeNull();
        probe.Forgone.Reports.Should().ContainSingle("前提: 発行を試みている");
        probe.Skips.Reasons.Should().Equal([DecisionSkipReason.DailyPolicyUnconfirmed]);
    }

    // T-10-1772: PR #1110 の監査 N3。発行口が**本判断のキャンセルでない** OperationCanceledException（発行口自身の打ち切り。
    // 呼び出し側のトークンは生きている）を投げても、見送りは壊さない（発行の失敗の 1 つとして飲む）。
    // 🔴 伝えるのは本判断のキャンセル（トークンが取り消された）ときだけ（SkipJudgedAsync と同じ規律）。種類だけで伝えると、
    // 発行口の遅延だけで見送りの計上が落ち、定時の巡回では銘柄ごとの catch へ、価格変動では再試行へ流れる。
    [Fact]
    public async Task T_10_1772_発行口自身の打ち切りでは見送りを壊さず_本判断のキャンセルだけを伝える()
    {
        var own = Create(withoutPolicy: true, forgone: new RecordingForgone
        {
            Throws = _ => new OperationCanceledException("発行口の打ち切り（呼び出し側は止めていない）"),
        });

        var decision = await own.Service.DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);

        decision.Should().BeNull("発行口の打ち切りは発行の失敗であり、見送りは従来どおり");
        own.Forgone.Reports.Should().ContainSingle("前提: 発行を試みている");
        own.Skips.Reasons.Should().Equal([DecisionSkipReason.DailyPolicyUnconfirmed], "見送りの計上は 1 件のまま");

        // 本判断のキャンセル: 発行の最中にトークンが取り消された → キャンセルを伝える（見送りとして計上しない）。
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cancelled = Create(withoutPolicy: true, forgone: new RecordingForgone
        {
            Throws = ct =>
            {
                cts.Cancel();
                return new OperationCanceledException(ct);
            },
        });

        var act = () => cancelled.Service.DecideAsync(MovementTrigger(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>("本判断のキャンセルは伝える");
        cancelled.Forgone.Reports.Should().ContainSingle("前提: 発行の最中に取り消された");
        cancelled.Skips.Reasons.Should().BeEmpty("取り消された判断を見送りとして数えない");
    }

    // T-10-1772: 台帳の語彙（4 値）は観測の語彙（DecisionSkipReason）と同じ名前で、写像は名前どおりである。
    [Fact]
    public void T_10_1772_台帳の理由は観測の理由と同じ名前で写る()
    {
        foreach (var reason in Enum.GetValues<DecisionForgoneBeforeLlmReason>())
        {
            AppSvc.ToSkipReason(reason).ToString().Should().Be(reason.ToString());
        }

        Enum.GetValues<DecisionForgoneBeforeLlmReason>().Should().HaveCount(4, "LLM より前の見送りは 4 地点（仕様書の母集合）");
    }

    // ---- T-10-1771: 保有照会・未約定の照会の成否を発生源を分けて報告する ----
    [Fact]
    public async Task T_10_1771_保有照会と未約定の照会が成功なら発生源を分けて成功を報告する()
    {
        var probe = Create(HoldJson, held: new FakeHeld(0));

        await probe.Service.DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);

        probe.Health.Reports.Should().Equal(
            (PositionQuerySource.TradeDecisionHoldings, true), (PositionQuerySource.TradeDecisionWorkingEntries, true));
    }

    [Theory]
    [InlineData("保有が不明", false, true)]
    [InlineData("保有の照会が例外", false, true)]
    [InlineData("未約定が不明", true, false)]
    public async Task T_10_1771_照会が不明や例外なら失敗を報告する(string name, bool holdingsOk, bool workingOk)
    {
        var held = name switch
        {
            "保有が不明" => new FakeHeld(null),
            "保有の照会が例外" => new FakeHeld(0, throws: true),
            _ => new FakeHeld(0, workingUnknown: true),
        };
        var probe = Create(HoldJson, held: held);

        await probe.Service.DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);

        probe.Health.Reports.Should().Equal(
            [(PositionQuerySource.TradeDecisionHoldings, holdingsOk), (PositionQuerySource.TradeDecisionWorkingEntries, workingOk)],
            name);
    }

    // T-10-1771: 決済の数量の引き直し（LLM の後の保有照会）も同じ発生源で報告する。
    [Fact]
    public async Task T_10_1771_決済の数量の引き直しも保有照会として報告する()
    {
        var probe = Create(
            """{"action":"Sell","rationale":"利確","referencePrice":1000,"stopLossDistancePerShare":30}""",
            held: new FakeHeld(10));

        await probe.Service.DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);

        probe.Health.Reports.Should().Equal(
            (PositionQuerySource.TradeDecisionHoldings, true), (PositionQuerySource.TradeDecisionWorkingEntries, true),
            (PositionQuerySource.TradeDecisionHoldings, true));
    }

    // T-10-1771: PR #1110 の監査 N2。照会の最中に判断が止められた（キャンセル）ときは、照会の**失敗として数えない**
    // （報告しない）。キャンセルは照会先の不調ではない。失敗と数えると、停止・再配備のたびに Failing が台帳へ出て、
    // 夜間の要約に実在しない照会の失敗の区間が現れる。キャンセルはそのまま伝える（fail-safe の「不明」へ縮退しない）。
    [Theory]
    [InlineData("保有状況")]
    [InlineData("未約定")]
    [InlineData("決済の数量")]
    public async Task T_10_1771_照会の最中のキャンセルは失敗として報告せず_キャンセルを伝える(string cancelAt)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var held = new CancellingHeld(cancelAt, cts);
        // 決済の数量の引き直しは LLM が Sell を返した後の照会である。
        var probe = Create(
            """{"action":"Sell","rationale":"利確","referencePrice":1000,"stopLossDistancePerShare":30}""", held: held);

        var act = () => probe.Service.DecideAsync(MovementTrigger(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(cancelAt);
        held.Calls.Should().Equal([cancelAt], "前提: その照会の最中に取り消された");
        probe.Health.Reports.Should().NotContain(r => !r.Succeeded, $"{cancelAt}: キャンセルを照会の失敗として数えない");
    }

    // T-10-1771（否定形）: 未結線（NoOp＝常に不明）は照会していないので、失敗として報告しない。
    [Fact]
    public async Task T_10_1771_未結線の保有照会は報告しない()
    {
        var probe = Create(HoldJson, held: new FakeHeld(null, workingUnknown: true, enabled: false));

        await probe.Service.DecideAsync(MovementTrigger(), TestContext.Current.CancellationToken);

        probe.Health.Reports.Should().BeEmpty();
    }
}
