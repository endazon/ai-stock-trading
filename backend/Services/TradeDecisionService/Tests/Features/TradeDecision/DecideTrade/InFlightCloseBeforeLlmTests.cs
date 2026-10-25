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

// 🔴 FR-04, FR-10, ADR-0050 決定1, #1338, IADR-0535 決定1〜3: **判断由来の決済が処理中のまま建玉を覆い、どの結論も発注にならない銘柄は、
// 一次スクリーニングを含め LLM を呼ばずに見送る。** 是正前は AAPL の Sell 13 株の指値が約定しない間、5.5 分ごとに一次・二次の LLM が同じ決済を
// 34 回判断し、うち 33 回を発注執行が InFlightCloseCoversPosition で見送った（2026-10-13）。
//   - 決済の結論: 発注執行が「覆う」と答えたときだけ発注にならない（ゲートと同じ判定）。覆わない・不明・失敗は LLM を呼ぶ。
//   - 買い増し（処理中の決済と逆方向の判断）: LLM の後で必ず見送られると分かるときだけ省く。分からなければ LLM を呼ぶ（照会もしない）。
public class InFlightCloseBeforeLlmTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 13, 42, 0, TimeSpan.Zero); // 2026-10-13 US セッション
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 10, 13), "利確を優先する方針");

    private const decimal Equity = 100_000m;
    private const decimal AaplPrice = 347.93m;
    private const string AaplSellJson =
        """{"action":"Sell","rationale":"含み益が +3% を超えたので利確","referencePrice":347.93,"stopLossDistancePerShare":7}""";

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

    private sealed class FakeSizing(SizingContext context) : ISizingContextProvider
    {
        public Task<SizingContext> GetContextAsync(CancellationToken ct = default) => Task.FromResult(context);
    }

    private sealed class FakeCurrentPrice(decimal price) : ICurrentPriceProvider
    {
        public bool IsEnabled => true;

        public Task<CurrentPriceReading?> GetCurrentPriceAsync(DecisionTrigger trigger, CancellationToken ct = default) =>
            Task.FromResult<CurrentPriceReading?>(new CurrentPriceReading(price, IntradayPriceContext.Unknown));
    }

    // 保有照会（実結線）。workingUnknown が true なら未約定の新規建ては不明（null）。
    private sealed class FakeHeld(int held, bool workingUnknown = false) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<int?>(held);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<HeldPosition?>(held == 0 ? HeldPosition.None : new HeldPosition(held, 330m, 320m));

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
            string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<WorkingEntryOrders?>(workingUnknown ? null : WorkingEntryOrders.None);
    }

    private sealed class FakeBlockers(params RejectionReason[] longSide) : IEntryBlockersProvider
    {
        public Task<EntryBlockers?> GetAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<EntryBlockers?>(new EntryBlockers(longSide, [RejectionReason.StoppedOutSameDay]));
    }

    private sealed class StaleFx : IFxRateProvider
    {
        public Task<decimal?> GetRateToBaseAsync(Market market, CancellationToken ct = default) => Task.FromResult<decimal?>(1m);

        public Task<FxRateReading?> GetReadingAsync(Market market, CancellationToken ct = default) =>
            Task.FromResult<FxRateReading?>(new FxRateReading(
                new FxRate(MarketCurrency.Of(market), MarketCurrency.Base, 1m, Now.AddDays(-40)), FxRateFreshness.Expired));
    }

    // 処理中の決済の照会口の偽物。answer が null なら不明、throws なら例外。
    private sealed class FakeInFlight(InFlightCloseCoverage? answer, bool throws = false) : IInFlightCloseProvider
    {
        public List<(string Symbol, TradeSide CloseSide)> Calls { get; } = [];

        public Task<InFlightCloseCoverage?> GetAsync(
            string symbol, Market market, TradeSide closeSide, CancellationToken cancellationToken = default)
        {
            Calls.Add((symbol, closeSide));
            return throws
                ? Task.FromException<InFlightCloseCoverage?>(new HttpRequestException("order-execution down"))
                : Task.FromResult(answer);
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

    private sealed class RecordingSkips : IDecisionSkipReporter
    {
        public List<DecisionSkipReason> Reasons { get; } = [];

        public void Report(string trigger, DecisionSkipReason reason) => Reasons.Add(reason);
    }

    private sealed record Probe(AppSvc Service, FixedLlm Llm, FakeInFlight InFlight, RecordingForgone Forgone, RecordingSkips Skips);

    private static InFlightCloseCoverage Covered(int quantity = 13) => new(true, quantity, ["ORD-AAPL-1"]);

    // 既定の残枠は十分（買い増しは発注になり得る）。
    private static SizingContext Context(decimal? stageRemaining = 50_000m, decimal? dailyRemaining = 50_000m) =>
        new(Equity, stageRemaining, dailyRemaining, 0, 0m, BrokerProvider.MoomooSimulate, TradingDefaults.CreateRiskLimits());

    // 🔴 #1434, IADR-0572 決定2: 台帳（リスク管理の open-positions）が約定の射影より遅れている状態。巡回の冒頭（プロンプト）は保有中を返し、
    // LLM の後の読み直し（Sell の関所）は追いついた 0 を返す（HOOD 2026-10-21 の時系列）。
    private sealed class LaggingLedgerHeld(int before) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<int?>(0);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<HeldPosition?>(new HeldPosition(before, 330m, 320m));

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<WorkingEntryOrders?>(WorkingEntryOrders.None);
    }

    private static Probe Create(
        FakeInFlight inFlight, int held = 13, IEntryBlockersProvider? blockers = null, SizingContext? context = null,
        bool workingUnknown = false, IFxRateProvider? fx = null, string llmOutput = AaplSellJson,
        IHeldPositionProvider? heldPosition = null)
    {
        var llm = new FixedLlm(llmOutput);
        var forgone = new RecordingForgone();
        var skips = new RecordingSkips();
        var service = new AppSvc(
            llm, new FakePolicy(), new FakeSizing(context ?? Context()), new FakeClock(), NullLogger<AppSvc>.Instance,
            options: new DecisionOrchestrationOptions { EnableScreening = true },
            currentPrice: new FakeCurrentPrice(AaplPrice), heldPosition: heldPosition ?? new FakeHeld(held, workingUnknown), fxRate: fx,
            skipReporter: skips, forgoneReporter: forgone, entryBlockers: blockers, inFlightCloses: inFlight);
        return new Probe(service, llm, inFlight, forgone, skips);
    }

    private static DecisionTrigger Trigger(bool exitOnly = false) =>
        DecisionTrigger.Scheduled("AAPL", Market.UnitedStates, Now, exitOnly: exitOnly);

    private static void ShouldBeForgoneBeforeLlm(Probe probe)
    {
        probe.Llm.Calls.Should().Be(0, "一次スクリーニングを含め、結論が発注にならない LLM 呼び出しをしない（IADR-0535 決定1）");
        probe.Forgone.Reports.Should().ContainSingle()
            .Which.Reason.Should().Be(DecisionForgoneBeforeLlmReason.InFlightCloseCoversPosition);
        probe.Skips.Reasons.Should().Equal(DecisionSkipReason.InFlightCloseCoversPosition);
    }

    // T-10-2642: 🔴 issue の形（AAPL）。ロング 13 株・処理中の Sell 13 株が建玉を覆い・買い増しは審査で必ず拒否される → LLM を呼ばない。
    [Fact]
    public async Task T_10_2642_処理中の決済が建玉を覆い買い増しも必ず見送られる銘柄はLLMを呼ばずに見送る()
    {
        var probe = Create(new FakeInFlight(Covered()), blockers: new FakeBlockers(RejectionReason.MaxPositionsExceeded));

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision.Should().BeNull();
        ShouldBeForgoneBeforeLlm(probe);
        probe.InFlight.Calls.Should().Equal([("AAPL", TradeSide.Sell)], "ロングの決済の方向（Sell）だけを照会する");
    }

    // T-10-2643: 🔴 逆方向の判断（買い増し）を塞がない。買い増しが発注になり得るなら、覆っていても LLM を呼ぶ（照会もしない）。
    [Fact]
    public async Task T_10_2643_買い増しが発注になり得るなら覆っていてもLLMを呼び照会もしない()
    {
        var probe = Create(new FakeInFlight(Covered()), blockers: new FakeBlockers());

        await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        probe.Llm.Calls.Should().BeGreaterThan(0, "買い増し（処理中の決済と逆方向の判断）は発注になり得る");
        probe.InFlight.Calls.Should().BeEmpty("買い増しの判定が成り立たなければ、ブローカーへの往復を伴う照会をしない");
        probe.Forgone.Reports.Should().BeEmpty();
    }

    // T-10-2644: 🔴 覆わない（部分＝建玉を超える残りの決済が出せる）・不明・照会の失敗は LLM を呼ぶ（決済〔損切り〕の判断を止めない）。
    [Theory]
    [InlineData("not-covered")]
    [InlineData("unknown")]
    [InlineData("throws")]
    public async Task T_10_2644_覆わない不明照会の失敗ではLLMを呼ぶ(string answer)
    {
        var inFlight = answer switch
        {
            "not-covered" => new FakeInFlight(new InFlightCloseCoverage(false, 5, [])),
            "unknown" => new FakeInFlight(null),
            _ => new FakeInFlight(null, throws: true),
        };
        var probe = Create(inFlight, blockers: new FakeBlockers(RejectionReason.MaxPositionsExceeded));

        await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        probe.Llm.Calls.Should().BeGreaterThan(0, answer);
        probe.InFlight.Calls.Should().ContainSingle();
        probe.Forgone.Reports.Should().BeEmpty();
    }

    // T-10-2645: 買い増しが LLM の後で必ず見送られる各条件（LLM の後の見送りと同じ条件・同じ関数）で省く。ショート（Buy の処理中）も対称。
    [Theory]
    [InlineData("exit-only")]
    [InlineData("fx-expired")]
    [InlineData("working-unknown")]
    [InlineData("below-one-share")]
    [InlineData("below-minimum-notional")]
    [InlineData("short")]
    public async Task T_10_2645_買い増しが必ず見送られる各条件で省きショートも対称(string condition)
    {
        var inFlight = new FakeInFlight(Covered());
        var probe = condition switch
        {
            "exit-only" => Create(inFlight, blockers: new FakeBlockers()),
            "fx-expired" => Create(inFlight, blockers: new FakeBlockers(), fx: new StaleFx()),
            "working-unknown" => Create(inFlight, blockers: new FakeBlockers(), workingUnknown: true),
            // 残枠 300 < 現在値 347.93 × 1 株（最小の名目額 1,000 にも届かないが、1 株の判定が先に成り立つ）。
            "below-one-share" => Create(inFlight, blockers: new FakeBlockers(), context: Context(stageRemaining: 300m)),
            // 残枠 900（1 株は買える）< 最小の名目額 1,000（equity 100,000 × 1%）。
            "below-minimum-notional" => Create(inFlight, blockers: new FakeBlockers(), context: Context(dailyRemaining: 900m)),
            _ => Create(inFlight, held: -10, blockers: new FakeBlockers(RejectionReason.MaxPositionsExceeded)),
        };
        // ショートの売り増しは ShortSide（StoppedOutSameDay）で拒否される。
        var decision = await probe.Service.DecideAsync(Trigger(exitOnly: condition == "exit-only"), TestContext.Current.CancellationToken);

        decision.Should().BeNull();
        ShouldBeForgoneBeforeLlm(probe);
        probe.InFlight.Calls.Should().Equal([("AAPL", condition == "short" ? TradeSide.Buy : TradeSide.Sell)]);
    }

    // T-10-2644（陰性）: 保有 0 の銘柄では照会しない（処理中の決済は保有中の銘柄の決済の結論にしか効かない）。
    [Fact]
    public async Task T_10_2644_保有0の銘柄では処理中の決済を照会しない()
    {
        var probe = Create(new FakeInFlight(Covered()), held: 0, blockers: new FakeBlockers(), llmOutput:
            """{"action":"Hold","rationale":"様子見","referencePrice":347.93,"stopLossDistancePerShare":7}""");

        await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        probe.InFlight.Calls.Should().BeEmpty();
        probe.Forgone.Reports.Should().BeEmpty();
    }

    // 🔴 T-10-2991, FR-04, FR-10, #1434, IADR-0572 決定2: **判断由来の決済が約定した直後の巡回**（台帳はまだ保有中・発注執行はブローカーの決済方向の
    // 建玉 0 を観測し NotCovered）では、出口の判断の LLM を呼ばない。買い増しは当日の判断由来の決済で必ず拒否される（DecisionExitSameDay）。
    // 是正前は LLM（一次・二次）を呼び、LLM の後の関所で台帳が追いついた held=0 により NakedShortOpen で見送っていた。
    [Fact]
    public async Task T_10_2991_決済の約定直後で建玉0の銘柄は出口の判断のLLMを呼ばない()
    {
        var probe = Create(
            new FakeInFlight(new InFlightCloseCoverage(false, 0, [], BrokerClosableQuantity: 0)),
            blockers: new FakeBlockers(RejectionReason.DecisionExitSameDay),
            heldPosition: new LaggingLedgerHeld(2339));

        var decision = await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        decision.Should().BeNull();
        probe.Llm.Calls.Should().Be(0, "どの結論も発注にならない（決済は発注執行の決済のゲートが建玉なしで見送る）");
        probe.Forgone.Reports.Should().ContainSingle()
            .Which.Reason.Should().Be(DecisionForgoneBeforeLlmReason.BrokerPositionClosed);
        probe.Skips.Reasons.Should().Equal(DecisionSkipReason.BrokerPositionClosed);
        probe.InFlight.Calls.Should().Equal([("AAPL", TradeSide.Sell)]);
    }

    // 🔴 T-10-2992, FR-04, FR-10, #1434, IADR-0572 決定2: 建玉が正（決済が出せる）・建玉が不明（照会していない・旧い送り手）なら従来どおり LLM を呼ぶ
    // （決済〔損切り〕の判断を止める向きへは倒さない）。買い増しが発注になり得るなら照会もしない（建玉 0 を返す照会口でも LLM を呼ぶ）。
    [Theory]
    [InlineData("closable-positive")]
    [InlineData("closable-unknown")]
    [InlineData("add-on-possible")]
    public async Task T_10_2992_建玉が正か不明か買い増しが発注になり得るならLLMを呼ぶ(string shape)
    {
        var probe = shape switch
        {
            "closable-positive" => Create(
                new FakeInFlight(new InFlightCloseCoverage(false, 0, [], BrokerClosableQuantity: 13)),
                blockers: new FakeBlockers(RejectionReason.DecisionExitSameDay)),
            "closable-unknown" => Create(
                new FakeInFlight(new InFlightCloseCoverage(false, 0, [], BrokerClosableQuantity: null)),
                blockers: new FakeBlockers(RejectionReason.DecisionExitSameDay)),
            _ => Create(
                new FakeInFlight(new InFlightCloseCoverage(false, 0, [], BrokerClosableQuantity: 0)),
                blockers: new FakeBlockers()),
        };

        await probe.Service.DecideAsync(Trigger(), TestContext.Current.CancellationToken);

        probe.Llm.Calls.Should().BeGreaterThan(0, shape);
        probe.Forgone.Reports.Should().BeEmpty();
        if (shape == "add-on-possible")
            probe.InFlight.Calls.Should().BeEmpty("買い増しの判定が成り立たなければ照会しない（IADR-0535 の前段は不変）");
    }
}
