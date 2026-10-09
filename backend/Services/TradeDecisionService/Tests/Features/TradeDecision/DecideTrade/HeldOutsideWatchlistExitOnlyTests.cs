extern alias RiskManagementWorker;

using RiskManagementWorker::RiskManagementService.Domain;
using TradeDecisionService.Common.Abstractions;
using TradeDecisionService.Features.TradeDecision;
using TradeDecisionService.Features.TradeDecision.DecideTrade;
using AiStockTrading.Shared.Contracts.Events;
using AiStockTrading.Shared.Contracts.Observability;
using AiStockTrading.Shared.Contracts.Trading;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using AppSvc = TradeDecisionService.Features.TradeDecision.DecideTrade.TradeDecisionAppService;

namespace TradeDecisionService.Tests;

// 🔴 FR-02, FR-04, UC-01, ADR-0051, #1286, IADR-0521 決定 2: 監視銘柄の外の保有銘柄（保有のみ）は出口専用で判断する。
// 決済（ロング保有の Sell・ショート保有の Buy）は従来どおり保有全量の決済として発注意図にし、新規建て（買い増し・売り増し）は
// 発注意図を作らず判断後の見送り（ExitOnlyOpenOutsideWatchlist）にする。監視銘柄の判断（ExitOnly=false）は変えない。
public class HeldOutsideWatchlistExitOnlyTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 15, 0, 0, TimeSpan.Zero);
    private static readonly DailyPolicy Policy = new(new DateOnly(2026, 10, 9), "利確は +5% で手仕舞う");

    private const string BuyJson =
        """{"action":"Buy","rationale":"押し目","referencePrice":1000,"stopLossDistancePerShare":30}""";
    private const string SellJson =
        """{"action":"Sell","rationale":"利確","referencePrice":1000,"stopLossDistancePerShare":30}""";

    private sealed class FakeClock : IClock { public DateTimeOffset UtcNow => Now; }

    private sealed class RecordingLlm(string output) : ILlmCompletionClient
    {
        public List<string> Prompts { get; } = [];

        public Task<string> CompleteAsync(
            string prompt, string? model = null, string? purpose = null, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
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

    // 保有照会（実結線）。未約定の新規建ては「無い」。
    private sealed class FakeHeld(int held) : IHeldPositionProvider
    {
        public bool IsEnabled => true;

        public Task<int?> GetSignedQuantityAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<int?>(held);

        public Task<HeldPosition?> GetPositionAsync(string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<HeldPosition?>(held == 0 ? HeldPosition.None : new HeldPosition(held, 1_000m, held > 0 ? 900m : 1_100m));

        public Task<WorkingEntryOrders?> GetWorkingEntryOrdersAsync(
            string symbol, Market market, CancellationToken ct = default) =>
            Task.FromResult<WorkingEntryOrders?>(WorkingEntryOrders.None);
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

    private sealed record Probe(AppSvc Service, RecordingLlm Llm, RecordingHeldReporter Held, RecordingSkips Skips);

    private static Probe Create(int held, string llmOutput)
    {
        var llm = new RecordingLlm(llmOutput);
        var heldReporter = new RecordingHeldReporter();
        var skips = new RecordingSkips();
        var service = new AppSvc(
            llm, new FakePolicy(), new FakeSizing(), new FakeClock(), NullLogger<AppSvc>.Instance,
            heldPosition: new FakeHeld(held), skipReporter: skips, heldReporter: heldReporter);
        return new Probe(service, llm, heldReporter, skips);
    }

    private static DecisionTrigger Trigger(bool exitOnly) =>
        DecisionTrigger.Scheduled("MSFT", Market.UnitedStates, Now, exitOnly);

    // T-10-2493: 出口専用の判断で LLM が決済を返したら、保有全量の決済として発注意図にする（出口は止めない）。
    [Theory]
    [InlineData(10, SellJson, TradeSide.Sell)]
    [InlineData(-10, BuyJson, TradeSide.Buy)]
    public async Task T_10_2493_出口専用の判断でも決済は保有全量で発注意図になる(int held, string output, TradeSide side)
    {
        var probe = Create(held, output);

        var decision = await probe.Service.DecideAsync(Trigger(exitOnly: true), TestContext.Current.CancellationToken);

        decision.Should().NotBeNull("出口は止めない");
        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Close);
        decision.Intent.Side.Should().Be(side);
        decision.Intent.Quantity.Should().Be(Math.Abs(held));
        probe.Skips.Reasons.Should().BeEmpty();
    }

    // T-10-2494: 出口専用の判断で LLM が新規建て（買い増し・売り増し）を返したら、発注意図を作らず判断後の見送りにする。
    [Theory]
    [InlineData(10, BuyJson)]
    [InlineData(-10, SellJson)]
    public async Task T_10_2494_出口専用の判断で新規建てを返しても発注意図を作らない_否定形(int held, string output)
    {
        var probe = Create(held, output);

        var decision = await probe.Service.DecideAsync(Trigger(exitOnly: true), TestContext.Current.CancellationToken);

        decision.Should().BeNull("監視銘柄の外への新規建ては出さない");
        probe.Skips.Reasons.Should().Equal(DecisionSkipReason.ExitOnlyOpenOutsideWatchlist);
        probe.Held.Reports.Should().ContainSingle()
            .Which.Reason.Should().Be(nameof(DecisionSkipReason.ExitOnlyOpenOutsideWatchlist), "判断後の見送りとして基準値を進める");
    }

    // T-10-2494（判断の間に保有が 0）: 保有が 0 になった出口専用の判断で LLM が買いを返しても、新規建てにしない。
    [Fact]
    public async Task T_10_2494_保有が0になった出口専用の判断で買いを返しても新規建てにしない_否定形()
    {
        var probe = Create(0, BuyJson);

        var decision = await probe.Service.DecideAsync(Trigger(exitOnly: true), TestContext.Current.CancellationToken);

        decision.Should().BeNull();
        probe.Skips.Reasons.Should().Equal(DecisionSkipReason.ExitOnlyOpenOutsideWatchlist);
    }

    // T-10-2495: 監視銘柄の判断（ExitOnly=false）は変えない —— 保有中の買い増しは従来どおり新規建ての発注意図になり、
    // プロンプトに出口専用の行は出ない。出口専用の判断のプロンプトにだけ出口専用の行が出る。
    [Fact]
    public async Task T_10_2495_監視銘柄の判断は買い増しを従来どおり通しプロンプトに出口専用の行を出さない()
    {
        var watched = Create(10, BuyJson);
        var decision = await watched.Service.DecideAsync(Trigger(exitOnly: false), TestContext.Current.CancellationToken);

        decision.Should().NotBeNull("監視銘柄の買い増しは従来どおり");
        decision!.Intent.PositionEffect.Should().Be(PositionEffect.Open);
        watched.Llm.Prompts.Should().OnlyContain(p => !p.Contains(TradeDecisionPromptBuilder.ExitOnlyLine, StringComparison.Ordinal));

        var exitOnly = Create(10, SellJson);
        await exitOnly.Service.DecideAsync(Trigger(exitOnly: true), TestContext.Current.CancellationToken);
        exitOnly.Llm.Prompts.Should().NotBeEmpty()
            .And.OnlyContain(p => p.Contains(TradeDecisionPromptBuilder.ExitOnlyLine, StringComparison.Ordinal));
    }

    // T-10-2495（監視銘柄が不明）: 監視銘柄節が「不明」の判断でも、出口専用の行は出る。
    [Fact]
    public void T_10_2495_監視銘柄が不明でも出口専用の行は出る()
    {
        TradeDecisionPromptBuilder.WatchlistSection(Trigger(exitOnly: true), null)
            .Should().Contain(TradeDecisionPromptBuilder.ExitOnlyLine);
        TradeDecisionPromptBuilder.WatchlistSection(Trigger(exitOnly: false), null)
            .Should().NotContain(TradeDecisionPromptBuilder.ExitOnlyLine);
    }
}
