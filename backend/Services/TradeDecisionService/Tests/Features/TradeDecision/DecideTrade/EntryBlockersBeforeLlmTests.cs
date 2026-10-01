extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// 🔴 FR-10, FR-04, ADR-0003, #1113, IADR-0463 決定 1・4: 新規建てが審査で必ず拒否される銘柄は、LLM を呼ぶ前に見送る。
// 省けるのは「保有が既知で 0、かつ未約定の新規建てが既知で空」で、リスク管理の可否の口が**買いの新規建て**を塞いでいると
// 答えたときだけである。保有中・未約定あり・不明・照会の失敗・未結線・売りだけ塞がり、では LLM を呼ぶ（決済の判断は必ず残す）。
public class EntryBlockersBeforeLlmTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 14, 0, 0, TimeSpan.Zero);
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 9, 30), "様子見の方針");

    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";
    private const string SellJson =
        """{"action":"Sell","rationale":"利確","referencePrice":1000,"stopLossDistancePerShare":30}""";

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

    private sealed class FakePolicy : IDailyPolicyProvider
    {
        public Task<DailyPolicy?> GetCurrentAsync(CancellationToken ct = default) => Task.FromResult<DailyPolicy?>(Policy);
    }

    private sealed class FakeSizing : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new SizingContext(100_000m, 50_000m, 20_000m, 0, 0m,
                BrokerProvider.InternalPaper, TradingDefaults.CreateRiskLimits()));
    }

    // 保有照会（実結線）。held が null なら不明。working は null なら不明、true なら未約定あり。
    private sealed class FakeHeld(int? held, bool? working = false) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult(held);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult(held switch
            {
                null => null,
                0 => HeldPosition.None,
                { } q => new HeldPosition(q, 1_000m, q > 0 ? 970m : 1_030m),
            });

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
            string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult(working switch
            {
                null => null,
                true => new WorkingEntryOrders([new WorkingEntryOrder(TradeSide.Buy, 5, 1_000m, Now.AddMinutes(-1))]),
                false => WorkingEntryOrders.None,
            });
    }

    // 可否の口。answer が null なら不明（照会の失敗）、throws なら例外。
    private sealed class FakeBlockers(EntryBlockers? answer, bool throws = false) : IEntryBlockersProvider
    {
        public EntryBlockers? Answer { get; set; } = answer;

        public List<(string Symbol, Market Market)> Calls { get; } = [];

        public Task<EntryBlockers?> GetAsync(string symbol, Market market, CancellationToken cancellationToken = default)
        {
            Calls.Add((symbol, market));
            return throws ? throw new HttpRequestException("リスク管理に届かない") : Task.FromResult(Answer);
        }
    }

    private sealed class RecordingForgone : IDecisionForgoneBeforeLlmReporter
    {
        public List<TradeDecisionForgoneBeforeLlm> Reports { get; } = [];

        public Task ReportAsync(TradeDecisionForgoneBeforeLlm forgone, CancellationToken cancellationToken = default)
        {
            Reports.Add(forgone);
            return Task.CompletedTask;
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

    private sealed record Probe(
        AppSvc Service, FixedLlm Llm, RecordingForgone Forgone, RecordingHeldReporter Held, RecordingSkips Skips);

    private static Probe Create(IHeldPositionProvider held, IEntryBlockersProvider? blockers, string llmOutput = BuyJson)
    {
        var llm = new FixedLlm(llmOutput);
        var forgone = new RecordingForgone();
        var heldReporter = new RecordingHeldReporter();
        var skips = new RecordingSkips();
        var service = new AppSvc(
            llm, new FakePolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            heldPosition: held, skipReporter: skips, heldReporter: heldReporter, forgoneReporter: forgone,
            entryBlockers: blockers);
        return new Probe(service, llm, forgone, heldReporter, skips);
    }

    private static DecisionTrigger Trigger() => DecisionTrigger.Scheduled("AAPL", Market.UnitedStates, Now);

    private static EntryBlockers LongBlocked(RejectionReason reason) => new([reason], []);

    public static TheoryData<RejectionReason> Blockers() =>
    [
        RejectionReason.KillSwitchActive,
        RejectionReason.TradingPaused,
        RejectionReason.StoppedOutSameDay,
        RejectionReason.GoodFaithViolationLimitReached,
        RejectionReason.MaxPositionsExceeded,
        RejectionReason.DailyLossLimitReached,
        RejectionReason.MaxDrawdownReached,
    ];

    // T-10-1791: 保有 0・未約定なし・買いの新規建てが塞がっている → LLM を 0 回で見送り、台帳へ理由つきで 1 件、
    // TradeDecisionHeld は出さない（判断をしていない見送りで急変の基準値を進めない）。ブロッカーそれぞれで同じ。
    [Theory]
    [MemberData(nameof(Blockers))]
    public async Task T_10_1791_塞がっている銘柄はLLMを呼ばずに見送る(RejectionReason reason)
    {
        var blockers = new FakeBlockers(LongBlocked(reason));
        var probe = Create(new FakeHeld(0), blockers);

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision.Should().BeNull();
        probe.Llm.Calls.Should().Be(0, "LLM を呼ばない（#1113 の費用）");
        blockers.Calls.Should().Equal([("AAPL", Market.UnitedStates)], "判断対象の銘柄・市場で 1 回だけ照会する");
        var e = probe.Forgone.Reports.Should().ContainSingle().Subject;
        e.Reason.Should().Be(DecisionForgoneBeforeLlmReason.EntryBlockedByRiskControls);
        e.Symbol.Should().Be("AAPL");
        e.Market.Should().Be(Market.UnitedStates);
        e.CycleTrigger.Should().Be(Trigger().MetricTrigger);
        probe.Skips.Reasons.Should().Equal([DecisionSkipReason.EntryBlockedByRiskControls]);
        probe.Held.Reports.Should().BeEmpty("TradeDecisionHeld は出さない（IADR-0452 決定 1）");
    }

    // T-10-1791: 塞がりが解けた次の判断では LLM を呼ぶ（関門は状態を持たず、判断ごとに読み直す＝窓の表の減る側）。
    [Fact]
    public async Task T_10_1791_塞がりが解けた次の判断ではLLMを呼ぶ()
    {
        var blockers = new FakeBlockers(LongBlocked(RejectionReason.MaxPositionsExceeded));
        var probe = Create(new FakeHeld(0), blockers);

        (await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken)).Should().BeNull();
        probe.Llm.Calls.Should().Be(0);

        blockers.Answer = EntryBlockers.None;
        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        probe.Llm.Calls.Should().BeGreaterThan(0);
        decision.Should().NotBeNull("空いたので新規建ての判断が出る（審査はリスク管理が行う）");
        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Open);
        probe.Forgone.Reports.Should().ContainSingle("1 回目の見送りだけ");
    }

    public static TheoryData<string> CallsLlmCases() =>
    [
        "ロング保有", "ショート保有", "未約定あり", "保有不明", "未約定不明", "照会の失敗（不明）", "照会の例外", "未結線",
        "売りだけ塞がり", "塞がりなし",
    ];

    private static (Probe Probe, FakeBlockers? Blockers) CallsLlm(string name)
    {
        var blocked = new FakeBlockers(LongBlocked(RejectionReason.StoppedOutSameDay));
        return name switch
        {
            "ロング保有" => (Create(new FakeHeld(10), blocked, SellJson), blocked),
            "ショート保有" => (Create(new FakeHeld(-10), blocked, BuyJson), blocked),
            "未約定あり" => (Create(new FakeHeld(0, working: true), blocked), blocked),
            "保有不明" => (Create(new FakeHeld(null), blocked), blocked),
            "未約定不明" => (Create(new FakeHeld(0, working: null), blocked), blocked),
            "照会の失敗（不明）" => (Create(new FakeHeld(0), new FakeBlockers(null)), null),
            "照会の例外" => (Create(new FakeHeld(0), new FakeBlockers(null, throws: true)), null),
            "未結線" => (Create(new FakeHeld(0), blockers: null), null),
            "売りだけ塞がり" => (Create(new FakeHeld(0),
                new FakeBlockers(new EntryBlockers([], [RejectionReason.StoppedOutSameDay]))), null),
            _ => (Create(new FakeHeld(0), new FakeBlockers(EntryBlockers.None)), null),
        };
    }

    // T-10-1792: 省けない状態では LLM を呼ぶ（見送りの事実も出さない）。未約定あり・不明の銘柄では可否を照会もしない。
    // ［2026-10-01 / #1130, IADR-0471 決定 1］保有中（ロング・ショート）の銘柄は照会する（買い増し・売り増しの可否をプロンプトへ渡すため。
    // LLM は必ず呼ぶ。振る舞いは HeldAddOnBlockersTests が固定する）。
    [Theory]
    [MemberData(nameof(CallsLlmCases))]
    public async Task T_10_1792_省けない状態ではLLMを呼ぶ(string name)
    {
        var (probe, blocked) = CallsLlm(name);

        await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        probe.Llm.Calls.Should().BeGreaterThan(0, name);
        probe.Forgone.Reports.Should().BeEmpty(name);
        probe.Skips.Reasons.Should().NotContain(DecisionSkipReason.EntryBlockedByRiskControls, name);
        if (blocked is not null && name is "ロング保有" or "ショート保有")
            blocked.Calls.Should().ContainSingle($"{name}: 保有中は買い増し・売り増しの可否を 1 回照会する（#1130）");
        else if (blocked is not null)
            blocked.Calls.Should().BeEmpty($"{name}: 保有 0・未約定なしが既知でなければ照会もしない");
    }

    // T-10-1792: 🔴 決済の判断は必ず残す。ロング保有の売り・ショート保有の買いは、口が塞がっていても決済として出る。
    [Theory]
    [InlineData(10, SellJson, TradeSide.Sell)]
    [InlineData(-10, BuyJson, TradeSide.Buy)]
    public async Task T_10_1792_保有中の銘柄の決済は通る(int held, string llmOutput, TradeSide side)
    {
        var blocked = new FakeBlockers(new EntryBlockers(
            [RejectionReason.KillSwitchActive, RejectionReason.StoppedOutSameDay], [RejectionReason.KillSwitchActive]));
        var probe = Create(new FakeHeld(held), blocked, llmOutput);

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision.Should().NotBeNull();
        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        decision.Intent.Side.Should().Be(side);
        decision.Intent.Quantity.Should().Be(Math.Abs(held));
    }

    // T-10-1792: 本判断のキャンセルは伝える（不明へ倒して LLM を呼ばない）。
    [Fact]
    public async Task T_10_1792_照会の最中の本判断のキャンセルは伝える()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = Create(new FakeHeld(0), new CancellingBlockers(cts));

        var act = () => probe.Service.DecideAsync(Trigger(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        probe.Llm.Calls.Should().Be(0);
    }

    private sealed class CancellingBlockers(CancellationTokenSource cts) : IEntryBlockersProvider
    {
        public Task<EntryBlockers?> GetAsync(string symbol, Market market, CancellationToken cancellationToken = default)
        {
            cts.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }
}
